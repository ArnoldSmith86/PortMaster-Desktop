using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public ObservableCollection<StoreAccountItem> Accounts { get; } = [];
    [ObservableProperty] private bool _isLoading;

    public SettingsViewModel(IEnumerable<IGameStore> stores)
    {
        foreach (var s in stores)
            Accounts.Add(new StoreAccountItem(s));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try { await Task.WhenAll(Accounts.Select(a => a.RefreshAsync())); }
        finally { IsLoading = false; }
    }
}
