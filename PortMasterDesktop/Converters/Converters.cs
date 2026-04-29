using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using PortMasterDesktop.Models;

namespace PortMasterDesktop.Converters;

public class NotEmptyConverter : IValueConverter
{
    public static readonly NotEmptyConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => !string.IsNullOrEmpty(v as string);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class InstallStateColorConverter : IValueConverter
{
    public static readonly InstallStateColorConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is PortInstallState s ? s switch
        {
            PortInstallState.Ready          => new SolidColorBrush(Color.Parse("#4CAF50")),
            PortInstallState.NeedsGameFiles => new SolidColorBrush(Color.Parse("#FF9800")),
            PortInstallState.NotInstalled   => new SolidColorBrush(Color.Parse("#6c63ff")),
            PortInstallState.NoPartition    => new SolidColorBrush(Color.Parse("#6c63ff")),
            _ => new SolidColorBrush(Color.Parse("#607D8B")),
        } : new SolidColorBrush(Color.Parse("#607D8B"));
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class InstallStateLabelConverter : IValueConverter
{
    public static readonly InstallStateLabelConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is PortInstallState s ? s switch
        {
            PortInstallState.Ready          => "INSTALLED",
            PortInstallState.NeedsGameFiles => "NEEDS FILES",
            PortInstallState.NotInstalled   => "AVAILABLE",
            PortInstallState.NoPartition    => "AVAILABLE",
            _                               => "",
        } : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class InstallStateVisibleConverter : IValueConverter
{
    public static readonly InstallStateVisibleConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is PortInstallState s && s is PortInstallState.Ready or PortInstallState.NeedsGameFiles;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class StoreInitialConverter : IValueConverter
{
    public static readonly StoreInitialConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is string s && s.Length > 0 ? s[0].ToString().ToUpperInvariant() : "?";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class EmptyStringToBoolConverter : IValueConverter
{
    public static readonly EmptyStringToBoolConverter Instance = new();
    // Returns true (show placeholder) when the string is empty/null
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => string.IsNullOrEmpty(v as string);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class BoolToBorderBrushConverter : IValueConverter
{
    public static readonly BoolToBorderBrushConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is true
            ? new SolidColorBrush(Color.Parse("#6c63ff"))
            : new SolidColorBrush(Color.Parse("#2d2d52"));
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class PartitionStatusColorConverter : IValueConverter
{
    public static readonly PartitionStatusColorConverter Instance = new();
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        var text = v as string ?? "";
        return text.Contains("No SD") || text.Contains("Plug")
            ? new SolidColorBrush(Color.Parse("#FF9800"))
            : new SolidColorBrush(Color.Parse("#4CAF50"));
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class WidthToHeightConverter : IValueConverter
{
    public static readonly WidthToHeightConverter Instance = new();
    // Convert width to height maintaining 2:3 portrait aspect ratio (height = width * 1.5)
    public object Convert(object? v, Type t, object? p, CultureInfo c)
        => v is double w && w > 0 ? w * 1.5 : 240.0;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}

public class AspectRatioToHeightConverter : IValueConverter
{
    public static readonly AspectRatioToHeightConverter Instance = new();
    // Convert width and aspect ratio to height. Parameters: width|aspectRatio (e.g., "160|1.333")
    public object Convert(object? v, Type t, object? p, CultureInfo c)
    {
        if (v is not double w || w <= 0) return 240.0;

        var param = p as string ?? "2|3";
        var parts = param.Split('|');
        if (parts.Length != 2) return w * 1.5;

        if (!double.TryParse(parts[0], out var aspectNum) || !double.TryParse(parts[1], out var aspectDen))
            return w * 1.5;

        var ratio = aspectNum / aspectDen;
        return w / ratio;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotSupportedException();
}
