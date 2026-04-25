using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortMasterDesktop.Views;

public partial class MessageBox : Window
{
    public MessageBox() => InitializeComponent();

    public MessageBox(string title, string body, string? command = null)
    {
        InitializeComponent();
        TitleBlock.Text = title;
        BodyBlock.Text = body;
        Title = title;
        if (command != null)
        {
            CommandBox.Text = command;
            CommandRow.IsVisible = true;
        }
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && CommandBox.Text != null)
            await clipboard.SetTextAsync(CommandBox.Text);
    }
}
