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
            // Щелчок по одному из нескольких выделенных — выделение сохраняется, меню относится ко всем.
            if (item.DataContext is ProductNodeViewModel { IsMultiSelected: true } && Vm?.Tree.HasMultiSelection == true) return;
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>Ctrl + щелчок — добавить узел в выделение (или убрать), Shift + щелчок — выделить диапазон.</summary>
    private void OnTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var toggle = mods.HasFlag(ModifierKeys.Control);
        var range = mods.HasFlag(ModifierKeys.Shift);
        if (!toggle && !range || Vm is not { } vm) return;
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not TreeViewItem)
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return; // стрелка раскрытия
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        if (d is not TreeViewItem { DataContext: ProductNodeViewModel node } item) return;
        vm.Tree.ExtendSelection(node, toggle, range);
        item.Focus();
        e.Handled = true;
    }

    /// <summary>Файлы CAD из Проводника, перетаскиваемые на дерево (пусто — перетаскивание не принимается).</summary>
    private IReadOnlyList<string> DroppedProjectFiles(DragEventArgs e) =>
        Vm != null && e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths
            ? Vm.DroppableProjectFiles(paths)
            : Array.Empty<string>();

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        var ok = DroppedProjectFiles(e).Count > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    /// <summary>Перетаскивание сборки (детали, чертежа) на дерево — «Добавить проект» с этим файлом.</summary>
    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        var files = DroppedProjectFiles(e);
        e.Handled = true;
        if (files.Count == 0 || Vm is not { } vm) return;
        // Окно постановки модальное: открываем его после завершения перетаскивания, чтобы не держать Проводник.
        Dispatcher.BeginInvoke(async () =>
        {
            Activate();
            await vm.AddProjectsAsync(files);
        });
    }

    private void SetDropHighlight(bool on)
    {
        NodeTree.BorderThickness = new Thickness(on ? 2 : 0);
        NodeTree.BorderBrush = on ? SolidBrew.Client.Infrastructure.StatusDotToBrushConverter.Blue : null;
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
