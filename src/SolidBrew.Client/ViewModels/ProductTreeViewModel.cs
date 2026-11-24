using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Тип узла дерева изделия: от него зависят значок, вкладки и действия панели.</summary>
public enum NodeType
{
    Part,
    Assembly,
    Drawing,
    Other,
    /// <summary>Новый файл рабочей папки, ещё не поставленный на учёт.</summary>
    Draft,
    /// <summary>Ссылка на файл, не учтённый в PDM.</summary>
    Unresolved,
    /// <summary>Служебный узел «Новые файлы».</summary>
    Group,
}

/// <summary>
/// Узел дерева изделия: документ комплекта (по <see cref="KitTreeNode"/>) с состоянием из рабочей папки,
/// черновик (новый файл) или служебный узел «Новые файлы».
/// </summary>
public sealed partial class ProductNodeViewModel : ObservableObject
{
    private readonly ProductTreeViewModel? _owner;

    /// <summary>Документ комплекта.</summary>
    public ProductNodeViewModel(KitTreeNode kit, FileStatus? file, ProductNodeViewModel? parent, ProductTreeViewModel? owner)
    {
        Kit = kit;
        File = file;
        Parent = parent;
        _owner = owner;
        Type = kit.IsUnresolved ? NodeType.Unresolved : kit.Kind switch
        {
            DocumentKind.Part => NodeType.Part,
            DocumentKind.Assembly => NodeType.Assembly,
            DocumentKind.Drawing => NodeType.Drawing,
            _ => NodeType.Other,
        };
        Key = (parent?.Key ?? string.Empty) + "/" + (kit.IsUnresolved ? "?" + kit.RefPath : kit.DocumentId.ToString("N"));
    }

    /// <summary>Черновик рабочей папки.</summary>
    public ProductNodeViewModel(FileStatus draft, ProductNodeViewModel? parent, ProductTreeViewModel? owner)
    {
        File = draft;
        Parent = parent;
        _owner = owner;
        Type = NodeType.Draft;
        Key = "draft:" + draft.RelPath.ToUpperInvariant();
    }

    /// <summary>Служебный узел.</summary>
    public ProductNodeViewModel(string title, ProductTreeViewModel? owner)
    {
        _groupTitle = title;
        _owner = owner;
        Type = NodeType.Group;
        Key = "group:" + title;
    }

    private readonly string? _groupTitle;

    public NodeType Type { get; }
    public KitTreeNode? Kit { get; }
    public FileStatus? File { get; }
    public ProductNodeViewModel? Parent { get; }
    public ObservableCollection<ProductNodeViewModel> Children { get; } = new();

