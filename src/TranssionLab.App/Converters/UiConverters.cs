using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TranssionLab.Core.Enums;

namespace TranssionLab.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ProvenanceToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush VerifiedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
    private static readonly SolidColorBrush DetectedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0xA5, 0xE9)));
    private static readonly SolidColorBrush InferredBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly SolidColorBrush UnknownBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.ToUpperInvariant() ?? string.Empty;
        return text switch
        {
            "VERIFIED" => VerifiedBrush,
            "DETECTED" => DetectedBrush,
            "INFERRED" => InferredBrush,
            _ => UnknownBrush
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

public sealed class DashboardStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)));
    private static readonly SolidColorBrush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
    private static readonly SolidColorBrush ErrBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)));
    private static readonly SolidColorBrush InfoBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly SolidColorBrush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ConnectionDashboardState state)
        {
            return state switch
            {
                ConnectionDashboardState.Connected or ConnectionDashboardState.OperationVerified => OkBrush,
                ConnectionDashboardState.ProtocolAvailable or ConnectionDashboardState.OperationInProgress => InfoBrush,
                ConnectionDashboardState.Unauthorized or ConnectionDashboardState.WaitingForAuthorization or
                ConnectionDashboardState.AuthenticationRequired or ConnectionDashboardState.DetectedButUnidentified => WarnBrush,
                ConnectionDashboardState.OperationFailed or ConnectionDashboardState.ProtocolUnsupported => ErrBrush,
                _ => MutedBrush
            };
        }
        return MutedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
