using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Панель выбранного узла: шапка, 3–4 применимых действия (остальное — в «⋯») и вкладки из <see cref="NodeTabRegistry"/>.
/// Последняя выбранная вкладка запоминается для каждого типа узла.
/// </summary>
public sealed partial class NodePanelViewModel : ObservableObject
{
    private readonly INodeHost _host;
    private readonly NodeTabRegistry _registry;
    private readonly Func<NodeActionKind, ICommand> _commands;
    private readonly Dictionary<NodeType, string> _lastTab = new();
    private NodeContext? _context;
    private bool _switching;

    public NodePanelViewModel(INodeHost host, NodeTabRegistry registry, Func<NodeActionKind, ICommand> commands)
    {
        _host = host;
        _registry = registry;
        _commands = commands;
    }

    [ObservableProperty]
    private ProductNodeViewModel? _node;

    public ObservableCollection<NodeAction> PrimaryActions { get; } = new();
    public ObservableCollection<NodeAction> MoreActions { get; } = new();

    /// <summary>Все действия узла (для контекстного меню дерева).</summary>
    public ObservableCollection<NodeAction> MenuActions { get; } = new();

    public ObservableCollection<NodeTabItem> Tabs { get; } = new();

    [ObservableProperty]
    private NodeTabItem? _selectedTab;

    /// <summary>Показывать просмотр модели (eDrawings) над вкладками.</summary>
    [ObservableProperty]
    private bool _showPreview = true;

    public bool HasNode => Node != null;
    public bool HasMore => MoreActions.Count > 0;
    public bool HasTabs => Tabs.Count > 0;

    public IEnumerable<NodeActionKind> PrimaryKinds => PrimaryActions.Select(a => a.Kind);
    public IEnumerable<NodeActionKind> MoreKinds => MoreActions.Select(a => a.Kind);

    /// <summary>Показать узел (или пустую панель).</summary>
    public void Show(ProductNodeViewModel? node, bool canModify, bool isProductRoot)
    {
        _context?.Cancel();
        _context = null;
        Node = node;

        var (primary, more) = NodeActionRules.For(node, canModify, isProductRoot, _host.Session.IsAdmin);
        PrimaryActions.ReplaceWith(primary.Select(Make));
        MoreActions.ReplaceWith(more.Select(Make));
        // Контекстное меню дерева: действия узла и всегда — «Добавить проект…».
        var menu = PrimaryActions.Concat(MoreActions).ToList();
        if (canModify && menu.All(a => a.Kind != NodeActionKind.AddProject)) menu.Add(Make(NodeActionKind.AddProject));
        MenuActions.ReplaceWith(menu);

        _switching = true;
        try
        {
            if (node == null)
            {
                Tabs.Clear();
                SelectedTab = null;
            }
            else
            {
                _context = new NodeContext(node, _host);
                var ctx = _context;
                Tabs.ReplaceWith(_registry.For(ctx).Select(t => new NodeTabItem(t.Id, t.Header, t.CreateViewModel(ctx))));
                SelectedTab = (_lastTab.TryGetValue(node.Type, out var id) ? Tabs.FirstOrDefault(t => t.Id == id) : null) ?? Tabs.FirstOrDefault();
            }
        }
        finally
        {
            _switching = false;
        }
        OnPropertyChanged(nameof(HasNode));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(HasTabs));
        _ = LoadSelectedAsync();
    }

    /// <summary>
    /// Меню дерева для нескольких выделенных узлов: действия, выполняемые разом (<see cref="NodeActionRules.Batch"/>),
    /// применимые хотя бы к одному из них, — с числом узлов, к которым применятся.
    /// </summary>
    public void ShowSelectionMenu(IReadOnlyList<ProductNodeViewModel> nodes, bool canModify, Func<ProductNodeViewModel, bool> isProductRoot)
    {
        var menu = new List<NodeAction>();
        foreach (var kind in NodeActionRules.Batch)
        {
            var count = nodes.Count(n => NodeActionRules.Applies(n, kind, canModify, isProductRoot(n)));
            if (count == 0) continue;
            var text = NodeActionRules.Text(kind);
            var dots = text.EndsWith('…');
            // «Сдать» сдаёт всё изменённое рабочей папки (окно сдачи), без числа.
            var label = kind == NodeActionKind.Checkin ? text : $"{text.TrimEnd('…')} ({count} из {nodes.Count}){(dots ? "…" : string.Empty)}";
            menu.Add(new NodeAction(kind, label, NodeActionRules.Glyph(kind), _commands(kind), NodeActionRules.ToolTip(kind)));
        }
        if (canModify) menu.Add(Make(NodeActionKind.AddProject));
        MenuActions.ReplaceWith(menu);
    }

    private NodeAction Make(NodeActionKind kind) =>
        new(kind, NodeActionRules.Text(kind), NodeActionRules.Glyph(kind), _commands(kind), NodeActionRules.ToolTip(kind));

    partial void OnSelectedTabChanged(NodeTabItem? value)
    {
        if (value == null || Node == null) return;
        _lastTab[Node.Type] = value.Id;
        if (!_switching) _ = LoadSelectedAsync();
    }

    /// <summary>Загрузить содержимое выбранной вкладки.</summary>
    public Task LoadSelectedAsync() => SelectedTab?.Content is NodeTabViewModel t ? t.EnsureLoadedAsync() : Task.CompletedTask;

    /// <summary>Перейти на вкладку (например, «Где применяется» из меню).</summary>
    public bool SelectTab(string id)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == id);
        if (tab == null) return false;
        SelectedTab = tab;
        return true;
    }
}
