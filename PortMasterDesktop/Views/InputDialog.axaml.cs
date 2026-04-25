using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortMasterDesktop.Views;

public partial class InputDialog : Window
{
    public InputDialog() => InitializeComponent();

    public InputDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleBlock.Text = title;
        MessageBlock.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(InputBox.Text);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
