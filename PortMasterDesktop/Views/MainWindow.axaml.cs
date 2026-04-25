using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PortMasterDesktop.Models;
using PortMasterDesktop.ViewModels;

namespace PortMasterDesktop.Views;

public partial class MainWindow : Window
{
    private GameCard? _selectedCard;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ShowAlertAsync = ShowAlertAsync;
                await vm.LoadCommand.ExecuteAsync(null);
            }
        };
    }

    private void OnFilterAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SetFilterCommand.Execute(LibraryFilter.All);
        FilterAllBtn.Classes.Add("active");
        FilterAvailBtn.Classes.Remove("active");
    }

    private void OnFilterAvail(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.SetFilterCommand.Execute(LibraryFilter.PortMasterAvailable);
        FilterAvailBtn.Classes.Add("active");
        FilterAllBtn.Classes.Remove("active");
    }

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var settingsVm = App.Services.GetRequiredService<SettingsViewModel>();
        var dlg = new SettingsWindow { DataContext = settingsVm };
        await dlg.ShowDialog(this);
    }

    private void OnGameCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not GameCard card || DataContext is not MainViewModel vm) return;

        if (_selectedCard == card)
        {
            // Second click → deselect
            _selectedCard.IsSelected = false;
            _selectedCard = null;
            vm.SelectGameCommand.Execute(null);
        }
        else
        {
            if (_selectedCard != null) _selectedCard.IsSelected = false;
            _selectedCard = card;
            card.IsSelected = true;
            if (card.DataContext is GameMatch match)
                vm.SelectGameCommand.Execute(match);
        }
        e.Handled = true;
    }

    private void OnScrollViewerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Deselect when clicking the empty grid background
        if (e.Source is not GameCard && (e.Source as Avalonia.Visual)?.FindAncestorOfType<GameCard>() == null)
        {
            if (_selectedCard != null)
            {
                _selectedCard.IsSelected = false;
                _selectedCard = null;
            }
            if (DataContext is MainViewModel vm)
                vm.SelectGameCommand.Execute(null);
        }
    }

    private Task ShowAlertAsync(string title, string message)
        => new MessageBox(title, message).ShowDialog(this);
}
