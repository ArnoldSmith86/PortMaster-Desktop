using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using PortMasterDesktop.Models;

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

    private GameMatch? _currentGame;

    public GameCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateImageHeight();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentGame != null)
            _currentGame.PropertyChanged -= OnGamePropertyChanged;

        _currentGame = DataContext as GameMatch;
        if (_currentGame != null)
            _currentGame.PropertyChanged += OnGamePropertyChanged;

        UpdateImageHeight();
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameMatch.DisplayImageAspectRatio) or nameof(GameMatch.UsePortMasterImages))
            UpdateImageHeight();
    }

    private void UpdateImageHeight()
    {
        if (_currentGame == null) return;

        var imagePanel = this.FindControl<Panel>("ImagePanel");
        if (imagePanel == null) return;

        var parentWidth = this.Bounds.Width;
        if (parentWidth <= 0) return;

        var height = parentWidth / _currentGame.DisplayImageAspectRatio;
        imagePanel.Height = height;
    }
}