    /// <summary>Ключ узла (путь от корня) — чтобы сохранить раскрытие и выделение при обновлении дерева.</summary>
    public string Key { get; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Виден при текущем фильтре.</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>Совпал с фильтром (подсвечивается).</summary>
    [ObservableProperty]
    private bool _isMatch;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && _owner != null) _owner.SelectedNode = this;
    }

    public bool IsDocument => Kit != null && !Kit.IsUnresolved;
    public bool IsDraft => Type == NodeType.Draft;
    public bool IsGroup => Type == NodeType.Group;
    public Guid? DocumentId => IsDocument ? Kit!.DocumentId : null;

    /// <summary>Версия в комплекте изделия.</summary>
    public int? Version => IsDocument ? Kit!.Version : null;

    /// <summary>Версия в рабочей папке.</summary>
    public int? LocalVersion => File?.LocalVersion ?? Kit?.LocalVersion;

    public int? LatestVersion => IsDocument ? Math.Max(Kit!.LatestVersion, File?.LatestVersion ?? 0) : null;

    /// <summary>Версия, которую показывает панель: в рабочей папке, иначе в комплекте.</summary>
    public int? ShownVersion => LocalVersion ?? Version;

    public string? Designation => Kit?.Designation ?? File?.Designation;
    public string? Name => Kit?.Name ?? File?.Name;

    public string FileName => Type switch
    {
        NodeType.Group => string.Empty,
        NodeType.Draft => File!.FileName,
        _ => File?.FileName ?? Kit!.FileName,
    };

    /// <summary>Путь в рабочей папке (null — документа нет в папке).</summary>
    public string? LocalRelPath => File?.RelPath;

    public string? FullPath => File?.FullPath;

    /// <summary>Файл есть на диске рабочей папки.</summary>
    public bool HasLocalFile => File != null && File.Local != LocalFileState.MissingLocally;

    public LockState Lock => Kit?.Lock ?? File?.Lock ?? LockState.Unknown;
    public LockDto? LockInfo => Kit?.LockInfo ?? File?.LockInfo;
    public bool IsMine => Lock == LockState.LockedByMe;
    public bool IsLockedByOther => Lock == LockState.LockedByOther;
    public bool IsModified => File?.Local == LocalFileState.ModifiedLocally;
    public bool IsOutdated => IsDocument && ShownVersion is { } v && LatestVersion is { } l && v < l;
    public bool IsUnused => Kit?.Status == DocumentStatus.Unused;

    /// <summary>Узел требует внимания: нет файла, ссылка не учтена, правка без взятия, чертёж с другой версией модели и т. п.</summary>
    public bool HasProblem => ProblemText.Length > 0;

    public string ProblemText
    {
        get
        {
            if (Type == NodeType.Unresolved) return "ссылка на файл, не учтённый в PDM";
            if (!IsDocument) return string.Empty;
            var parts = new List<string>();
            if (File == null) parts.Add("документа нет в рабочей папке");
            else if (File.Local == LocalFileState.MissingLocally) parts.Add("файла нет в рабочей папке");
            if (IsModified && !IsMine) parts.Add("изменён без взятия в работу — сдать нельзя");
            if (File?.LockRemovedByOthers == true) parts.Add("блокировку сняли принудительно");
            if (Kit!.HasVersionConflict) parts.Add("в изделии в нескольких версиях");
            if (Kit.DrawingModelVersionMismatch) parts.Add("чертёж сдан с другой версией модели");
            return string.Join("; ", parts);
        }
    }

    public string Title => Type switch
    {
        NodeType.Group => _groupTitle!,
        NodeType.Unresolved => Format.FileName(Kit!.RefPath),
        _ => string.IsNullOrWhiteSpace(Designation) ? FileName : Designation!,
    };

    /// <summary>Наименование (если обозначения нет — имя файла уже в заголовке).</summary>
    public string Subtitle => Type switch
    {
        NodeType.Group => $"{Children.Count}",
        NodeType.Unresolved => "не учтён в PDM",
        _ => Name ?? string.Empty,
    };

    public string KindText => Type switch
    {
        NodeType.Part => "Деталь",
        NodeType.Assembly => "Сборка",
        NodeType.Drawing => "Чертёж",
        NodeType.Draft => "Новый файл (" + Format.Kind(File!.Kind) + ")",
        NodeType.Unresolved => "Отсутствующий файл",
        NodeType.Group => "Новые файлы",
        _ => "Документ",
    };

    public string Glyph => Glyphs.For(Type);

    /// <summary>Цветовая метка: свободен — без метки.</summary>
    public StateMark Mark
    {
        get
        {
            switch (Type)
            {
                case NodeType.Group: return StateMark.None;
                case NodeType.Draft: return StateMark.Draft;
                case NodeType.Unresolved: return StateMark.Missing;
            }
            if (IsLockedByOther) return StateMark.Other;
            if (IsModified) return StateMark.Modified;
            if (IsMine) return StateMark.Mine;
            if (IsOutdated) return StateMark.Outdated;
            if (!HasLocalFile) return StateMark.Missing;
            return StateMark.None;
        }
    }

    /// <summary>Короткий текст состояния (пусто, если документ свободен).</summary>
    public string StateText
    {
        get
        {
            switch (Type)
            {
                case NodeType.Group: return string.Empty;
                case NodeType.Draft: return "черновик";
                case NodeType.Unresolved: return "не учтён";
            }
            if (IsLockedByOther) return "взят: " + Who(LockInfo);
            if (IsModified) return IsMine ? "изменён" : "изменён без взятия";
            if (IsMine) return "взят мной";
            if (IsOutdated) return $"есть v{LatestVersion}";
            if (!HasLocalFile) return "нет файла";
            return string.Empty;
        }
    }

    public bool HasState => StateText.Length > 0;

    /// <summary>Подробное состояние для шапки панели.</summary>
    public string FullStateText
    {
        get
        {
            if (Type == NodeType.Group) return string.Empty;
            if (Type == NodeType.Draft) return "черновик — ещё не поставлен на учёт; сдайте, чтобы он появился в хранилище";
            if (Type == NodeType.Unresolved) return "ссылка на файл, не учтённый в PDM";
            var parts = new List<string>
            {
                Lock switch
                {
                    LockState.LockedByMe => "взят мной",
                    LockState.LockedByOther => "взят: " + Who(LockInfo),
                    LockState.Free => "свободен",
                    _ => string.Empty,
                },
            };
            if (IsModified) parts.Add("изменён локально");
            if (IsOutdated) parts.Add($"есть новая версия v{LatestVersion}");
            if (IsUnused) parts.Add("помечен «не используется»");
            if (ProblemText.Length > 0) parts.Add(ProblemText);
            return string.Join(", ", parts.Where(p => p.Length > 0));
        }
    }

    public string VersionText
    {
        get
        {
            if (!IsDocument) return string.Empty;
            var shown = ShownVersion!.Value;
            var text = $"v{shown} из v{LatestVersion}";
            if (LocalVersion is { } local && Version is { } kit && local != kit) text += $" (в изделии v{kit})";
            return text;
        }
    }

    /// <summary>Кем и когда взят.</summary>
    public string LockText => LockInfo is { } l && Lock is LockState.LockedByMe or LockState.LockedByOther
        ? (Lock == LockState.LockedByMe ? "Взят мной" : "Взят: " + Who(l)) + ", " + Format.Time(l.At)
        : string.Empty;

    public string ToolTip
    {
        get
        {
            if (Type == NodeType.Group) return "Новые файлы рабочей папки, ещё не поставленные на учёт. Сдайте их, чтобы они появились в хранилище.";
            if (Type == NodeType.Unresolved) return "Ссылка: " + Kit!.RefPath;
            var lines = new List<string> { LocalRelPath ?? Kit?.RelPath ?? FileName };
            if (FullStateText.Length > 0) lines.Add(FullStateText);
            return string.Join("\n", lines);
        }
    }

    private static string Who(LockDto? l) => l == null ? "?" : string.IsNullOrWhiteSpace(l.UserDisplayName) ? l.UserLogin : l.UserDisplayName;

    /// <summary>Подходит ли узел под текст фильтра (подстрока обозначения, наименования, имени файла).</summary>
    public bool MatchesText(string text) =>
        Contains(Designation, text) || Contains(Name, text) || Contains(FileName, text) ||
        (Type == NodeType.Unresolved && Contains(Kit!.RefPath, text));

    private static bool Contains(string? value, string text) => value != null && value.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    public IEnumerable<ProductNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.SelfAndDescendants()) yield return d;
        }
    }

    public IEnumerable<ProductNodeViewModel> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }

    public override string ToString() => Title;
}

