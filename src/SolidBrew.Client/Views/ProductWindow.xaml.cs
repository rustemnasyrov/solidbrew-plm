using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Views;

public partial class ProductWindow : Window
{
    public ProductWindow()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(new FocusCommand(() =>
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }), Key.F, ModifierKeys.Control));
    }

    private ProductWindowViewModel? Vm => DataContext as ProductWindowViewModel;

    /// <summary>Правый щелчок выделяет узел, чтобы контекстное меню относилось к нему.</summary>
    private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnSwitcherOpened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => SwitcherSearch.Focus());

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        var vm = Vm;
        if (vm == null || !vm.Busy.IsBusy) return;
        if (MessageBox.Show(this, "Идёт операция. Прервать её и закрыть окно?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        vm.Busy.CancelCommand.Execute(null);
    }

    private sealed class FocusCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
