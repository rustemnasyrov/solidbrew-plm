using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Экран «Комплект» (6.1 п. 3): дерево выбранной версии головного документа с состояниями и командами
/// на отмеченных (или выделенном) узлах. После команд дерево обновляет главное окно (событие <see cref="WorkflowController.Changed"/>).
/// </summary>
public sealed partial class KitViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loadingVersions;

    public KitViewModel(MainViewModel main) => _main = main;

    private AppSession Session => _main.Session;
    private WorkflowController Flow => _main.Flow;

    [ObservableProperty]
    private Guid? _documentId;

    [ObservableProperty]
    private DocumentSummaryDto? _head;

    [ObservableProperty]
    private int? _selectedVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(CheckOutCommand), nameof(UndoCheckOutCommand), nameof(UpdateToLatestCommand),
        nameof(WhereUsedCommand), nameof(RenameCommand), nameof(ShowDocumentCommand), nameof(OpenFileCommand))]
    private KitNodeViewModel? _selectedNode;

    [ObservableProperty]
    private string _title = "Комплект не выбран. Выберите документ в обзоре и нажмите «Комплект».";

    public ObservableCollection<int> Versions { get; } = new();
    public ObservableCollection<KitNodeViewModel> Roots { get; } = new();

    public bool CanModify => Session.CanModify;
    public bool HasKit => Roots.Count > 0;

    /// <summary>Показать комплект документа (версия по умолчанию — последняя).</summary>
    public async Task LoadAsync(Guid documentId, int? version)
    {
        var outcome = await Flow.Busy.RunAsync("Загрузка документа", (_, ct) => Session.Api.GetDocumentAsync(documentId, ct));
        if (!outcome.Ok) return;
        var detail = outcome.Value!;
        DocumentId = documentId;
        Head = detail.Document;
        _loadingVersions = true;
        Versions.ReplaceWith(detail.Versions.Select(v => v.Number).OrderByDescending(v => v));
        SelectedVersion = version ?? detail.Document.LatestVersion;
        _loadingVersions = false;
        await RefreshAsync();
    }

    partial void OnSelectedVersionChanged(int? value)
    {
        if (!_loadingVersions && value != null) _ = RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (DocumentId is not { } id || SelectedVersion is not { } version) return;
        var ws = Session.Workspace;
        var root = Flow.WorkRoot;
        var outcome = await Flow.Busy.RunAsync("Построение дерева комплекта", (_, ct) => ws.GetKitTreeAsync(id, version, root, ct));
        if (!outcome.Ok) return;
        var keepChecked = new HashSet<Guid>(AllNodes().Where(n => n.IsChecked).Select(n => n.DocumentId));
        var tree = outcome.Value!;
        var rootNode = new KitNodeViewModel(tree, null, this);
        foreach (var n in rootNode.SelfAndDescendants()) n.IsChecked = keepChecked.Contains(n.DocumentId) && !n.IsUnresolved;
        Roots.ReplaceWith(new[] { rootNode });
        SelectedNode = rootNode;
        rootNode.IsSelected = true;
        Title = $"Комплект {Format.Doc(Head?.RelPath ?? tree.RelPath, Head?.Designation ?? tree.Designation)} v{version}" +
                (Head?.Name is { Length: > 0 } n2 ? $" — {n2}" : string.Empty) +
                (root != null && WorkspaceService.IsWorkspace(root) ? $". Рабочая папка: {root}" : ". Рабочая папка не выбрана — локальные состояния не показаны.");
        OnPropertyChanged(nameof(HasKit));
        var count = rootNode.SelfAndDescendants().Count();
        Flow.Busy.Done($"Узлов в дереве: {count}.");
    }

    public IEnumerable<KitNodeViewModel> AllNodes() => Roots.SelectMany(r => r.SelfAndDescendants());

    /// <summary>Узлы для команды: отмеченные флажками, иначе выделенный.</summary>
    public List<KitNodeViewModel> Targets()
    {
        var marked = AllNodes().Where(n => n.IsChecked && !n.IsUnresolved).ToList();
        if (marked.Count > 0) return marked;
        return SelectedNode is { IsUnresolved: false } s ? new List<KitNodeViewModel> { s } : new List<KitNodeViewModel>();
    }

    private List<Guid> TargetIds() => Targets().Select(n => n.DocumentId).Distinct().ToList();

    private bool HasTarget() => SelectedNode is { IsUnresolved: false } || AllNodes().Any(n => n.IsChecked);

    internal void OnCheckedChanged() => this.NotifyCommands();

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var n in AllNodes()) n.IsChecked = false;
    }

    /// <summary>«Открыть»: выделенный узел — его версия в комплекте; по умолчанию весь комплект.</summary>
    [RelayCommand(CanExecute = nameof(HasTarget))]
    private async Task OpenAsync()
    {
        var node = SelectedNode is { IsUnresolved: false } s ? s : Roots.FirstOrDefault();
        if (node == null) return;
        await Flow.OpenAsync(node.DocumentId, node.Version, node.Label);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task CheckOutAsync() => Flow.CheckOutAsync(TargetIds());

    [RelayCommand]
    private Task CheckinAsync() => Flow.CheckinAsync();

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task UndoCheckOutAsync() => Flow.UndoCheckOutAsync(TargetIds());

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task UpdateToLatestAsync() => Flow.UpdateToLatestAsync(TargetIds());

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task WhereUsedAsync()
    {
        var node = Targets().First();
        return _main.ShowDocumentAsync(node.DocumentId, node.Version, DocumentSection.WhereUsed);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private Task ShowDocumentAsync()
    {
        var node = Targets().First();
        return _main.ShowDocumentAsync(node.DocumentId, node.Version, DocumentSection.Properties);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private void Rename()
    {
        var node = Targets().First();
        var root = Flow.WorkRoot;
        var rel = node.RelPath;
        if (root != null && WorkspaceService.IsWorkspace(root))
        {
            var entry = Session.Workspace.LoadState(root).FindByDocument(node.DocumentId);
            if (entry != null) rel = entry.RelPath;
        }
        Flow.Rename(rel);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private void OpenFile()
    {
        var node = Targets().First();
        var root = Flow.WorkRoot;
        if (root == null || !WorkspaceService.IsWorkspace(root))
        {
            Session.Services.Dialogs.ShowError("Открыть файл", new ErrorInfo("Рабочая папка не выбрана. Сначала откройте комплект командой «Открыть»."));
            return;
        }
        var entry = Session.Workspace.LoadState(root).FindByDocument(node.DocumentId);
        Flow.OpenFile(Core.Paths.RelPath.Combine(root, entry?.RelPath ?? node.RelPath));
    }

    [RelayCommand]
    private void Export()
    {
        if (DocumentId is { } id) Flow.Export(id, SelectedVersion);
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var n in AllNodes()) n.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var n in AllNodes()) n.IsExpanded = false;
        if (Roots.FirstOrDefault() is { } r) r.IsExpanded = true;
    }
}

/// <summary>Узел дерева комплекта.</summary>
public sealed partial class KitNodeViewModel : ObservableObject
{
    private readonly KitViewModel _owner;

    public KitNodeViewModel(KitTreeNode node, KitNodeViewModel? parent, KitViewModel owner)
    {
        Node = node;
        Parent = parent;
        _owner = owner;
        _isExpanded = parent == null || parent.Parent == null;
        foreach (var c in node.Children) Children.Add(new KitNodeViewModel(c, this, owner));
    }

    public KitTreeNode Node { get; }
    public KitNodeViewModel? Parent { get; }

    public int Depth => Parent == null ? 0 : Parent.Depth + 1;

    /// <summary>Ширина колонки имени с учётом отступа дерева (чтобы остальные колонки были ровными).</summary>
    public double NameWidth => Math.Max(140, 340 - Depth * 19);
    public ObservableCollection<KitNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.SelectedNode = this;
    }

    partial void OnIsCheckedChanged(bool value) => _owner.OnCheckedChanged();

    public Guid DocumentId => Node.DocumentId;
    public int Version => Node.Version;
    public string RelPath => Node.RelPath;
    public string FileName => Node.FileName;
    public string? Designation => Node.Designation;
    public string? Name => Node.Name;
    public bool IsUnresolved => Node.IsUnresolved;
    public bool IsCycle => Node.IsCycle;
    public string Label => Format.Doc(Node.RelPath.Length > 0 ? Node.RelPath : Node.RefPath, Node.Designation);

    public string VersionText => IsUnresolved ? string.Empty : Node.Version == Node.LatestVersion ? $"v{Node.Version}" : $"v{Node.Version} / v{Node.LatestVersion}";

    public string StateText => Node.StateText + (IsCycle ? ", повтор (цикл ссылок)" : string.Empty);

    public string Tooltip => $"{Node.RelPath}\nСсылка: {Node.RefPath}\nCAD: {Node.Cad} {Node.CadVersion}\n{StateText}";

    public string KindIcon => Node.Kind switch
    {
        DocumentKind.Assembly => "",
        DocumentKind.Drawing => "",
        DocumentKind.Part => "",
        _ => "",
    };

    /// <summary>Основная цветовая метка (по важности).</summary>
    public StateMark Mark
    {
        get
        {
            if (IsUnresolved) return StateMark.Missing;
            if (Node.Lock == LockState.LockedByOther) return StateMark.Other;
            if (Node.Local == LocalFileState.ModifiedLocally) return StateMark.Modified;
            if (Node.Lock == LockState.LockedByMe) return StateMark.Mine;
            if (Node.IsOutdated || Node.HasVersionConflict) return StateMark.Outdated;
            if (Node.Local == LocalFileState.MissingLocally) return StateMark.Missing;
            return StateMark.Free;
        }
    }

    public IEnumerable<KitNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }
}

public enum DocumentSection
{
    Properties = 0,
    History = 1,
    Dependencies = 2,
    WhereUsed = 3,
    Derived = 4,
    Journal = 5,
}
