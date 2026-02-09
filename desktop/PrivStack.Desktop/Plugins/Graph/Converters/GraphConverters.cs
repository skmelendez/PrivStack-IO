using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace PrivStack.Desktop.Plugins.Graph.Converters;

/// <summary>
/// Converts a bool (isCollapsed) to a sidebar width.
/// When collapsed: returns 48. When expanded: reads ThemeSidebarWidth from app resources.
/// </summary>
public class BoolToWidthConverter : IValueConverter
{
    public static readonly BoolToWidthConverter Instance = new();
    private const double CollapsedWidth = 48;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isCollapsed && isCollapsed)
            return CollapsedWidth;

        if (Application.Current?.TryGetResource("ThemeSidebarWidth",
                Application.Current.ActualThemeVariant, out var resource) == true
            && resource is double width)
        {
            return width;
        }

        return 300.0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts bool to one of two strings. Parameter format: "trueValue,falseValue"
/// </summary>
public class BoolToStringConverter : IValueConverter
{
    public static readonly BoolToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2)
                return boolValue ? parts[0] : parts[1];
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts bool to one of two PathIcon data strings. Parameter format: "trueData,falseData"
/// </summary>
public class BoolToPathIconConverter : IValueConverter
{
    public static readonly BoolToPathIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && parameter is string param)
        {
            var parts = param.Split(',');
            if (parts.Length == 2)
            {
                var pathData = boolValue ? parts[0] : parts[1];
                return StreamGeometry.Parse(pathData);
            }
        }
        return null!;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