/// <summary>Глифы Segoe Fluent / MDL2.</summary>
public static class Glyphs
{
    public const string Assembly = "";
    public const string Part = "";
    public const string Drawing = "";
    public const string Document = "";
    public const string Draft = "";
    public const string Missing = "";
    public const string Folder = "";

    public static string For(NodeType type) => type switch
    {
        NodeType.Assembly => Assembly,
        NodeType.Part => Part,
        NodeType.Drawing => Drawing,
        NodeType.Draft => Draft,
        NodeType.Unresolved => Missing,
        NodeType.Group => Folder,
        _ => Document,
    };

    public static string For(DocumentKind kind) => kind switch
    {
        DocumentKind.Assembly => Assembly,
        DocumentKind.Part => Part,
        DocumentKind.Drawing => Drawing,
        _ => Document,
    };
}

/// <summary>Дерево изделия: построение по комплекту с чертежами и состоянию рабочей папки, фильтр и быстрые отборы.</summary>
public sealed partial class ProductTreeViewModel : ObservableObject
{
    public ObservableCollection<ProductNodeViewModel> Roots { get; } = new();

    [ObservableProperty]
    private ProductNodeViewModel? _selectedNode;

    /// <summary>Подстрока обозначения, наименования или имени файла.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showMine;

    [ObservableProperty]
    private bool _showModified;

