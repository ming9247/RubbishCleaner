using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RubbishCleaner.Core.Models;

namespace RubbishCleaner.App.Infrastructure;

/// <summary>风险等级 → 颜色（绿 / 黄 / 红）。</summary>
public sealed class RiskLevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Normal = new(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly SolidColorBrush Suspicious = new(Color.FromRgb(0xD8, 0x9B, 0x00));
    private static readonly SolidColorBrush HighRisk = new(Color.FromRgb(0xD1, 0x3A, 0x3A));

    static RiskLevelToBrushConverter()
    {
        Normal.Freeze();
        Suspicious.Freeze();
        HighRisk.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RiskLevel.HighRisk => HighRisk,
        RiskLevel.Suspicious => Suspicious,
        _ => Normal,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>证据严重程度 → 颜色。</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = new(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xD8, 0x9B, 0x00));
    private static readonly SolidColorBrush Critical = new(Color.FromRgb(0xD1, 0x3A, 0x3A));

    static SeverityToBrushConverter()
    {
        Info.Freeze();
        Warning.Freeze();
        Critical.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        EvidenceSeverity.Critical => Critical,
        EvidenceSeverity.Warning => Warning,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔值 → 可见性（true 可见）。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔值 → 可见性（false 可见），用于"无数据"提示。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
