#!/usr/bin/env bash
# Build PortMaster Desktop as a Linux AppImage.
# All tools are kept locally — no sudo required.
#
# First run:  ./build-linux-appimage.sh --setup   (downloads .NET 9 + appimagetool)
# Build:      ./build-linux-appimage.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TOOLS_DIR="$SCRIPT_DIR/tools"
DOTNET_DIR="$TOOLS_DIR/dotnet9"
DOTNET="$DOTNET_DIR/dotnet"
APPIMAGETOOL="$TOOLS_DIR/appimagetool"

APP_NAME="PortMasterDesktop"
PROJECT="$SCRIPT_DIR/PortMasterDesktop/PortMasterDesktop.csproj"
APP_VERSION=$(grep -oP '(?<=<Version>)[^<]+' "$PROJECT" 2>/dev/null || echo "0.1.0")
PUBLISH_DIR="$SCRIPT_DIR/publish/linux"
APPDIR="$SCRIPT_DIR/AppDir"

# ─── setup mode ───────────────────────────────────────────────────────────────
if [[ "${1:-}" == "--setup" ]]; then
    echo "▶ Installing .NET 9 SDK to $DOTNET_DIR …"
    mkdir -p "$DOTNET_DIR"
    curl -fsSL https://dot.net/v1/dotnet-install.sh \
        | bash -s -- --channel 9.0 --install-dir "$DOTNET_DIR"

    echo "▶ Downloading appimagetool…"
    mkdir -p "$TOOLS_DIR"
    curl -fsSL -o "$APPIMAGETOOL" \
        "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x "$APPIMAGETOOL"

    echo "✅ Setup complete. Run ./build-linux-appimage.sh to build."
    exit 0
fi

# ─── sanity checks ────────────────────────────────────────────────────────────
if [[ ! -x "$DOTNET" ]]; then
    echo "❌ .NET not found at $DOTNET"
    echo "   Run: ./build-linux-appimage.sh --setup"
    exit 1
fi

if [[ ! -x "$APPIMAGETOOL" ]]; then
    echo "❌ appimagetool not found at $APPIMAGETOOL"
    echo "   Run: ./build-linux-appimage.sh --setup"
    exit 1
fi

echo "▶ .NET version: $("$DOTNET" --version)"

# ─── build ────────────────────────────────────────────────────────────────────
echo "▶ Publishing $APP_NAME $APP_VERSION (net9.0, linux-x64)…"
"$DOTNET" publish "$PROJECT" \
    --framework net9.0 \
    --runtime linux-x64 \
    --self-contained true \
    --configuration Release \
    --output "$PUBLISH_DIR" \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true

# ─── AppDir ────────────────────────────────────────────────────────────────────
echo "▶ Creating AppDir…"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin"
mkdir -p "$APPDIR/usr/lib/$APP_NAME"
mkdir -p "$APPDIR/usr/share/applications"
mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR/"* "$APPDIR/usr/lib/$APP_NAME/"

# AppRun
cat > "$APPDIR/AppRun" << 'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/usr/lib/PortMasterDesktop${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$HERE/usr/lib/PortMasterDesktop/PortMasterDesktop" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"

# Desktop entry (must be in root AND in usr/share/applications)
cat > "$APPDIR/$APP_NAME.desktop" << DESKTOP
[Desktop Entry]
Type=Application
Name=PortMaster Desktop
GenericName=Game Port Installer
Comment=Install PortMaster ports from your game library
Exec=PortMasterDesktop
Icon=portmasterdesktop
Terminal=false
Categories=Game;Utility;
Keywords=PortMaster;games;ports;handheld;
MimeType=x-scheme-handler/portmasterdesktop;
DESKTOP
cp "$APPDIR/$APP_NAME.desktop" "$APPDIR/usr/share/applications/"

# Icon — prefer the pre-converted PNG from the PortMaster logo SVG
ICON_SRC="$SCRIPT_DIR/PortMasterDesktop/Resources/AppIcon/portmaster.png"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "$APPDIR/portmasterdesktop.png"
elif command -v inkscape &>/dev/null; then
    inkscape "$SCRIPT_DIR/PortMasterDesktop/Resources/AppIcon/portmaster.svg" \
        --export-filename="$APPDIR/portmasterdesktop.png" \
        --export-width=256 --export-height=256 2>/dev/null
elif command -v rsvg-convert &>/dev/null; then
    rsvg-convert -w 256 -h 256 \
        "$SCRIPT_DIR/PortMasterDesktop/Resources/AppIcon/portmaster.svg" \
        -o "$APPDIR/portmasterdesktop.png"
else
    echo "⚠  Using placeholder icon"
    printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x02\x00\x00\x00\x90wS\xde\x00\x00\x00\x0cIDATx\x9cc\xac\x8c\xfc?\x00\x00\x04\x01\x01\x00u\x01\xaa\xce\x00\x00\x00\x00IEND\xaeB`\x82' \
        > "$APPDIR/portmasterdesktop.png"
fi
cp "$APPDIR/portmasterdesktop.png" \
   "$APPDIR/usr/share/icons/hicolor/256x256/apps/portmasterdesktop.png"

# ─── package ──────────────────────────────────────────────────────────────────
echo "▶ Packaging AppImage…"
OUTPUT="$SCRIPT_DIR/${APP_NAME}-${APP_VERSION}-x86_64.AppImage"
ARCH=x86_64 "$APPIMAGETOOL" "$APPDIR" "$OUTPUT"

echo ""
echo "✅  Built: $OUTPUT"
echo ""
echo "To register the OAuth callback URL scheme:"
echo "  xdg-mime default $APP_NAME.desktop x-scheme-handler/portmasterdesktop"
