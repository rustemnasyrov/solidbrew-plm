using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    private void BuildColumns()
    {
        PropertiesGrid.Columns.Clear();
        PropertiesGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Свойство",
            Binding = new Binding(nameof(PropertyPivotRow.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            FontWeight = FontWeights.SemiBold,
        });
        if (_vm == null) return;
        for (var i = 0; i < _vm.Columns.Count; i++)
        {
            PropertiesGrid.Columns.Add(new DataGridTextColumn
            {
                Header = _vm.Columns[i],
                Binding = new Binding($"{nameof(PropertyPivotRow.Values)}[{i}]"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 90,
            });
        }
    }
}
