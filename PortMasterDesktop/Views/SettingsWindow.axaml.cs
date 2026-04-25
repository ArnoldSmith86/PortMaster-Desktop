using Avalonia.Controls;
using PortMasterDesktop.Stores;
using PortMasterDesktop.ViewModels;

namespace PortMasterDesktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            BaseGameStore.PromptDelegate = (title, message) =>
                new InputDialog(title, message).ShowDialog<string?>(this);

            if (DataContext is SettingsViewModel vm)
                await vm.LoadCommand.ExecuteAsync(null);
        };
    }
}
