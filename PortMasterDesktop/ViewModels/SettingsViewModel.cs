using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortMasterDesktop.Services;
using PortMasterDesktop.Stores;

namespace PortMasterDesktop.ViewModels;

public partial class StoreAccountItem : ObservableObject
{
    public readonly IGameStore Store;

    public string StoreName => Store.DisplayName;

    [ObservableProperty] private bool _isAuthenticated;
    [ObservableProperty] private string _accountName = "";
    [ObservableProperty] private bool _isBusy;

    public StoreAccountItem(IGameStore store) => Store = store;

    public async Task RefreshAsync()
    {
        IsAuthenticated = await Store.IsAuthenticatedAsync();
        AccountName = IsAuthenticated ? (await Store.GetAccountNameAsync() ?? "") : "";
    }

    [RelayCommand]
    public async Task LoginAsync()
    {
        IsBusy = true;
        try
        {
            if (await Store.AuthenticateAsync())
            {
                IsAuthenticated = true;
                AccountName = await Store.GetAccountNameAsync() ?? "";
            }
        }
        catch { /* auth failure — store returns false, exceptions shouldn't escape */ }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public async Task LogoutAsync()
    {
        IsBusy = true;
        try
        {
            await Store.LogoutAsync();
            IsAuthenticated = false;
            AccountName = "";
        }
        finally { IsBusy = false; }
    }

    // Runs AuthenticateAsync again while keeping the session (for adding API keys, etc.)
    [RelayCommand]
    public async Task ConfigureAsync()
    {
        IsBusy = true;
        try
        {
            await Store.AuthenticateAsync();
            AccountName = await Store.GetAccountNameAsync() ?? "";
        }
        finally { IsBusy = false; }
    }
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly CacheService _cache;

    public ObservableCollection<StoreAccountItem> Accounts { get; } = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _usePortMasterImages;

    public string Version => typeof(SettingsViewModel).Assembly
        .GetName().Version?.ToString(3) ?? "1.0.0";

    public SettingsViewModel(IEnumerable<IGameStore> stores, CacheService cache)
    {
        _cache = cache;
        foreach (var s in stores)
            Accounts.Add(new StoreAccountItem(s));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        // Fast path: persisted settings only — no per-store HTTP calls.
        // The Settings overlay triggers RefreshAccountsAsync separately when opened.
        var saved = await _cache.LoadJsonAsync<bool>("use_portmaster_images");
        UsePortMasterImages = saved;
    }

    [RelayCommand]
    public async Task RefreshAccountsAsync()
    {
        IsLoading = true;
        try { await Task.WhenAll(Accounts.Select(a => a.RefreshAsync())); }
        finally { IsLoading = false; }
    }

    partial void OnUsePortMasterImagesChanged(bool value)
    {
        _ = _cache.SaveJsonAsync("use_portmaster_images", value);
    }
}
