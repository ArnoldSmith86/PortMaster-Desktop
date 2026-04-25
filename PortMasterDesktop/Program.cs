using Avalonia;
using PortMasterDesktop.Models;
using PortMasterDesktop.PortMaster;
using PortMasterDesktop.Services;
using PortMasterDesktop.Stores;

namespace PortMasterDesktop;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--test") || args.Contains("--installtest"))
        {
            RunTestsAsync(args).GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // ── CLI test mode ─────────────────────────────────────────────────────────
    // Usage: PortMasterDesktop --test [--refresh] [--steam] [--partition] [--ports] [--installtest]

    private static async Task RunTestsAsync(string[] args)
    {
        bool all = !args.Any(a => a.StartsWith("--") && a != "--test" && a != "--refresh");
        bool forceRefresh = args.Contains("--refresh");

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== PortMaster Desktop CLI Test Mode ===");
        Console.ResetColor();

        var cache = new CacheService();

        // ── Partition detection ───────────────────────────────────────────────
        if (all || args.Contains("--partition"))
        {
            Console.WriteLine("\n[Partitions]");
            var partSvc = new PartitionService();
            var parts = partSvc.Detect();
            if (parts.Count == 0)
            {
                Warn("  No partition with roms/ports/ found.");
            }
            else
            {
                foreach (var p in parts)
                {
                    var writeStatus = p.CanWrite ? "writable" : "⚠ no write permission";
                    Ok($"  {p.MountPoint}  ({p.FreeSpace} free / {p.TotalSpace} total)  fs={p.FileSystem}  {writeStatus}");
                }
            }
        }

        // ── PortMaster catalog ────────────────────────────────────────────────
        if (all || args.Contains("--ports"))
        {
            Console.WriteLine("\n[PortMaster Catalog]");
            try
            {
                var pm = new PortMasterClient(cache);
                Action<string> progress = msg => Console.Write($"\r  {msg,-60}");
                var ports = await pm.GetPortsAsync(forceRefresh, progress);
                Console.WriteLine();
                var rtr = ports.Count(p => p.Attr.Rtr);
                var needsFiles = ports.Count(p => !p.Attr.Rtr && p.Attr.Store.Count > 0);
                Ok($"  {ports.Count} ports  •  {rtr} RTR (free-to-play)  •  {needsFiles} need game files");

                // Sample port
                var first = ports.FirstOrDefault();
                if (first != null)
                    Console.WriteLine($"  Sample: {first.Attr.Title} ({first.Name})  size={first.Size / 1024 / 1024} MB  url={first.DownloadUrl[..Math.Min(60, first.DownloadUrl.Length)]}…");
            }
            catch (Exception ex) { Err($"  Error: {ex.Message}"); }
        }

        // ── Steam (local) ─────────────────────────────────────────────────────
        if (all || args.Contains("--steam"))
        {
            Console.WriteLine("\n[Steam (local)]");
            var steamRoot = LocalSteamStore.FindSteamRoot();
            if (steamRoot == null)
            {
                Warn("  No local Steam installation found.");
            }
            else
            {
                Ok($"  Steam root: {steamRoot}");
                var store = new LocalSteamStore(cache);
                try
                {
                    var lib = await store.GetLibraryAsync();
                    Ok($"  Library: {lib.Count} games ({lib.Count(g => g.IsInstalled)} installed)");
                    var sample = lib.Take(5).Select(g => g.Title);
                    Console.WriteLine($"  Sample: {string.Join(", ", sample)}");
                }
                catch (Exception ex) { Err($"  Error: {ex.Message}"); }
            }
        }

        // ── GOG ───────────────────────────────────────────────────────────────
        if (all || args.Contains("--gog"))
        {
            Console.WriteLine("\n[GOG]");
            var gog = new GogStore(cache);
            if (await gog.IsAuthenticatedAsync())
            {
                try
                {
                    var lib = await gog.GetLibraryAsync();
                    Ok($"  Library: {lib.Count} games");
                    foreach (var g in lib)
                        Console.WriteLine($"    {g.Title}  (id={g.Id}, installed={g.IsInstalled}, size={g.InstallSizeBytes})");
                }
                catch (Exception ex) { Err($"  Error: {ex.Message}"); }
            }
            else Warn("  Not authenticated (login in Settings).");
        }

        // ── GOG download test: Don't Starve Linux installer ───────────────────
        if (args.Contains("--gogtest"))
        {
            Console.WriteLine("\n[GOG Download: Don't Starve Linux Installer]");
            const string DontStarveId = "1207659210";

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "portmaster-desktop", "cache", "gamefiles", "dontstarve");

            var gog = new GogStore(cache);
            if (!await gog.IsAuthenticatedAsync())
            {
                Warn("  Not authenticated with GOG. Log in via Settings.");
            }
            else
            {
                var existingFiles = Directory.Exists(destDir)
                    ? Directory.GetFiles(destDir, "*", SearchOption.AllDirectories) : [];
                if (existingFiles.Length > 0)
                {
                    Ok($"  Already downloaded: {existingFiles.Length} file(s) in {destDir}");
                    foreach (var f in existingFiles)
                        Console.WriteLine($"    {Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024 / 1024.0:F1} MB)");
                }
                else
                {
                    Console.WriteLine($"  Downloading to {destDir} …");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IProgress<(string msg, double frac)> prog = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg,-70} [{r.frac * 100:F0}%]  "));

                    var err = await gog.DownloadInstallerAsync(DontStarveId, destDir, prog);
                    Console.WriteLine();
                    sw.Stop();
                    if (err != null) Err($"  {err}");
                    else
                    {
                        Ok($"  Done in {sw.Elapsed.TotalSeconds:F1}s → {destDir}");
                        foreach (var f in Directory.GetFiles(destDir))
                            Console.WriteLine($"    {Path.GetFileName(f)}  ({new FileInfo(f).Length / 1024 / 1024.0:F1} MB)");
                    }
                }
            }
        }

        // ── itch.io download test: Wand Wars Linux ────────────────────────────
        if (args.Contains("--itchtest"))
        {
            Console.WriteLine("\n[itch.io Download: Wand Wars Linux]");
            const long WandWarsGameId  = 86929;
            const long WandWarsKeyId   = 3564940;   // user's purchase/download key
            const long WandWarsUploadId = 1183507;  // Linux upload ID
            const string WandWarsFilename = "Wand Wars Linux.zip";
            const long WandWarsSize = 63870350;

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "portmaster-desktop", "cache", "gamefiles", "wandwars");
            var destPath = Path.Combine(destDir, WandWarsFilename);

            var itch = new ItchStore(cache);
            if (!await itch.IsAuthenticatedAsync())
            {
                Warn("  Not authenticated with itch.io. Add an API key in Settings.");
            }
            else
            {
                // Show available uploads first
                Console.WriteLine("  Fetching upload list…");
                var (uploads, uploadsErr) = await itch.GetUploadsAsync(WandWarsGameId, WandWarsKeyId);
                if (uploadsErr != null) { Err($"  {uploadsErr}"); }
                else
                {
                    foreach (var u in uploads)
                        Console.WriteLine($"    [{u.Id}] {u.Filename}  ({u.Size / 1024 / 1024.0:F1} MB)  linux={u.Linux}  demo={u.Demo}");
                }

                if (File.Exists(destPath))
                {
                    Ok($"  Already cached: {destPath}  ({new FileInfo(destPath).Length / 1024 / 1024.0:F1} MB)");
                }
                else
                {
                    Console.WriteLine($"\n  Downloading Linux build to {destPath} …");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IProgress<(string msg, double frac)> prog = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg,-70} [{r.frac * 100:F0}%]  "));

                    var err = await itch.DownloadUploadAsync(
                        WandWarsUploadId, WandWarsKeyId, destPath, WandWarsSize, prog);
                    Console.WriteLine();
                    sw.Stop();
                    if (err != null) Err($"  {err}");
                    else
                    {
                        var fi = new FileInfo(destPath);
                        Ok($"  Done in {sw.Elapsed.TotalSeconds:F1}s  ({fi.Length / 1024 / 1024.0:F1} MB) → {destPath}");
                    }
                }
            }
        }

        // ── Epic raw dump ─────────────────────────────────────────────────────
        if (args.Contains("--epictest"))
        {
            Console.WriteLine("\n[Epic Games — raw library + catalog test]");
            var epic = new EpicStore(cache);
            if (!await epic.IsAuthenticatedAsync())
            {
                Warn("  Not authenticated (login in Settings first).");
            }
            else
            {
                cache.Invalidate("epic_library"); // always fresh
                cache.Invalidate("epic_library");
                var lib = await epic.GetLibraryAsync();
                Ok($"  Library: {lib.Count} games");
                foreach (var g in lib.Take(20))
                    Console.WriteLine($"    id={g.Id,-30}  title={g.Title,-40}  cover={g.CoverUrl[..Math.Min(60, g.CoverUrl.Length)]}");
            }
        }

        // ── Humble Bundle test ────────────────────────────────────────────────
        if (args.Contains("--humbletest"))
        {
            Console.WriteLine("\n[Humble Bundle — library test]");
            var humble = new HumbleStore(cache);
            if (!await humble.IsAuthenticatedAsync())
            {
                Warn("  Not authenticated (add session cookie in Settings).");
            }
            else
            {
                cache.Invalidate("humble_library");
                var lib = await humble.GetLibraryAsync();
                Ok($"  Library: {lib.Count} games");
                foreach (var g in lib.Take(30))
                    Console.WriteLine($"    id={g.Id,-35}  title={g.Title,-35}  cover={(g.CoverUrl.Length > 0 ? "yes" : "no")}");

                // Download test: find and download A Virus Named TOM
                var virusTom = lib.FirstOrDefault(g => g.Id.Contains("avirusnamedtom") && !g.Id.Contains("soundtrack"));
                if (virusTom != null)
                {
                    Console.WriteLine($"\n[Humble Download — {virusTom.Title} ({virusTom.Id})]");
                    var (downloads, dlErr) = await humble.GetDownloadsAsync(virusTom.Id);
                    if (dlErr != null) { Warn($"  {dlErr}"); }
                    else
                    {
                        Console.WriteLine($"  {downloads.Count} download(s):");
                        foreach (var d in downloads)
                            Console.WriteLine($"    platform={d.Platform,-10}  name={d.HumanName,-30}  size={d.FileSize / 1048576.0:F1} MB  url={d.Url[..Math.Min(60,d.Url.Length)]}...");

                        var linuxDl = downloads.FirstOrDefault(d =>
                            d.Platform.Equals("linux", StringComparison.OrdinalIgnoreCase));
                        if (linuxDl != null && !string.IsNullOrEmpty(linuxDl.Url))
                        {
                            var destDir = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "portmaster-desktop", "cache", "gamefiles", virusTom.Id);
                            Directory.CreateDirectory(destDir);
                            var fileName = linuxDl.Url.Split('?')[0].Split('/').Last();
                            if (string.IsNullOrEmpty(fileName)) fileName = $"{virusTom.Id}.bin";
                            var destPath = Path.Combine(destDir, fileName);
                            Console.WriteLine($"  Downloading → {destPath}");
                            var progress = new Progress<(string message, double fraction)>(p =>
                                Console.Write($"\r  {p.message}  ({p.fraction:P0})     "));
                            var err = await humble.DownloadGameFileAsync(linuxDl.Url, destPath,
                                linuxDl.FileSize, progress);
                            Console.WriteLine();
                            if (err != null) Warn($"  Download error: {err}");
                            else Ok($"  Downloaded to {destPath}");
                        }
                        else Warn("  No Linux download found.");
                    }
                }
            }
        }

        // ── Matching sample ───────────────────────────────────────────────────
        if (all || args.Contains("--match"))
        {
            Console.WriteLine("\n[Port–Game Matching (Steam)]");
            var steamRoot = LocalSteamStore.FindSteamRoot();
            if (steamRoot != null)
            {
                var store = new LocalSteamStore(cache);
                var pm = new PortMasterClient(cache);
                var ports = await pm.GetPortsAsync(false);
                var steamLib = await store.GetLibraryAsync();
                var steamPorts = ports.Where(p => p.Attr.Store.Any(s =>
                    s.Name.Equals("steam", StringComparison.OrdinalIgnoreCase))).ToList();

                int matched = 0;
                foreach (var port in steamPorts.Take(50))
                {
                    var steamEntry = port.Attr.Store.FirstOrDefault(s =>
                        s.Name.Equals("steam", StringComparison.OrdinalIgnoreCase));
                    if (steamEntry == null) continue;
                    var game = await store.FindOwnedGameAsync(steamEntry.GameUrl);
                    if (game != null) { matched++; Console.WriteLine($"    OWNED: {port.Attr.Title} → {game.Title}"); }
                }
                Ok($"  {matched} / {Math.Min(steamPorts.Count, 50)} sample ports owned");
            }
        }

        // ── Full library match (all stores vs full catalog) ───────────────────
        if (args.Contains("--fullmatch"))
        {
            Console.WriteLine("\n[Full Library Match (all authenticated stores)]");
            var pm = new PortMasterClient(cache);
            var partSvc = new PartitionService();
            var stores = new List<IGameStore> { new LocalSteamStore(cache), new GogStore(cache) };
            var installSvc = new InstallService(pm);
            var libSvc = new LibraryService(stores, pm, partSvc, installSvc);
            Action<string> progress = msg => Console.Write($"\r  {msg,-60}");
            var (matches, partitions, storeCounts) = await libSvc.LoadAsync(forceRefresh, progress);
            Console.WriteLine();

            var owned = matches.Where(m => m.HasOwnedGame).ToList();
            if (owned.Count == 0)
                Warn("  No owned games match any PortMaster port.");
            else
            {
                Ok($"  {owned.Count} owned game(s) with a PortMaster port:");
                foreach (var m in owned)
                    Console.WriteLine($"    [{m.InstallState}] {m.DisplayTitle}  ({string.Join(", ", m.OwnedGames.Select(g => g.Title))})");
            }

            Console.WriteLine();
            var storeInfo = storeCounts.Count > 0
                ? string.Join(", ", storeCounts.Select(s => $"{s.displayName}: {s.count}"))
                : "No stores connected";
            Console.WriteLine($"  Stores: {storeInfo}");
            Console.WriteLine($"  Partitions: {(partitions.Count == 0 ? "none" : string.Join(", ", partitions.Select(p => p.DisplayName)))}");
        }

        // ── Download test: Papers, Please → cache only ───────────────────────
        if (args.Contains("--downloadtest"))
        {
            Console.WriteLine("\n[Download Test: Papers, Please]");
            var pm = new PortMasterClient(cache);
            var ports = await pm.GetPortsAsync(false);
            var port = ports.FirstOrDefault(p =>
                p.Attr.Title.Contains("Papers", StringComparison.OrdinalIgnoreCase));

            if (port == null) { Err("  Port not found in catalog."); }
            else
            {
                Ok($"  Found: {port.Attr.Title}  ({port.Size / 1024 / 1024.0:F1} MB)");
                var downloadUrl = port.Source?.Url ?? port.DownloadUrl;
                Console.WriteLine($"  URL: {downloadUrl}");

                var cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "portmaster-desktop", "cache", "ports");
                Directory.CreateDirectory(cacheDir);
                var destZip = Path.Combine(cacheDir, port.Name);

                if (File.Exists(destZip))
                {
                    Ok($"  Already cached: {destZip}  ({new FileInfo(destZip).Length / 1024 / 1024.0:F1} MB)");
                }
                else
                {
                    Console.WriteLine($"  Downloading to {destZip} …");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IProgress<(string msg, double frac)> prog = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg}  [{r.frac * 100:F0}%]   "));

                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("PortMasterDesktop/1.0");
                    using var resp = await http.GetAsync(
                        downloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? port.Size;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(destZip);
                    var buf = new byte[131072];
                    long done2 = 0; int read;
                    while ((read = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read));
                        done2 += read;
                        prog.Report(($"Downloading… {done2 / 1024 / 1024} MB / {total / 1024 / 1024} MB",
                            total > 0 ? (double)done2 / total : 0));
                    }
                    Console.WriteLine();
                    sw.Stop();
                    var fi = new FileInfo(destZip);
                    Ok($"  Saved {fi.Length / 1024 / 1024.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s → {destZip}");
                    Console.WriteLine($"  MD5 expected: {port.Source?.Md5 ?? "n/a"}");
                }
            }
        }

        // ── Download game files from Steam (depot download) ───────────────────
        if (args.Contains("--gamefilestest"))
        {
            Console.WriteLine("\n[Steam Depot Download: Papers, Please]");
            const uint appId     = 239030;
            const uint depotId   = 239033;
            const ulong manifestId = 4681415496149094331UL;

            var destDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "portmaster-desktop", "cache", "gamefiles", "papersplease");

            if (Directory.Exists(destDir) && Directory.GetFiles(destDir, "*", SearchOption.AllDirectories).Length > 0)
            {
                var existing = Directory.GetFiles(destDir, "*", SearchOption.AllDirectories);
                Ok($"  Already downloaded: {existing.Length} files in {destDir}");
            }
            else
            {
                string? gameFilesErr = null;

                // Step 1: list manifest to preview what we'll download
                Console.WriteLine("  Connecting to Steam to inspect manifest…");
                List<(string name, long size)> files;
                using (var downloader = new PortMasterDesktop.Stores.SteamDepotDownloader(cache))
                {
                    string? listErr;
                    (files, listErr) = await downloader.ListManifestAsync(appId, depotId, manifestId);
                    if (listErr != null) gameFilesErr = $"Manifest error: {listErr}";
                }

                if (gameFilesErr == null)
                {
                    long totalBytes = files.Sum(f => f.size);
                    Ok($"  Manifest: {files.Count} files, {totalBytes / 1024 / 1024.0:F1} MB total");
                    foreach (var (name, size) in files.Take(10))
                        Console.WriteLine($"    {name}  ({size / 1024.0:F0} KB)");
                    if (files.Count > 10) Console.WriteLine($"    … and {files.Count - 10} more");

                    // Step 2: download all files
                    Console.WriteLine($"\n  Downloading to {destDir} …");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    IProgress<(string msg, double frac)> prog = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg,-70} [{r.frac * 100:F0}%]  "));

                    using (var downloader2 = new PortMasterDesktop.Stores.SteamDepotDownloader(cache))
                        gameFilesErr = await downloader2.DownloadDepotAsync(
                            appId, depotId, manifestId, destDir, prog);

                    Console.WriteLine();
                    sw.Stop();
                    if (gameFilesErr == null)
                        Ok($"  Done in {sw.Elapsed.TotalSeconds:F1}s → {destDir}");
                }

                if (gameFilesErr != null)
                    Err($"  {gameFilesErr}");
            }
        }

        // ── Full install test: Super Meat Boy → SD card ───────────────────────
        // Usage: --test --installtest
        if (args.Contains("--installtest"))
        {
            Console.WriteLine("\n[Install Test: Super Meat Boy → SD card]");

            // 1. Catalog
            var pm = new PortMasterClient(cache);
            var ports = await pm.GetPortsAsync(forceRefresh);
            var port = ports.FirstOrDefault(p => p.Name == "supermeatboy.zip");
            if (port == null) { Err("  supermeatboy.zip not found in catalog."); goto installTestDone; }
            Ok($"  Port: {port.Attr.Title}  ({port.Size / 1048576.0:F1} MB)");

            // 2. Partition
            var partSvc = new PartitionService();
            var partitions = partSvc.Detect();
            if (partitions.Count == 0) { Warn("  No SD card / PortMaster partition detected. Plug in SD card."); goto installTestDone; }
            var partition = partitions[0];
            Ok($"  Partition: {partition.DisplayName}  ({partition.FreeSpace} free)");

            var installSvc = new InstallService(pm);
            var portsPath = partition.PortsPath;

            // 3. Install port ZIP (skip if already installed)
            var portDir = Path.Combine(portsPath, "supermeatboy");
            if (Directory.Exists(portDir))
            {
                Ok($"  Port already installed at {portDir}");
                var mergeErr = InstallService.MergeGamelist(portsPath, port);
                if (mergeErr != null) Err($"  gamelist.xml error: {mergeErr}");
                else Ok($"  gamelist.xml updated");
            }
            else
            {
                Console.WriteLine($"  Installing port to {portsPath} …");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                IProgress<(string msg, double frac)> prog = new Progress<(string msg, double frac)>(r =>
                    Console.Write($"\r  {r.msg,-60} [{r.frac * 100:F0}%]  "));
                try
                {
                    await installSvc.InstallPortAsync(port, portsPath, prog);
                    Console.WriteLine();
                    sw.Stop();
                    Ok($"  Port installed in {sw.Elapsed.TotalSeconds:F1}s");
                }
                catch (Exception ex) { Console.WriteLine(); Err($"  Install failed: {ex.Message}"); goto installTestDone; }
            }

            // 4. Find Super Meat Boy in local Steam
            var gamedataDir = Path.Combine(portDir, "gamedata");
            bool hasRealGameFiles = Directory.Exists(gamedataDir) &&
                Directory.EnumerateFiles(gamedataDir, "*", SearchOption.AllDirectories)
                    .Any(f => !Path.GetFileName(f).StartsWith('.'));
            if (hasRealGameFiles)
            {
                Ok($"  Game data already present in {gamedataDir} — skipping file copy.");
                goto installTestDone;
            }

            var steamStore = new LocalSteamStore(cache);
            StoreGame? smb = null;
            if (await steamStore.IsAuthenticatedAsync())
            {
                var lib = await steamStore.GetLibraryAsync();
                smb = lib.FirstOrDefault(g => g.Id == "40800");
                if (smb != null)
                    Console.WriteLine($"  Found in Steam: {smb.Title}  installed={smb.IsInstalled}  path={smb.InstallPath}");
                else
                    Warn("  Super Meat Boy (appId 40800) not in Steam library.");
            }
            else
            {
                Warn("  Steam not found locally.");
            }

            // 5a. Copy from local install
            if (smb?.IsInstalled == true && !string.IsNullOrEmpty(smb.InstallPath))
            {
                Console.WriteLine($"  Copying game files from {smb.InstallPath} → {gamedataDir} …");
                IProgress<(string msg, double frac)> cpProg = new Progress<(string msg, double frac)>(r =>
                    Console.Write($"\r  {r.msg,-60} [{r.frac * 100:F0}%]  "));
                var copyErr = await installSvc.InstallGameFilesAsync(port, smb, portsPath, cpProg);
                Console.WriteLine();
                if (copyErr != null) Err($"  Copy failed: {copyErr}");
                else Ok($"  Game files copied to {gamedataDir}");
            }
            // 5b. Depot download path
            else
            {
                const string AppId    = "40800";
                const string DepotId  = "40802";
                const string Manifest = "6556596646716197166";
                var depotPath = SteamDepotService.DepotPath(AppId, DepotId);

                async Task CopyDepotAsync()
                {
                    Console.WriteLine($"\n  Copying depot → {gamedataDir} …");
                    IProgress<(string msg, double frac)> depProg = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg,-60} [{r.frac * 100:F0}%]  "));
                    var fakeGame = new StoreGame
                    {
                        Store = StoreId.Steam, Id = AppId,
                        Title = "Super Meat Boy", IsInstalled = true, InstallPath = depotPath
                    };
                    var copyErr = await installSvc.InstallGameFilesAsync(port, fakeGame, portsPath, depProg);
                    Console.WriteLine();
                    if (copyErr != null) Err($"  Copy failed: {copyErr}"); else Ok($"  Done → {gamedataDir}");
                }

                bool depotHasFiles = Directory.Exists(depotPath) &&
                    Directory.EnumerateFiles(depotPath, "*", SearchOption.AllDirectories).Any();

                if (depotHasFiles)
                {
                    Ok($"  Depot already at {depotPath}");
                    await CopyDepotAsync();
                }
                else
                {
                    // Open Steam console for the user to paste the download command,
                    // then monitor until it arrives and copy automatically.
                    Console.WriteLine("  Opening Steam console…");
                    try
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo("xdg-open", "steam://open/console")
                            { UseShellExecute = false,
                              RedirectStandardOutput = true, RedirectStandardError = true });
                    }
                    catch { /* ignore — user can open it manually */ }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\n  Paste in the Steam Console tab:\n");
                    Console.WriteLine($"    download_depot {AppId} {DepotId} {Manifest}");
                    Console.ResetColor();
                    Console.WriteLine($"\n  Waiting for depot at:\n    {depotPath}");
                    Console.WriteLine("  (up to 30 min — Ctrl+C to abort)\n");

                    IProgress<(string msg, double frac)> monProg = new Progress<(string msg, double frac)>(r =>
                        Console.Write($"\r  {r.msg,-60} [{r.frac * 100:F0}%]  "));

                    var monErr = await SteamDepotService.MonitorDepotDownloadAsync(
                        depotPath, monProg, CancellationToken.None, TimeSpan.FromMinutes(30));
                    Console.WriteLine();

                    if (monErr != null) { Err($"  {monErr}"); goto installTestDone; }
                    await CopyDepotAsync();
                }
            }

            installTestDone:;
        }

        // ── Debug: trace Steam binary VDF readers ─────────────────────────────
        if (args.Contains("--debugsteam"))
        {
            Console.WriteLine("\n[Steam Binary VDF Debug]");
            var steamRoot = LocalSteamStore.FindSteamRoot();
            if (steamRoot != null && long.TryParse("76561198067352326", out long sid64))
            {
                long s32 = sid64 - 76561197960265728L;
                Console.WriteLine($"  steam32={s32}");
                var owned = PortMasterDesktop.Stores.SteamLocalLibraryReader.ReadOwnedAppsDebug(steamRoot, sid64);
                var games = owned.Where(e => e.Value.appType.Equals("game", StringComparison.OrdinalIgnoreCase)).ToList();
                Ok($"  Found {owned.Count} owned apps, {games.Count} are games");
                foreach (var e in games.Take(10))
                    Console.WriteLine($"    {e.Key}: {e.Value.name}");
            }
        }

        // ── Steam install command ─────────────────────────────────────────────
        // Usage: --steaminstall [appid]  (default: searches library by title)
        var steamInstallIdx = Array.IndexOf(args, "--steaminstall");
        if (steamInstallIdx >= 0)
        {
            Console.WriteLine("\n[Steam Install Command]");
            var steamStore = new LocalSteamStore(cache);
            if (!await steamStore.IsAuthenticatedAsync())
            {
                Warn("  Steam not found.");
            }
            else
            {
                // Explicit app ID passed as next argument, or search library by title
                string? targetArg = steamInstallIdx + 1 < args.Length && !args[steamInstallIdx + 1].StartsWith("--")
                    ? args[steamInstallIdx + 1] : null;

                var lib = await steamStore.GetLibraryAsync();
                StoreGame? target = null;
                if (targetArg != null)
                {
                    // Try numeric app ID first, then title substring
                    target = lib.FirstOrDefault(g => g.Id == targetArg) ??
                             lib.FirstOrDefault(g => g.Title.Contains(targetArg, StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        // Not in cached library — send by raw app ID if numeric
                        if (long.TryParse(targetArg, out _))
                        {
                            Console.WriteLine($"  App ID {targetArg} not in library cache, sending install anyway.");
                            var directErr = LocalSteamStore.RequestInstall(targetArg);
                            if (directErr != null) Warn($"  Error: {directErr}");
                            else Ok($"  steam://install/{targetArg} sent.");
                            goto steamInstallDone;
                        }
                        Warn($"  '{targetArg}' not found in library.");
                        goto steamInstallDone;
                    }
                }
                else
                {
                    target = lib.FirstOrDefault(g => g.Id == "736260"); // default: Baba Is You
                }

                if (target == null) { Warn("  Game not found in library."); goto steamInstallDone; }

                Console.WriteLine($"  Found: {target.Title} (appid={target.Id}, installed={target.IsInstalled})");
                if (target.IsInstalled)
                    Console.WriteLine($"  Already installed at: {target.InstallPath}");
                Console.WriteLine($"  Sending steam://install/{target.Id} to Steam client...");
                var err = LocalSteamStore.RequestInstall(target.Id);
                if (err != null) Warn($"  Error: {err}");
                else Ok($"  steam://install/{target.Id} sent — Steam should react now.");
            }
            steamInstallDone:;
        }

        // ── Steam depot download via local Steam console ───────────────────────
        // Usage: --depotest <appId> <depotId>
        var depotIdx = Array.IndexOf(args, "--depotest");
        if (depotIdx >= 0)
        {
            var appId   = depotIdx + 1 < args.Length ? args[depotIdx + 1] : null;
            var depotId = depotIdx + 2 < args.Length ? args[depotIdx + 2] : null;

            Console.WriteLine("\n[Steam Depot Download — Local Steam Console]");
            if (appId == null || depotId == null)
            {
                Warn("  Usage: --test --depotest <appId> <depotId>");
            }
            else
            {
                var depotPath = SteamDepotService.DepotPath(appId, depotId);
                Console.WriteLine($"  App: {appId}  Depot: {depotId}");
                Console.WriteLine($"  Expected path: {depotPath}");

                if (Directory.Exists(depotPath) &&
                    Directory.EnumerateFiles(depotPath, "*", SearchOption.AllDirectories).Any())
                {
                    var sizeMb = new DirectoryInfo(depotPath)
                        .GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1048576.0;
                    Ok($"  Already present ({sizeMb:F0} MB) — skipping download.");
                }
                else
                {
                    // CLI: open console and print the command ourselves (no dialog delegate)
                    Console.WriteLine("  Opening Steam console...");
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo("steam://open/console")
                        { UseShellExecute = true });

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"\n  >>> Paste in Steam Console:  download_depot {appId} {depotId}\n");
                    Console.ResetColor();
                    Console.WriteLine("  Monitoring download path — press Ctrl+C to abort...");

                    var progress = new Progress<(string message, double fraction)>(p =>
                        Console.Write($"\r  {p.message,-60}"));

                    var err = await SteamDepotService.MonitorDepotDownloadAsync(
                        depotPath, progress, CancellationToken.None,
                        timeout: TimeSpan.FromMinutes(30));

                    Console.WriteLine();
                    if (err != null) Warn($"  {err}");
                    else Ok($"  Done → {depotPath}");
                }
            }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n=== Done ===");
        Console.ResetColor();
    }

    private static void Ok(string msg) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(msg); Console.ResetColor(); }
    private static void Warn(string msg) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(msg); Console.ResetColor(); }
    private static void Err(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(msg); Console.ResetColor(); }
}
