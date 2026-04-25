using PortMasterDesktop.Services;
using SteamKit2;
using SteamKit2.Authentication;

namespace PortMasterDesktop.Stores;

/// <summary>
/// Downloads a Steam depot using the account's saved refresh token.
/// Uses SteamKit2 CDN.Client to fetch manifest + chunks without launching the Steam client.
/// </summary>
public class SteamDepotDownloader : IDisposable
{
    private readonly CacheService _cache;
    private SteamClient? _steamClient;
    private CancellationTokenSource? _loopCts;

    public SteamDepotDownloader(CacheService cache)
    {
        _cache = cache;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists files in a depot manifest without downloading content.
    /// Returns (files, error); error is null on success.
    /// </summary>
    public async Task<(List<(string name, long size)> files, string? error)> ListManifestAsync(
        uint appId, uint depotId, ulong manifestId, CancellationToken ct = default)
    {
        var (client, content, apps, loginErr) = await ConnectAndLoginAsync(ct);
        if (loginErr != null) return ([], loginErr);
        try
        {
            var manifest = await FetchManifestAsync(client!, content!, apps!, appId, depotId, manifestId);
            var files = manifest.Files?
                .Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)
                .Select(f => (f.FileName, (long)f.TotalSize))
                .ToList() ?? [];
            return (files, null);
        }
        catch (Exception ex) { return ([], ex.Message); }
        finally { Cleanup(); }
    }

    /// <summary>
    /// Downloads all depot files into <paramref name="destDir"/>.
    /// Progress tuple: (message, fraction in [0,1]).
    /// Returns an error string on failure, null on success.
    /// </summary>
    public async Task<string?> DownloadDepotAsync(
        uint appId, uint depotId, ulong manifestId, string destDir,
        IProgress<(string message, double fraction)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(("Connecting to Steam…", 0));
        var (client, content, apps, loginErr) = await ConnectAndLoginAsync(ct);
        if (loginErr != null) return loginErr;

        Directory.CreateDirectory(destDir);
        try
        {
            return await RunDownloadAsync(client!, content!, apps!, appId, depotId, manifestId,
                destDir, progress, ct);
        }
        catch (Exception ex) { return ex.Message; }
        finally { Cleanup(); }
    }

    // ── Core download logic ───────────────────────────────────────────────────

    private static async Task<DepotManifest> FetchManifestAsync(
        SteamClient client, SteamContent content, SteamApps apps,
        uint appId, uint depotId, ulong manifestId)
    {
        var servers = (await content.GetServersForSteamPipe()).ToList();
        if (servers.Count == 0) throw new InvalidOperationException("No CDN servers returned.");
        var server = servers.FirstOrDefault(s => s.Type == "SteamCDN") ?? servers[0];

        var depotKeyResult = await apps.GetDepotDecryptionKey(depotId, appId);
        if (depotKeyResult.Result != EResult.OK)
            throw new InvalidOperationException($"Depot key error: {depotKeyResult.Result}");

        var manifestCode = await content.GetManifestRequestCode(
            depotId, appId, manifestId, "public", null);

        using var cdn = new SteamKit2.CDN.Client(client);
        return await cdn.DownloadManifestAsync(
            depotId, manifestId, manifestCode, server, depotKeyResult.DepotKey,
            proxyServer: null, cdnAuthToken: null);
    }

    private static async Task<string?> RunDownloadAsync(
        SteamClient client, SteamContent content, SteamApps apps,
        uint appId, uint depotId, ulong manifestId, string destDir,
        IProgress<(string message, double fraction)>? progress, CancellationToken ct)
    {
        progress?.Report(("Fetching CDN servers…", 0.01));
        var servers = (await content.GetServersForSteamPipe()).ToList();
        if (servers.Count == 0) return "No CDN servers returned.";
        var server = servers.FirstOrDefault(s => s.Type == "SteamCDN") ?? servers[0];

        progress?.Report(("Requesting depot key…", 0.02));
        var depotKeyResult = await apps.GetDepotDecryptionKey(depotId, appId);
        if (depotKeyResult.Result != EResult.OK)
            return $"Failed to get depot key: {depotKeyResult.Result}";
        var depotKey = depotKeyResult.DepotKey;

        progress?.Report(("Getting manifest access code…", 0.03));
        var manifestCode = await content.GetManifestRequestCode(
            depotId, appId, manifestId, "public", null);

        progress?.Report(("Downloading manifest…", 0.04));
        using var cdn = new SteamKit2.CDN.Client(client);
        var manifest = await cdn.DownloadManifestAsync(
            depotId, manifestId, manifestCode, server, depotKey,
            proxyServer: null, cdnAuthToken: null);

        if (manifest.Files == null || manifest.Files.Count == 0)
            return "Manifest contained no files.";

        // Create directories first
        foreach (var dir in manifest.Files.Where(f => (f.Flags & EDepotFileFlag.Directory) != 0))
            Directory.CreateDirectory(Path.Combine(destDir,
                dir.FileName.Replace('\\', Path.DirectorySeparatorChar)));

        var fileList = manifest.Files
            .Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)
            .OrderBy(f => f.FileName)
            .ToList();