    [ObservableProperty]
    private bool _showOutdated;

    [ObservableProperty]
    private bool _showProblems;

    [ObservableProperty]
    private int _mineCount;

    [ObservableProperty]
    private int _modifiedCount;

    [ObservableProperty]
    private int _outdatedCount;

    [ObservableProperty]
    private int _problemCount;

    [ObservableProperty]
    private bool _hasNoMatches;

    public ProductNodeViewModel? Root => Roots.FirstOrDefault(r => !r.IsGroup);

    /// <summary>Узел «Новые файлы» (null — черновиков нет).</summary>
    public ProductNodeViewModel? DraftsGroup => Roots.FirstOrDefault(r => r.IsGroup);

    public bool IsFiltered => FilterText.Trim().Length > 0 || ShowMine || ShowModified || ShowOutdated || ShowProblems;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowMineChanged(bool value) => ApplyFilter();
    partial void OnShowModifiedChanged(bool value) => ApplyFilter();
    partial void OnShowOutdatedChanged(bool value) => ApplyFilter();
    partial void OnShowProblemsChanged(bool value) => ApplyFilter();

    public IEnumerable<ProductNodeViewModel> AllNodes() => Roots.SelectMany(r => r.SelfAndDescendants());

    /// <summary>Первый узел документа (документ может входить в изделие несколько раз).</summary>
    public ProductNodeViewModel? Find(Guid documentId) => AllNodes().FirstOrDefault(n => n.DocumentId == documentId);

    public void Clear()
    {
        Roots.Clear();
        SelectedNode = null;
        MineCount = ModifiedCount = OutdatedCount = ProblemCount = 0;
        HasNoMatches = false;
    }

    /// <summary>Построить дерево. Раскрытие и выделение сохраняются по ключам узлов.</summary>
    public void Load(KitTreeNode tree, WorkspaceStatus? status)
    {
        var previousRoot = Root?.Key;
        var expanded = new HashSet<string>(AllNodes().Where(n => n.IsExpanded).Select(n => n.Key), StringComparer.Ordinal);
        var selected = SelectedNode?.Key;

        var root = Build(tree, null, status);
        // Раскрытие и выделение сохраняются только при обновлении того же изделия.
        var hadNodes = previousRoot == root.Key;
        if (!hadNodes) selected = null;
        var nodes = new List<ProductNodeViewModel> { root };
        var drafts = status?.Files.Where(f => f.Local == LocalFileState.Draft).OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<FileStatus>();
        if (drafts.Count > 0)
        {
            var group = new ProductNodeViewModel("Новые файлы", this);
            foreach (var d in drafts) group.Children.Add(new ProductNodeViewModel(d, group, this));
            nodes.Add(group);
        }

        foreach (var n in nodes.SelectMany(r => r.SelfAndDescendants()))
            n.IsExpanded = hadNodes ? expanded.Contains(n.Key) : n.Parent == null || n.Parent.Parent == null && n.Children.Count < 40;
        root.IsExpanded = true;

        SelectedNode = null;
        Roots.ReplaceWith(nodes);
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(DraftsGroup));

