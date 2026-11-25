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
        DataContextChanged += OnDataContextChanged;
    }

    private ProductWindowViewModel? Vm => DataContext as ProductWindowViewModel;

    private GridLength _previewHeight = new(1, GridUnitType.Star);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProductWindowViewModel old)
        {
            old.Panel.PropertyChanged -= OnPanelPropertyChanged;
            old.ReleasePreviewRequested -= OnReleasePreview;
        }
        if (e.NewValue is ProductWindowViewModel vm)
        {
            vm.Panel.PropertyChanged += OnPanelPropertyChanged;
            vm.ReleasePreviewRequested += OnReleasePreview;
            ApplyPreviewRow(vm.Panel.ShowPreview);
        }
    }

    /// <summary>Перед «Открыть в CAD»: файл не должен быть занят просмотром.</summary>
    private void OnReleasePreview(object? sender, EventArgs e) => Preview.Release();

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodePanelViewModel.ShowPreview) && sender is NodePanelViewModel panel) ApplyPreviewRow(panel.ShowPreview);
    }

    /// <summary>Скрытый просмотр не занимает место; при показе — прежняя высота (её меняет перетаскивание границы).</summary>
    private void ApplyPreviewRow(bool show)
    {
        if (!show && PreviewRow.Height.Value > 0) _previewHeight = PreviewRow.Height;
        PreviewRow.Height = show ? _previewHeight : new GridLength(0);
    }

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

    private ScrollViewer? _treeScroll;

    /// <summary>
    /// Выделенный узел показывается прокруткой только по вертикали: WPF ещё и сдвигает дерево вправо, чтобы строка
    /// с длинным именем влезла, — тогда у верхних уровней не видно значков и стрелок раскрытия. Возвращаем к левому краю.
    /// </summary>
    private void OnTreeItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        _treeScroll ??= FindChild<ScrollViewer>(NodeTree);
        var scroll = _treeScroll;
        if (scroll == null) return;
        Dispatcher.BeginInvoke(() => scroll.ScrollToLeftEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

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
