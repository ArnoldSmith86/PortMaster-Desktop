# Changelog

All notable user-facing changes to PortMaster Desktop are documented here. This
project follows [Semantic Versioning](https://semver.org/) and the format is
inspired by [Keep a Changelog](https://keepachangelog.com/).

## 0.2.2 — 2026-05-02

### Added
- **Application log file** written on every run under
  `portmaster-desktop/logs/` (last 9 files kept). Logs system info, OS
  version, every HTTP call with timing and response size, which SD card
  candidates were checked and why each was accepted or rejected, which stores
  are authenticated, and per-store game counts. Credentials, tokens, and API
  keys are never written — itch.io path-embedded keys are redacted, all query
  strings stripped, auth headers not logged.
- **Custom destination path** in Settings — type any path that ends in
  `/ports` to override auto-detection (e.g. when the card is mounted at an
  unusual location). Takes effect as soon as Settings are closed.
- **`ports/PortMaster` layout** is now detected in addition to `roms/ports`,
  covering devices where the ports folder sits directly at the card root.
- **Windows installer** (`*-windows-x64-setup.exe`) — installs to
  `%ProgramFiles%\PortMaster Desktop\`, creates a Start Menu shortcut, and
  registers with Add/Remove Programs. The plain EXE is still offered as
  `*-windows-x64-portable.exe` for users who prefer no install.
- **Windows taskbar / Explorer icon** — the exe now embeds the PortMaster
  icon at all standard sizes (16 → 256 px).

### Fixed
- SD card detection on Windows was not finding cards whose ports directory
  follows the `ports/PortMaster` layout.

## 0.2.1 — 2026-05-01

### Fixed
- Loading screen no longer shows a duplicate "🎮 Connect your stores…" panel
  bleeding through the dim overlay while the library is loading.

### Changed
- Filter tabs renamed to **All Store Games**, **Available Ports**, and
  **Ready to Run Ports** so the difference between filtered store games and
  PortMaster ports is explicit at a glance.

## 0.2.0 — 2026-04-29

### Added
- **PortMaster Images toggle** in Settings — swap each game's cover for the
  matching port screenshot (4:3) and get larger tiles to go with them.
- **Ready to Run Ports** filter tab — every PortMaster port that needs no
  external game files, always shown with port screenshots and large tiles.
- **Get Cover** button per game to fetch a portrait cover from SteamGridDB
  on demand.
- **Last error** indicator on each store account in Settings — when a store
  fails (rate-limited, network down, expired session, …) you now see exactly
  what went wrong instead of a silent miss.
- **Live download progress** in the bottom status bar for the PortMaster
  screenshots zip — real percentage, MB counter, and an extraction phase.

### Changed
- **Startup is back to instant.** The library appears immediately instead of
  stalling for several seconds while every store's account name was verified
  over HTTP. Settings refreshes accounts in the background when you open it.
- **One flaky store no longer breaks everything.** If a store returns
  HTTP 429 / times out / errors during the load, that single store is skipped
  for an hour and every other store keeps working. SteamGridDB also backs off
  for an hour on rate-limit instead of failing the whole enrichment pass.
- Tiles are 1.5× bigger whenever PortMaster screenshots are showing, including
  the Ready to Run tab.
- Settings overlay is full-screen with a clean 1:1 split for accounts vs
  display options.
- Tile widths distribute evenly across the window with no blank space at the
  right edge, no jumpy resize behaviour.

### Fixed
- PortMaster screenshots actually download for the alternative view and the
  Ready to Run tab. Previously an empty cache directory left over from an
  interrupted attempt was masking the real download.
- Manually-set covers (via Get Cover) persist across app restarts and are no
  longer wiped by Refresh.
- SteamGridDB stops skipping landscape covers it should have replaced.
- Screenshot-fallback path correctly falls back to the remote screenshot URL
  when the local file isn't present yet.

## 0.1.0 — initial release

- Multi-store login (Steam local, GOG, Epic, itch.io, Amazon, Humble Bundle).
- PortMaster catalog browsing with cover art and per-port detail pages.
- One-click install of port ZIPs to a detected SD card with `roms/ports/`.
- Game-file copy automation from local Steam installs.
- Steam depot download via the local Steam console for games that need it.
- SteamGridDB cover enrichment for games whose store covers aren't portrait.
