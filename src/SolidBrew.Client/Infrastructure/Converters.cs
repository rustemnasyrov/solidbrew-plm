using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Цвет метки состояния документа. Свободен — без метки.</summary>
public sealed class MarkToBrushConverter : IValueConverter
{
    // Та же схема, что у кружков дерева (StatusDotToBrushConverter): синий — взят мной, красный — изменён,
    // жёлтый — устарел / пересохранить, фиолетовый — взят другим, серый — нет файла; черновик — бирюзовый.
    public static readonly Brush Free = Brushes.Transparent;
    public static readonly Brush Mine = StatusDotToBrushConverter.Blue;
    public static readonly Brush Other = StatusDotToBrushConverter.Purple;
    public static readonly Brush Modified = StatusDotToBrushConverter.Red;
    public static readonly Brush Draft = StatusDotToBrushConverter.Teal;
    public static readonly Brush Outdated = StatusDotToBrushConverter.Yellow;
    public static readonly Brush Missing = StatusDotToBrushConverter.Gray;

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

/// <summary>Цвет кружка состояния документа (<see cref="StatusDot"/>).</summary>
public sealed class StatusDotToBrushConverter : IValueConverter
{
    public static readonly Brush Green = Frozen("#2E9E44");
    public static readonly Brush Blue = Frozen("#1E6FD9");
    public static readonly Brush Red = Frozen("#D93025");
    public static readonly Brush Yellow = Frozen("#F2B705");
    public static readonly Brush Purple = Frozen("#7B1FA2");
    public static readonly Brush Gray = Frozen("#9AA0A6");
    public static readonly Brush Teal = Frozen("#00897B");

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusDot.InStorage => Green,
        StatusDot.Mine => Blue,
        StatusDot.MineModified => Red,
        StatusDot.NeedsResave => Yellow,
        StatusDot.Other => Purple,
        StatusDot.Missing => Gray,
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
        new LegendEntry(StatusDotToBrushConverter.Green, "сдан в хранилище, правок нет"),
        new LegendEntry(StatusDotToBrushConverter.Blue, "взят мной, не изменён"),
        new LegendEntry(StatusDotToBrushConverter.Red, "изменён — сдайте"),
        new LegendEntry(StatusDotToBrushConverter.Yellow, "сборку / чертёж надо пересохранить; есть новая версия"),
        new LegendEntry(StatusDotToBrushConverter.Purple, "взят другим"),
        new LegendEntry(StatusDotToBrushConverter.Teal, "черновик (новый файл)"),
        new LegendEntry(StatusDotToBrushConverter.Gray, "нет файла / не учтён"),
    };
}

/// <summary>
/// Кнопка с выпадающим меню: <c>inf:DropDown.Menu</c> открывается по щелчку под кнопкой
/// (DataContext меню — DataContext кнопки).
/// </summary>
public static class DropDown
{
    public static readonly DependencyProperty MenuProperty = DependencyProperty.RegisterAttached(
        "Menu", typeof(System.Windows.Controls.ContextMenu), typeof(DropDown), new PropertyMetadata(null, OnMenuChanged));

    public static System.Windows.Controls.ContextMenu? GetMenu(DependencyObject d) => (System.Windows.Controls.ContextMenu?)d.GetValue(MenuProperty);

    public static void SetMenu(DependencyObject d, System.Windows.Controls.ContextMenu? value) => d.SetValue(MenuProperty, value);

    private static void OnMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.ButtonBase button) return;
        button.Click -= OnClick;
        if (e.NewValue != null) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        var button = (System.Windows.Controls.Primitives.ButtonBase)sender;
        var menu = GetMenu(button);
        if (menu == null) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
