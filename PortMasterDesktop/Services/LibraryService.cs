using PortMasterDesktop.Models;
using PortMasterDesktop.PortMaster;
using PortMasterDesktop.Stores;

namespace PortMasterDesktop.Services;

public class LibraryService
{
    private readonly IEnumerable<IGameStore> _stores;
    private readonly PortMasterClient _portMaster;
    private readonly PartitionService _partitionService;
    private readonly InstallService _installService;

    public LibraryService(
        IEnumerable<IGameStore> stores,
        PortMasterClient portMaster,
        PartitionService partitionService,
        InstallService installService)
    {
        _stores = stores;
        _portMaster = portMaster;
        _partitionService = partitionService;
        _installService = installService;
    }

    public async Task<(IReadOnlyList<GameMatch> matches,
                       IReadOnlyList<PartitionInfo> partitions,
                       IReadOnlyList<(string displayName, int count)> storeCounts)>
        LoadAsync(
            bool forceRefresh = false,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
    {
        var partitions = _partitionService.Detect();
        var primaryPartition = partitions.FirstOrDefault();

        progress?.Report("Loading PortMaster catalog…");
        var ports = await _portMaster.GetPortsAsync(forceRefresh, progress, ct);

        progress?.Report("Loading game libraries…");
        var authStores = new List<IGameStore>();
        foreach (var store in _stores)
            if (await store.IsAuthenticatedAsync())
                authStores.Add(store);

        var libraryTasks = authStores.Select(s => s.GetLibraryAsync(ct)).ToList();
        var libraries = await Task.WhenAll(libraryTasks);

        // Per-store counts
        var storeCounts = authStores
            .Zip(libraries)
            .GroupBy(pair => pair.First.StoreId)
            .Select(g =>
            {
                var first = g.First();
                var total = g.Sum(pair => pair.Second.Count);
                return (first.First.DisplayName, total);
            })
            .Where(x => x.total > 0)
            .ToList();

        // All owned games (flat, deduped by store+id)
        var allOwned = new List<StoreGame>();
        var seen = new HashSet<string>();
        for (int i = 0; i < authStores.Count; i++)
            foreach (var g in libraries[i])
                if (seen.Add($"{authStores[i].StoreId}:{g.Id}"))
                    allOwned.Add(g);

        progress?.Report("Matching games with ports…");

        // Build port lookup: store → gameUrl → port (for fast matching)
        var portByStoreUrl = new Dictionary<string, Port>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in ports)
            foreach (var ps in port.Attr.Store)
                if (!string.IsNullOrEmpty(ps.GameUrl))
                    portByStoreUrl[ps.GameUrl] = port;

        // For each port, find owned games (same logic as before)
        var portMatches = new Dictionary<string, GameMatch>(StringComparer.OrdinalIgnoreCase); // port slug → match

        foreach (var port in ports)
        {
            var owned = new List<StoreGame>();
            StoreMatchCompatibility? bestCompat = null;

            // Load GFI store entries once; build a lookup by StoreId for Phase 1 use
            var gfiEntries = await _installService.GetStoreEntriesAsync(port.Name);
            var gfiByStoreId = gfiEntries
                .Where(e => ParseStoreId(e.StoreKey) != null)
                .ToDictionary(e => ParseStoreId(e.StoreKey)!.Value, e => e);

            // Phase 1: catalog store entries (port.Attr.Store)
            var checkedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var portStore in port.Attr.Store)
            {
                if (!string.IsNullOrEmpty(portStore.GameUrl))
                    checkedUrls.Add(portStore.GameUrl);
                var storeId = ParseStoreId(portStore.Name);
                if (storeId == null) continue;

                foreach (var authStore in authStores.Where(s => s.StoreId == storeId))
                {
                    var game = await authStore.FindOwnedGameAsync(portStore.GameUrl, ct);
                    if (game != null && owned.All(g => g.Id != game.Id))
                    {
                        owned.Add(game);
                        // Resolve compat from GFI; if no GFI entry → Unknown
                        var compat = gfiByStoreId.TryGetValue(storeId.Value, out var gfi)
                            ? GfiCompatToMatchCompat(gfi.Compatibility)
                            : StoreMatchCompatibility.Unknown;
                        bestCompat = BestCompat(bestCompat, compat);
                        break;
                    }
                }
            }

            // Phase 2: GFI supplementary entries not already checked by Phase 1
            foreach (var entry in gfiEntries)
            {
                if (string.IsNullOrEmpty(entry.Url)) continue;
                if (checkedUrls.Contains(entry.Url)) continue;
                checkedUrls.Add(entry.Url);
                var storeId = ParseStoreId(entry.StoreKey);
                if (storeId == null) continue;
                foreach (var authStore in authStores.Where(s => s.StoreId == storeId))
                {
                    var game = await authStore.FindOwnedGameAsync(entry.Url, ct);
                    if (game != null && owned.All(g => g.Id != game.Id))
                    {
                        owned.Add(game);
                        bestCompat = BestCompat(bestCompat, GfiCompatToMatchCompat(entry.Compatibility));
                        break;
                    }
                }
            }

            if (owned.Count == 0 && !port.Attr.Rtr) continue; // skip unowned non-RTR ports

            var installState = ComputeInstallState(port, primaryPartition);
            portMatches[port.Slug] = new GameMatch
            {
                Port = port,
                OwnedGames = owned,
                InstallState = installState,
                StoreCompat = owned.Count > 0 ? bestCompat : null,
            };
        }

