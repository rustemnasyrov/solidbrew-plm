using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Views;

/// <summary>Столбцы таблицы свойств зависят от конфигураций документа — строятся здесь.</summary>
public partial class CardTabView : UserControl
{
    private CardTabViewModel? _vm;

    public CardTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as CardTabViewModel);
    }

    private void Attach(CardTabViewModel? vm)
    {
        if (_vm != null) _vm.PropertyChanged -= OnChanged;
        _vm = vm;
        if (_vm != null) _vm.PropertyChanged += OnChanged;
        BuildColumns();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardTabViewModel.Columns)) BuildColumns();
    }

    /// <summary>
    /// Широкая таблица («По конфигурациям») — свойство и столбец на каждую конфигурацию; две половины (одно значение) —
    /// свойство и «Значение» в каждой.
    /// </summary>
    private void BuildColumns()
    {
        var columns = _vm?.Columns ?? Array.Empty<string>();
        Fill(PropertiesGrid, columns.Count);
        Fill(LeftGrid, Math.Min(columns.Count, 1));
        Fill(RightGrid, Math.Min(columns.Count, 1));

        void Fill(DataGrid grid, int count)
        {
            grid.Columns.Clear();
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Свойство",
                Binding = new Binding(nameof(PropertyPivotRow.Name)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                FontWeight = FontWeights.SemiBold,
            });
            for (var i = 0; i < count; i++)
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = columns[i],
                    CellTemplate = CellTemplate($"{nameof(PropertyPivotRow.Cells)}[{i}]"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    MinWidth = 90,
                });
            }
        }
    }

    /// <summary>
    /// Ячейка значения: текст; пустое свойство, которое можно заполнить, — поле ввода (заполненное подсвечивается
    /// до записи в файл).
    /// </summary>
    private DataTemplate CellTemplate(string cell)
    {
        var boolVis = (IValueConverter)FindResource("BoolVis");
        var grid = new FrameworkElementFactory(typeof(Grid));

        // Заполненное в файле: выделить и скопировать можно, изменить — нет (только в CAD).
        var text = new FrameworkElementFactory(typeof(TextBox));
        text.SetBinding(TextBox.TextProperty, new Binding(cell + "." + nameof(PropertyCell.Value)) { Mode = BindingMode.OneWay });
        text.SetValue(TextBoxBase.IsReadOnlyProperty, true);
        text.SetValue(TextBoxBase.IsReadOnlyCaretVisibleProperty, false);
        text.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        text.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        text.SetValue(Control.PaddingProperty, new Thickness(0));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetBinding(UIElement.VisibilityProperty, new Binding(cell + "." + nameof(PropertyCell.IsEditable)) { Converter = boolVis, ConverterParameter = "invert" });
        grid.AppendChild(text);

        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetBinding(TextBox.TextProperty, new Binding(cell + "." + nameof(PropertyCell.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(UIElement.VisibilityProperty, new Binding(cell + "." + nameof(PropertyCell.IsEditable)) { Converter = boolVis });
        box.SetValue(Control.PaddingProperty, new Thickness(3, 1, 3, 1));
        box.SetValue(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xD5, 0xDB, 0xE3)));
        box.SetValue(FrameworkElement.ToolTipProperty, "Пусто в файле. Введите значение — оно будет записано в файл CAD.");
        var changed = new Style(typeof(TextBox), TryFindResource(typeof(TextBox)) as Style);
        var trigger = new DataTrigger { Binding = new Binding(cell + "." + nameof(PropertyCell.IsChanged)), Value = true };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xC4))));
        trigger.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        changed.Triggers.Add(trigger);
        box.SetValue(FrameworkElement.StyleProperty, changed);
        grid.AppendChild(box);

        return new DataTemplate { VisualTree = grid };
    }
}
