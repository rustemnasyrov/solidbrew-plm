using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Цвет метки состояния документа.</summary>
public sealed class MarkToBrushConverter : IValueConverter
{
    public static readonly Brush Free = Frozen("#2E7D32");
    public static readonly Brush Mine = Frozen("#1565C0");
    public static readonly Brush Other = Frozen("#C62828");
    public static readonly Brush Modified = Frozen("#EF6C00");
    public static readonly Brush Draft = Frozen("#7B1FA2");
    public static readonly Brush Outdated = Frozen("#C9A000");
    public static readonly Brush Missing = Frozen("#616161");

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StateMark.Free => Free,
        StateMark.Mine => Mine,
        StateMark.Other => Other,
        StateMark.Modified => Modified,
        StateMark.Draft => Draft,
        StateMark.Outdated => Outdated,
        StateMark.Missing => Missing,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true → Visible (параметр «invert» — наоборот).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is string s && s == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>null или пустая строка → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is string s ? s.Length > 0 : value != null;
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Число &gt; 0 → Visible.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is int i && i > 0;
        if (parameter is string p && p == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Инверсия bool.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed record LegendEntry(Brush Brush, string Text);

/// <summary>Легенда цветовых меток.</summary>
public static class Legend
{
    public static IReadOnlyList<LegendEntry> Items { get; } = new[]
    {
        new LegendEntry(MarkToBrushConverter.Free, "свободен"),
        new LegendEntry(MarkToBrushConverter.Mine, "взят мной"),
        new LegendEntry(MarkToBrushConverter.Other, "взят другим"),
        new LegendEntry(MarkToBrushConverter.Modified, "изменён локально"),
        new LegendEntry(MarkToBrushConverter.Draft, "черновик"),
        new LegendEntry(MarkToBrushConverter.Outdated, "устарел / есть новая версия"),
        new LegendEntry(MarkToBrushConverter.Missing, "отсутствует"),
    };
}
