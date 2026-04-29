using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PortMasterDesktop.Models;
using PortMasterDesktop.PortMaster;
using PortMasterDesktop.Services;

namespace PortMasterDesktop.ViewModels;

public enum LibraryFilter { All, PortMasterAvailable, ReadyToRun }

public partial class MainViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly InstallService _installer;
    private readonly SteamGridDbService _steamGridDb;
    private readonly PortMasterImagesService _portMasterImages;

    private IReadOnlyList<GameMatch> _allMatches = [];
    private string _portMasterImagesPath = "";
    private bool _usePortMasterImages = false;

    [ObservableProperty] private ObservableCollection<GameMatch> _displayedGames = [];
    [ObservableProperty] private GameMatch? _selectedGame;
    [ObservableProperty] private LibraryFilter _activeFilter = LibraryFilter.PortMasterAvailable;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "Loading…";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _installProgress;
    [ObservableProperty] private string _installMessage = "";
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private ObservableCollection<string> _installSteps = [];
    [ObservableProperty] private bool _showInstallLog;
    [ObservableProperty] private PartitionInfo? _activePartition;
    [ObservableProperty] private double _tileWidth = 160;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private SettingsViewModel? _settingsVm;

    private StreamWriter? _installLogWriter;
    public string? LastInstallLogPath { get; private set; }
    [ObservableProperty] private string _partitionStatus = "No SD card detected";
    [ObservableProperty] private string _dbStats = "";

    public Func<string, string, string?, Task>? ShowAlertAsync { get; set; }

    public MainViewModel(LibraryService library, InstallService installer, SteamGridDbService steamGridDb, PortMasterImagesService portMasterImages)
    {
        _library = library;
        _installer = installer;
        _steamGridDb = steamGridDb;
        _portMasterImages = portMasterImages;
    }

    [RelayCommand]
    public async Task OpenSettings()
    {
        if (SettingsVm == null)
            SettingsVm = App.Services.GetRequiredService<SettingsViewModel>();

        IsSettingsOpen = true;
        await SettingsVm.LoadCommand.ExecuteAsync(null);
    }

    public event Action? OnSettingsClosed;

    [RelayCommand]
    public void CloseSettings()
    {
        IsSettingsOpen = false;
        ApplyImageModeSetting();
        OnSettingsClosed?.Invoke();
    }

    public void ApplyImageModeSetting()
    {
        if (SettingsVm != null)
        {
            _usePortMasterImages = SettingsVm.UsePortMasterImages;

            // Prefer downloaded images, fallback to SD card if available
            var imagesPath = _portMasterImagesPath;
            if (string.IsNullOrEmpty(imagesPath) && ActivePartition != null)
            {
                var portMasterDir = Path.Combine(ActivePartition.MountPoint, "roms", "ports", "PortMaster");
                imagesPath = Path.Combine(portMasterDir, "screenshots");
            }

            foreach (var game in _allMatches)
            {
                game.UsePortMasterImages = SettingsVm.UsePortMasterImages;
                game.PortMasterImagesPath = imagesPath;
            }
        }
    }

    // ── Computed properties ───────────────────────────────────────────────────

    public bool HasSelectedGame => SelectedGame != null;
    public bool IsEmpty => !IsLoading && DisplayedGames.Count == 0;
    public bool SelectedIsIncompatible =>
        SelectedGame?.StoreCompat == StoreMatchCompatibility.Incompatible
        && (SelectedGame?.HasOwnedGame ?? false);
    public string SelectedIncompatibleReason =>
        SelectedGame?.IncompatibleReason ?? "This version of the game is not compatible with this port.";
    public bool CanInstall => SelectedGame?.HasPort == true
        && !SelectedIsIncompatible
        && ActivePartition != null
        && ActivePartition.CanWrite
        && !IsInstalling
        && SelectedGame.InstallState != PortInstallState.Ready;
    public bool NoPartitionDetected => ActivePartition == null;
    public bool NoWritePermission => ActivePartition != null && !ActivePartition.CanWrite;
    public bool HasPartitionWarning => ActivePartition != null &&
        (!ActivePartition.CanWrite || !ActivePartition.CanWriteLibsDir);

    public string PartitionWriteWarning
    {
        get
        {
            if (ActivePartition == null) return "";
            var issues = new List<string>();
            if (!ActivePartition.CanWritePortsDir) issues.Add("roms/ports/");
            if (!ActivePartition.CanWriteGamelist) issues.Add("roms/ports/gamelist.xml");
            if (!ActivePartition.CanWriteLibsDir)  issues.Add("roms/ports/PortMaster/libs/");
            if (issues.Count == 0) return "";
            return "⚠ No write permission to " + string.Join(", ", issues);
        }
    }

    public string SelectedPortSize => SelectedGame?.Port != null
        ? FormatBytes(SelectedGame.Port.Size) : "";
    public string SelectedGameSize => SelectedGame?.OwnedGameSizeBytes > 0
        ? FormatBytes(SelectedGame.OwnedGameSizeBytes)
        : (SelectedGame != null ? "Unknown" : "");

    public string SelectedInstallStateText => SelectedGame?.InstallState switch
    {
        PortInstallState.Ready          => "Installed",
        PortInstallState.NeedsGameFiles => "Needs game files",
        PortInstallState.NotInstalled   => "Not installed",
        PortInstallState.NoPartition    => "Plug in SD card",
        _                               => "",
    };

    public string InstallButtonLabel => SelectedGame?.InstallState switch
    {
        PortInstallState.NeedsGameFiles when SelectedGame.HasOwnedGame => "Copy Game Files",
        PortInstallState.NeedsGameFiles => "Show Instructions",
        _ => "Install",
    };

    public string SelectedStoreText
    {
        get
        {
            if (SelectedGame == null) return "";
            var stores = SelectedGame.OwnedGames
                .Select(g => g.Store)
                .Distinct()
                .Select(StoreDisplayName);
            return string.Join(" · ", stores);
        }
    }

    public string DetailPageUrl => SelectedGame?.Port != null
        ? $"{PortMasterClient.DetailPageBase}{SelectedGame.Port.Slug}"
        : "";

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync()
    {
        // Load persisted setting first
        if (SettingsVm == null)
            SettingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        await SettingsVm.LoadCommand.ExecuteAsync(null);

        await LoadInternalAsync(false);
    }

    [RelayCommand]
    public Task RefreshAsync() => LoadInternalAsync(true);

    [RelayCommand]
    public Task RescanPartitionsAsync() => LoadInternalAsync(false);

    private async Task LoadInternalAsync(bool forceRefresh)
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            // Download PortMaster images in background
            _ = Task.Run(async () =>
            {
                _portMasterImagesPath = await _portMasterImages.EnsureImagesAsync(
                    msg => StatusMessage = msg) ?? "";
            });

            var (matches, partitions, storeCounts) = await _library.LoadAsync(forceRefresh,
                msg => StatusMessage = msg);
            _allMatches = matches;
            ApplyImageModeSetting();
            ActivePartition = partitions.FirstOrDefault();
            if (ActivePartition != null)
            {
                var fsLabel = string.IsNullOrEmpty(ActivePartition.FileSystem)
                    ? "" : $" ({ActivePartition.FileSystem})";
                PartitionStatus = $"{ActivePartition.DisplayName}{fsLabel} — {ActivePartition.FreeSpace} free";
            }
            else
            {
                PartitionStatus = "No SD card detected — plug in to enable install";
            }
            ApplyFilter();
            _ = _steamGridDb.EnrichMatchesAsync(_allMatches,
                setUrl: (m, u) => Dispatcher.UIThread.Post(() => m.SgdbCoverUrl = u));

            var withPort = _allMatches.Count(m => m.HasPort && m.HasOwnedGame);
            var storeInfo = storeCounts.Count > 0
                ? string.Join("  ·  ", storeCounts.Select(s => $"{s.displayName}: {s.count}"))
                : "No stores connected";
            DbStats = $"{storeInfo}  •  {withPort} with PortMaster port";
            StatusMessage = withPort > 0
                ? $"{withPort} of your games have a PortMaster port"
                : "No port matches yet";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public void SetFilter(LibraryFilter filter)
    {
        ActiveFilter = filter;
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    public void SelectGame(GameMatch? game)
    {
        if (game != SelectedGame)
        {
            ShowInstallLog = false;
            InstallSteps.Clear();
        }
        SelectedGame = game;
        OnPropertyChanged(nameof(HasSelectedGame));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(SelectedIsIncompatible));
        OnPropertyChanged(nameof(SelectedIncompatibleReason));
        OnPropertyChanged(nameof(SelectedPortSize));
        OnPropertyChanged(nameof(SelectedGameSize));
        OnPropertyChanged(nameof(SelectedInstallStateText));
        OnPropertyChanged(nameof(SelectedStoreText));
        OnPropertyChanged(nameof(InstallButtonLabel));
        OnPropertyChanged(nameof(DetailPageUrl));
        OnPropertyChanged(nameof(NoPartitionDetected));
        OnPropertyChanged(nameof(NoWritePermission));
        OnPropertyChanged(nameof(HasPartitionWarning));
        OnPropertyChanged(nameof(PartitionWriteWarning));
    }

    [RelayCommand]
    public async Task InstallAsync()
    {
        if (SelectedGame?.Port == null || ActivePartition == null) return;

        IsInstalling = true;
        ShowInstallLog = true;
        InstallProgress = 0;
        InstallMessage = "Starting…";
        InstallSteps.Clear();

        // Open log file
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortMasterDesktop", "logs");
        Directory.CreateDirectory(logDir);
        LastInstallLogPath = Path.Combine(logDir,
            $"install_{DateTime.Now:yyyyMMdd_HHmmss}_{SelectedGame.Port.Slug}.log");
        _installLogWriter?.Dispose();
        _installLogWriter = new StreamWriter(LastInstallLogPath, append: false) { AutoFlush = true };
        _installLogWriter.WriteLine("=== PortMaster Desktop Install Log ===");
        _installLogWriter.WriteLine($"Port:       {SelectedGame.Port.Attr.Title}  ({SelectedGame.Port.Name})");
        _installLogWriter.WriteLine($"SD card:    {ActivePartition.MountPoint}  ({ActivePartition.FileSystem})");
        _installLogWriter.WriteLine($"Started:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _installLogWriter.WriteLine();

        var barProgress = new Progress<(string msg, double frac)>(p =>
        {
            InstallMessage = p.msg;
            InstallProgress = p.frac;
        });
        Action<string> stepLog = LogStep;
        _installer.ShowDialogAsync = ShowAlertAsync;

        bool hasErrors = false;

        try
        {
            var portPath = ActivePartition.PortsPath;

            var installError = await _installer.RunInstallAsync(
                SelectedGame.Port,
                SelectedGame.InstallState,
                SelectedGame.OwnedGames,
                portPath,
                fileSystem: ActivePartition.FileSystem,
                progress: barProgress,
                stepLog: stepLog);

            if (installError != null)
            {
                hasErrors = true;
                LogStep($"⚠️  {installError}");
                await ShowManualInstructionsAsync(SelectedGame.Port, installError);
            }

            InstallMessage = hasErrors ? "Done (check log for warnings)" : "Done!";
            InstallProgress = 1.0;
            LogStep($"📄 Log: {LastInstallLogPath}");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            InstallMessage = $"Error: {ex.Message}";
            LogStep($"❌ Fatal: {ex.Message}");
            _installLogWriter?.WriteLine($"\nEXCEPTION:\n{ex}");
        }
        finally
        {
            IsInstalling = false;
            _installLogWriter?.Dispose();
            _installLogWriter = null;
        }
    }

    private void LogStep(string line)
    {
        InstallSteps.Add(line);
        _installLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]  {line}");
    }

    [RelayCommand]
    public void OpenDetailPage()
    {
        if (SelectedGame?.Port == null) return;
        try { Process.Start(new ProcessStartInfo(DetailPageUrl) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    [RelayCommand]
    public async Task RefreshGameCover()
    {
        if (SelectedGame == null) return;

        string? foundCoverUrl = null;
        await _steamGridDb.EnrichMatchesAsync(new[] { SelectedGame },
            setUrl: (m, u) =>
            {
                foundCoverUrl = u;
                Dispatcher.UIThread.Post(() => m.SgdbCoverUrl = u);
            },
            forceRefresh: true);

        // Persist the selected cover to survive app restart
        if (!string.IsNullOrEmpty(foundCoverUrl))
        {
            await _steamGridDb.SaveSelectedCoverAsync(SelectedGame, foundCoverUrl);
        }
    }

    public void UpdateTileWidth(double availableWidth)
    {
        double minTileWidth = 140;
        double maxTileWidth = 200;

        // Make tiles 1.5x bigger when showing PortMaster images
        if (_usePortMasterImages)
        {
            minTileWidth *= 1.5;
            maxTileWidth *= 1.5;
        }

        const double itemMargin = 4;  // Margin around each card
        const double containerMargin = 8;  // Container outer margin

        // Account for container margins and minimum sizing
        double workingWidth = availableWidth - 2 * containerMargin;
        if (workingWidth < minTileWidth + 2 * itemMargin)
            workingWidth = minTileWidth + 2 * itemMargin;

        // Calculate how many columns can fit with minimum tile width + margins
        double tileWithMargins = minTileWidth + 2 * itemMargin;
        int columns = Math.Max(1, (int)(workingWidth / tileWithMargins));

        // Distribute width evenly: each tile gets its fair share minus margins
        // Total tile space = workingWidth - (columns * 2 * itemMargin)
        // Per-tile = (workingWidth - columns * 2 * itemMargin) / columns
        double totalMarginWidth = columns * 2 * itemMargin;
        double tileWidth = (workingWidth - totalMarginWidth) / columns;
        tileWidth = Math.Min(maxTileWidth, Math.Max(minTileWidth, tileWidth));

        TileWidth = tileWidth;
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ApplyFilter()
    {
        var q = SearchText.Trim().ToLowerInvariant();

        IEnumerable<GameMatch> src = ActiveFilter switch
        {
            // "Ready to Run" = ports that don't need game files
            LibraryFilter.ReadyToRun => _allMatches.Where(m => m.HasPort && m.Port!.Attr.Rtr),
            // "Available" = owned games that have a PortMaster port
            LibraryFilter.PortMasterAvailable => _allMatches.Where(m => m.HasPort && m.HasOwnedGame),
            // "All Games" = every owned game (with or without a port)
            _ => _allMatches.Where(m => m.HasOwnedGame),
        };

        if (!string.IsNullOrEmpty(q))
            src = src.Where(m =>
                m.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (m.Port?.Attr.Genres?.Any(g => g.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false));

        var games = src.OrderBy(m => m.DisplayTitle).ToList();

        // Force PortMaster images for ReadyToRun tab
        if (ActiveFilter == LibraryFilter.ReadyToRun)
        {
            foreach (var game in games)
                game.UsePortMasterImages = true;
        }

        DisplayedGames = new ObservableCollection<GameMatch>(games);
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(NoPartitionDetected));
        OnPropertyChanged(nameof(NoWritePermission));
        OnPropertyChanged(nameof(HasPartitionWarning));
        OnPropertyChanged(nameof(PartitionWriteWarning));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task ShowManualInstructionsAsync(Port port, string note)
    {
        var body = string.IsNullOrEmpty(note)
            ? (port.Attr.InstMd ?? port.Attr.Inst)
            : $"{note}\n\nManual instructions:\n{port.Attr.InstMd ?? port.Attr.Inst}";
        if (ShowAlertAsync != null)
            await ShowAlertAsync($"Install {port.Attr.Title}", body, null);
    }

    private static string StoreDisplayName(StoreId id) => id switch
    {
        StoreId.Steam  => "Steam",
        StoreId.Gog    => "GOG",
        StoreId.Epic   => "Epic",
        StoreId.Itch   => "itch.io",
        StoreId.Amazon => "Amazon",
        StoreId.Humble => "Humble",
        _              => id.ToString(),
    };

    private static string FormatBytes(long b)
    {
        if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:F1} GB";
        if (b >= 1_048_576)     return $"{b / 1_048_576.0:F1} MB";
        if (b >= 1024)          return $"{b / 1024.0:F1} KB";
        return $"{b} B";
    }
}
