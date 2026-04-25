using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PortMasterDesktop.Views;

public partial class MessageBox : Window
{
    public MessageBox() => InitializeComponent();

    public MessageBox(string title, string body)
    {
        InitializeComponent();
        TitleBlock.Text = title;
        BodyBlock.Text = body;
        Title = title;
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e) => Close();
}