        long totalBytes = fileList.Sum(f => (long)f.TotalSize);
        long doneBytes  = 0;

        for (int i = 0; i < fileList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = fileList[i];
            var destPath = Path.Combine(destDir,
                file.FileName.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            if (!string.IsNullOrEmpty(file.LinkTarget))
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                File.CreateSymbolicLink(destPath, file.LinkTarget);
                continue;
            }

            var relName = file.FileName.Length > 48
                ? "…" + file.FileName[^47..] : file.FileName;
            var frac = totalBytes > 0 ? (double)doneBytes / totalBytes : 0;
            progress?.Report(($"[{i + 1}/{fileList.Count}] {relName}", frac));

            await using var dest = File.Create(destPath);
            if (file.TotalSize > 0) dest.SetLength((long)file.TotalSize);

            foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var buf = new byte[chunk.UncompressedLength];
                await cdn.DownloadDepotChunkAsync(depotId, chunk, server, depotKey, buf,
                    proxyServer: null, cdnAuthToken: null);
                dest.Seek((long)chunk.Offset, SeekOrigin.Begin);
                await dest.WriteAsync(buf.AsMemory(0, (int)chunk.UncompressedLength), ct);
                doneBytes += chunk.UncompressedLength;
            }
        }

        progress?.Report(($"Downloaded {fileList.Count} files ({totalBytes / 1024 / 1024.0:F1} MB)", 1.0));
        return null;
    }

    // ── Authentication ────────────────────────────────────────────────────────

    private async Task<(SteamClient? client, SteamContent? content, SteamApps? apps, string? error)>
        ConnectAndLoginAsync(CancellationToken ct)
    {
        var steamId64Str = LoadCredential("remote_steam_id64") ?? LoadCredential("steam_id64");
        var refreshToken = LoadCredential("remote_refresh_token");
        var username     = LoadCredential("remote_username") ?? "";

        if (steamId64Str == null)
            return (null, null, null, "No Steam account found. Log in via Settings first.");
        if (refreshToken == null)
            return (null, null, null, "No refresh token saved. Log in via Settings → Steam account.");

        var steamClient  = new SteamClient();
        _steamClient = steamClient;
        var manager = new CallbackManager(steamClient);

        var steamContent = steamClient.GetHandler<SteamContent>()!;
        var steamApps    = steamClient.GetHandler<SteamApps>()!;
        var steamUser    = steamClient.GetHandler<SteamUser>()!;

        var connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggedInTcs  = new TaskCompletionSource<EResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connectedTcs.TrySetResult(true));
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
            connectedTcs.TrySetException(new Exception("Steam disconnected.")));
        manager.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedInTcs.TrySetResult(cb.Result));

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopCts.CancelAfter(TimeSpan.FromSeconds(60));
        _ = Task.Run(() =>
        {
            while (!_loopCts.Token.IsCancellationRequested)
                manager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
        }, _loopCts.Token);

        steamClient.Connect();

        try { await connectedTcs.Task.WaitAsync(_loopCts.Token); }
        catch (Exception ex)
        {
            Cleanup();
            return (null, null, null, $"Connection failed: {ex.Message}");
        }

        // AccessToken in LogOnDetails accepts a refresh token (JWT) for re-auth
        steamUser.LogOn(new SteamUser.LogOnDetails
        {
            Username    = username,
            AccessToken = refreshToken,
        });

        EResult loginResult;
        try
        {
            loginResult = await loggedInTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), _loopCts.Token);
        }
        catch (Exception ex)
        {
            Cleanup();
            return (null, null, null, $"Login timed out: {ex.Message}");
        }

        if (loginResult != EResult.OK)
        {
            Cleanup();
            return (null, null, null,
                $"Steam login failed: {loginResult}. Try logging in again via Settings.");
        }

        return (steamClient, steamContent, steamApps, null);
    }

    private void Cleanup()
    {
        _loopCts?.Cancel();
        _steamClient?.Disconnect();
    }

    // Reads credentials in the same format as BaseGameStore: creds/Steam_{key}
    private static string? LoadCredential(string key)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "portmaster-desktop", "creds", $"Steam_{key}");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Dispose()
    {
        Cleanup();
        _loopCts?.Dispose();
    }
}
