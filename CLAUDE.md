# PortMaster Desktop — working notes

Avalonia/.NET 9 desktop app that bridges store libraries (Steam/GOG/Epic/Itch/Amazon/Humble) with PortMaster ports for ARM64 handhelds. The user runs Linux Mint and wants tight feedback loops — closed-loop CLI testing is encouraged before reporting work as done.

## Build & test

`dotnet` is **not** on PATH. Use the bundled SDK at `tools/dotnet9/dotnet`. First-time setup downloads it: `./build-linux-appimage.sh --setup`.

- **Build (debug):** `tools/dotnet9/dotnet build -c Debug --nologo -v q` from repo root or `PortMasterDesktop/`
- **AppImage:** `./build-linux-appimage.sh` from repo root → `PortMasterDesktop-<version>-x86_64.AppImage`
- **Version:** bump `<Version>` in `PortMasterDesktop/PortMasterDesktop.csproj`; the build script reads it via grep.

### CLI test mode

`Program.cs` has a substantial test harness invoked with `--test [flags]`. Use this for closed-loop validation without launching the GUI. Useful flags (see `RunTestsAsync` for the full list):

- `--partition` — detect SD card / `roms/ports/`
- `--ports` — fetch + parse PortMaster catalog
- `--steam` — read local Steam library
- `--fullmatch` — full LoadAsync cycle (libraries × catalog matching)
- `--portmaster-images` — download & verify the screenshot zip
- `--rtr` — Ready to Run filter checks
- `--installtest`, `--gogtest`, `--itchtest`, `--epictest`, `--humbletest` — store-specific flows
- `--refresh` (modifier) — bypass caches

Headless GUI launches don't show the UI but exercise the code path: `tools/dotnet9/dotnet PortMasterDesktop/bin/Debug/net9.0/PortMasterDesktop.dll &` and `kill` after a few seconds.

## Architecture pointers

- `PortMaster/PortMasterClient.cs` — the only thing that talks to PortMaster infrastructure (catalog, runtime catalog, image CDN, port ZIP downloads).
- `Stores/` — one `IGameStore` per platform; `BaseGameStore` provides shared HTTP + credential storage. `LocalSteamStore` reads VDF files (no auth); others use OAuth or session cookies stored under `~/.local/share/portmaster-desktop/creds/`.
- `Services/LibraryService.cs` — orchestrates catalog × per-store libraries × `GameFileInstructions.json` to build `GameMatch` list.
- `Services/InstallService.cs` — three-phase install: source verify → download to temp → copy to SD. Never writes to SD until everything's confirmed downloadable.
- `Services/CacheService.cs` — file-based JSON + image cache under `~/.local/share/portmaster-desktop/cache/`. Never auto-expires; only manual Refresh invalidates.
- `PortMaster/GameFileInstructions.json` — embedded resource with per-port store URLs, Steam depot info, and copy_all/copy_dir/copy_file steps. Schema in `Services/InstallService.cs` private classes.

## Cache layout (Linux)

- `~/.local/share/portmaster-desktop/cache/` — JSON: `portmaster_catalog`, `steam_local_library`, `gog_library`, etc., plus image dimension cache (`coverdim_*`) and SteamGridDB lookup cache (`sgdb_*`)
- `~/.local/share/portmaster-desktop/cache/portmaster_images/` — extracted `*.screenshot.{png,jpg}` files (~1300, ~83 MB zip)
- `~/.local/share/portmaster-desktop/cache/gamefiles/<port>/` — downloaded game files (depot/installer payloads)
- `~/.local/share/PortMasterDesktop/ImageCache/` — **note capitalisation** — Avalonia AsyncImageLoader's disk cache, keyed by MD5(url). `SteamGridDbService` writes here too so AsyncImageLoader can serve covers without re-downloading.
- `~/.local/share/portmaster-desktop/creds/` — store credentials (cleartext, owner-only mode)

## MVVM conventions

- CommunityToolkit.Mvvm `[ObservableProperty]` source generators. Use `[NotifyPropertyChangedFor(...)]` for dependent computed properties.
- Cross-thread updates: `Avalonia.Threading.Dispatcher.UIThread.Post(() => ...)`. Background tasks must marshal property writes to the UI thread when bindings depend on them.
- DI registered in `App.axaml.cs`. Most services are singletons; ViewModels are transient.

## Performance traps to avoid

- **Don't put HTTP calls on the startup path.** `MainViewModel.LoadAsync` deliberately uses `SettingsViewModel.LoadAsync` (cache-only) and defers `RefreshAccountsAsync` (per-store HTTP, e.g. `GogStore.GetAccountNameAsync` hits `embed.gog.com`) to `OpenSettings`. The 30s `BaseGameStore.Http` timeout means a flaky store will otherwise stall startup for tens of seconds.
- **Cache-presence checks should look at actual files**, not just `Directory.Exists`. Empty cache directories from interrupted downloads will silently suppress retries. See `PortMasterImagesService.HasCachedImages()`.
- **Background work needs a global progress channel.** `MainViewModel` exposes `IsBackgroundTaskActive` + `BackgroundTaskMessage`; the no-selection status bar in `MainWindow.axaml` shows them. Use `EnsurePortMasterImagesAsync` as the template for new background fetches.

## Secrets

- `secrets.env` (gitignored) holds `STEAMGRIDDB_API_KEY`. The csproj's `GenerateSecrets` MSBuild target writes `obj/Secrets.g.cs` from the env var at compile time. If SteamGridDB enrichment is silently no-oping, check the env var is exported.

## Workflow expectations

- Don't commit unless explicitly asked. When asked, recent commits use a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer.
- For UI changes, validate with the CLI test mode before claiming done — the user has called this out as a non-negotiable.
- Don't blindly trust `MEMORY.md` claims about file:line locations or behaviour; verify against current code, especially for an evolving project like this one.
