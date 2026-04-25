using Avalonia;
using Avalonia.Controls;

namespace PortMasterDesktop.Views;

public partial class GameCard : UserControl
{
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<GameCard, bool>(nameof(IsSelected));

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public GameCard() => InitializeComponent();
}