        // Build the full game list: every owned game gets an entry
        // Games with a matching port share the GameMatch; games without get their own
        var ownedMatchedIds = portMatches.Values
            .SelectMany(m => m.OwnedGames)
            .Select(g => $"{g.Store}:{g.Id}")
            .ToHashSet();

        var matches = new List<GameMatch>(portMatches.Values);

        foreach (var game in allOwned)
        {
            if (!ownedMatchedIds.Contains($"{game.Store}:{game.Id}"))
            {
                matches.Add(new GameMatch
                {
                    Port = null,
                    OwnedGames = [game],
                    InstallState = PortInstallState.NoPartition,
                });
            }
        }

        return (matches, partitions, storeCounts);
    }

    private static PortInstallState ComputeInstallState(Port port, PartitionInfo? partition)
    {
        if (partition == null) return PortInstallState.NoPartition;

        bool installed = PortMasterClient.IsPortInstalled(port, partition.PortsPath);
        if (!installed) return PortInstallState.NotInstalled;
        if (port.Attr.Rtr) return PortInstallState.Ready;

        return IsGameDataPresent(port, partition.PortsPath)
            ? PortInstallState.Ready
            : PortInstallState.NeedsGameFiles;
    }

    private static bool IsGameDataPresent(Port port, string portsPath)
    {
        var portDir = Path.Combine(portsPath, port.Slug);
        if (!Directory.Exists(portDir)) return false;
        foreach (var candidate in new[] { "gamedata", "gamefiles", "data" })
        {
            var sub = Path.Combine(portDir, candidate);
            if (Directory.Exists(sub) && Directory.EnumerateFileSystemEntries(sub).Any())
                return true;
        }
        return false;
    }

    private static StoreMatchCompatibility GfiCompatToMatchCompat(string compatibility) =>
        compatibility switch
        {
            "compatible"   => StoreMatchCompatibility.Compatible,
            "incompatible" => StoreMatchCompatibility.Incompatible,
            "not_available"=> StoreMatchCompatibility.Incompatible,
            _              => StoreMatchCompatibility.Unknown, // "unverified" or anything else
        };

    private static StoreMatchCompatibility BestCompat(StoreMatchCompatibility? current, StoreMatchCompatibility add)
    {
        if (current == null) return add;
        static int Score(StoreMatchCompatibility c) => c switch
        {
            StoreMatchCompatibility.Compatible   => 2,
            StoreMatchCompatibility.Unknown      => 1,
            _                                    => 0,
        };
        return Score(add) > Score(current.Value) ? add : current.Value;
    }

    private static StoreId? ParseStoreId(string name) => name.ToLowerInvariant() switch
    {
        "steam" => StoreId.Steam,
        "gog" => StoreId.Gog,
        "epic" or "egs" or "epic games" => StoreId.Epic,
        "itch" or "itch.io" => StoreId.Itch,
        "amazon" or "amazon games" => StoreId.Amazon,
        "humble" or "humble bundle" => StoreId.Humble,
        _ => null,
    };
}
