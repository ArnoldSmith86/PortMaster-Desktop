using AsyncImageLoader;
using AsyncImageLoader.Loaders;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PortMasterDesktop.PortMaster;
using PortMasterDesktop.Services;
using PortMasterDesktop.Stores;
using PortMasterDesktop.ViewModels;
using PortMasterDesktop.Views;

namespace PortMasterDesktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Use disk-caching image loader so web images are persisted across runs
        ImageLoader.AsyncImageLoader = new DiskCachedWebImageLoader(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PortMasterDesktop", "ImageCache"));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var logService = new LogService();
        logService.Initialize();

        var services = new ServiceCollection();

        services.AddSingleton(logService);
        services.AddSingleton<CacheService>();
        services.AddSingleton<PortMasterClient>();
        services.AddSingleton<PartitionService>();
        services.AddSingleton<InstallService>();

        // Two Steam adapters: local (reads local installation) or remote (SteamKit2 login)
        // Both are always registered; LocalSteamStore auto-authenticates when Steam is found.
        services.AddSingleton<IGameStore, LocalSteamStore>();
        services.AddSingleton<IGameStore, RemoteSteamStore>();

        services.AddSingleton<IGameStore, GogStore>();
        services.AddSingleton<IGameStore, EpicStore>();
        services.AddSingleton<IGameStore, ItchStore>();
        services.AddSingleton<IGameStore, AmazonStore>();
        services.AddSingleton<IGameStore, HumbleStore>();

        services.AddSingleton<SteamDepotService>();
        services.AddSingleton<SteamGridDbService>();
        services.AddSingleton<PortMasterImagesService>();
        services.AddSingleton<LibraryService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
