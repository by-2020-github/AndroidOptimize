using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AndroidOptimize.Core.Models;

namespace AndroidOptimize.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Low = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush Medium = new(Color.FromRgb(0xEF, 0x6C, 0x00));
    private static readonly SolidColorBrush High = new(Color.FromRgb(0xC6, 0x28, 0x28));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            RiskLevel.High => High,
            RiskLevel.Medium => Medium,
            _ => Low,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush Downgraded = new(Color.FromRgb(0xEF, 0x6C, 0x00));
    private static readonly SolidColorBrush Failed = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush Neutral = new(Color.FromRgb(0x61, 0x61, 0x61));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ActionStatus.Success => Success,
            ActionStatus.Downgraded => Downgraded,
            ActionStatus.Failed => Failed,
            _ => Neutral,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Error = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush Success = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush Warn = new(Color.FromRgb(0xEF, 0x6C, 0x00));
    private static readonly SolidColorBrush Info = new(Color.FromRgb(0x42, 0x42, 0x42));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "错误" => Error,
            "成功" => Success,
            "提示" => Warn,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class SourceToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ai = new(Color.FromRgb(0x6A, 0x1B, 0x9A));
    private static readonly SolidColorBrush List = new(Color.FromRgb(0x37, 0x47, 0x4F));
    private static readonly SolidColorBrush Policy = new(Color.FromRgb(0x00, 0x69, 0x5C));
    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0xEF, 0x6C, 0x00));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "AI 建议" => Ai,
            "策略" => Policy,
            "未知" => Unknown,
            _ => List,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