        var docs = AllNodes().Where(n => n.IsDocument).GroupBy(n => n.DocumentId).Select(g => g.First()).ToList();
        MineCount = docs.Count(n => n.IsMine);
        ModifiedCount = docs.Count(n => n.IsModified) + drafts.Count;
        OutdatedCount = docs.Count(n => n.IsOutdated);
        ProblemCount = docs.Count(n => n.HasProblem) + AllNodes().Count(n => n.Type == NodeType.Unresolved);
        ApplyFilter();

        var target = (selected == null ? null : AllNodes().FirstOrDefault(n => n.Key == selected)) ?? root;
        Select(target);
    }

    private ProductNodeViewModel Build(KitTreeNode node, ProductNodeViewModel? parent, WorkspaceStatus? status)
    {
        var file = node.IsUnresolved ? null : status?.FindByDocument(node.DocumentId);
        var vm = new ProductNodeViewModel(node, file, parent, this);
        foreach (var c in node.Children) vm.Children.Add(Build(c, vm, status));
        return vm;
    }

    /// <summary>Выделить узел (раскрыв предков).</summary>
    public void Select(ProductNodeViewModel node)
    {
        foreach (var a in node.Ancestors()) a.IsExpanded = true;
        if (SelectedNode != null && !ReferenceEquals(SelectedNode, node)) SelectedNode.IsSelected = false;
        node.IsSelected = true;
        SelectedNode = node;
    }

    public bool Select(Guid documentId)
    {
        var node = Find(documentId);
        if (node == null) return false;
        if (!node.IsVisible) ClearFilter();
        Select(node);
        return true;
    }

    /// <summary>Подходит ли узел под быстрые отборы (между собой — «или»).</summary>
    private bool MatchesQuick(ProductNodeViewModel n)
    {
        if (!ShowMine && !ShowModified && !ShowOutdated && !ShowProblems) return true;
        return (ShowMine && n.IsMine) ||
               (ShowModified && (n.IsModified || n.IsDraft)) ||
               (ShowOutdated && n.IsOutdated) ||
               (ShowProblems && (n.HasProblem || n.Type == NodeType.Unresolved));
    }

    /// <summary>
    /// Применить фильтр: узел виден, если подходит сам (текст и быстрые отборы) или виден кто-то из его потомков;
    /// предки совпавших раскрываются.
    /// </summary>
    public void ApplyFilter()
    {
        var text = FilterText.Trim();
        var active = IsFiltered;
        var any = false;

        bool Visit(ProductNodeViewModel n)
        {
            var childVisible = false;
            foreach (var c in n.Children)
            {
                if (Visit(c)) childVisible = true;
            }
            if (!active)
            {
                n.IsMatch = false;
                n.IsVisible = !n.IsGroup || n.Children.Count > 0;
                return true;
            }
            var self = !n.IsGroup && (text.Length == 0 || n.MatchesText(text)) && MatchesQuick(n);
            n.IsMatch = self;
            n.IsVisible = self || childVisible;
            if (childVisible) n.IsExpanded = true;
            if (self) any = true;
            return n.IsVisible;
        }

        foreach (var r in Roots) Visit(r);
        HasNoMatches = active && !any;
        OnPropertyChanged(nameof(IsFiltered));
    }

    [RelayCommand]
    private void ClearFilter()
    {
        FilterText = string.Empty;
        ShowMine = ShowModified = ShowOutdated = ShowProblems = false;
    }

    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var n in AllNodes()) n.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var n in AllNodes()) n.IsExpanded = n.Parent == null;
    }
}
