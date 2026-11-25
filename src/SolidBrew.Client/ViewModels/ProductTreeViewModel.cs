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

    /// <summary>
    /// Изделие верхнего уровня, ещё не открытое в окне: раскрытие (или «Открыть») открывает его в рабочую папку,
    /// и заглушка заменяется деревом изделия.
    /// </summary>
    public ProductNodeViewModel(ProductItem product, ProductTreeViewModel owner)
    {
        Product = product;
        _owner = owner;
        Kit = new KitTreeNode
        {
            DocumentId = product.DocumentId,
            RelPath = product.Title,
            Designation = product.Designation,
            Name = product.Name,
            Kind = DocumentKind.Assembly,
            Status = DocumentStatus.Active,
            Lock = LockState.Unknown,
        };
        Type = NodeType.Assembly;
        Key = "product:" + product.DocumentId.ToString("N");
        // Пустой служебный потомок — чтобы у заглушки была стрелка раскрытия.
        Children.Add(new ProductNodeViewModel("…", owner));
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

    private bool _needsResave;

    /// <summary>
    /// Сборку или чертёж надо взять в работу и пересохранить: изменилась (или устарела) входящая модель — для чертежа
    /// та, которую он изображает. Считает дерево (<see cref="ProductTreeViewModel.ComputeNeedsResave"/>).
    /// </summary>
    public bool NeedsResave
    {
        get => _needsResave;
        internal set
        {
            if (_needsResave == value) return;
            _needsResave = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDot));
            OnPropertyChanged(nameof(StatusDotText));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(HasState));
            OnPropertyChanged(nameof(FullStateText));
        }
    }

    /// <summary>Цветной кружок после значка.</summary>
    public StatusDot StatusDot
    {
        get
        {
            if (IsProductStub) return StatusDot.InStorage;
            if (!IsDocument) return StatusDot.None;
            if (IsModified) return StatusDot.MineModified;
            if (NeedsResave) return StatusDot.NeedsResave;
            if (IsMine) return StatusDot.Mine;
            if (IsLockedByOther) return StatusDot.Other;
            if (!HasLocalFile) return StatusDot.Missing;
            return StatusDot.InStorage;
        }
    }

    public string StatusDotText => StatusDot switch
    {
        StatusDot.InStorage => "Сдан в хранилище: свободен, правок нет",
        StatusDot.Mine => "Взят мной на редактирование, не изменён",
        StatusDot.MineModified => IsMine ? "Взят мной и изменён — сдайте правки" : "Изменён без взятия в работу — возьмите в работу, чтобы сдать",
        StatusDot.NeedsResave => Type == NodeType.Drawing
            ? "Модель чертежа изменилась — возьмите чертёж в работу и пересохраните"
            : "Изменилась входящая модель — возьмите сборку в работу и пересохраните",
        StatusDot.Other => "Взят другим: " + Who(LockInfo),
        StatusDot.Missing => "Файла нет в рабочей папке",
        _ => string.Empty,
    };

    /// <summary>Заглушка неоткрытого изделия (null — обычный узел).</summary>
    public ProductItem? Product { get; }

    public bool IsProductStub => Product != null;

    partial void OnIsExpandedChanged(bool value)
    {
        // Раскрыли неоткрытое изделие — открыть его; заглушка остаётся свёрнутой, пока изделие не загрузится.
        if (value && IsProductStub && _owner != null)
        {
            IsExpanded = false;
            _owner.RequestProduct(Product!);
        }
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

    /// <summary>Текущая версия документа: более новая из версии в рабочей папке и последней в хранилище.</summary>
    public int CurrentVersion => Math.Max(LocalVersion ?? Version ?? 0, LatestVersion ?? 0);

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

    private bool? _linkExists;

    /// <summary>
    /// Для узла «не учтён в PDM»: путь из ссылки, если файл по нему есть (CAD откроет его по ссылке); иначе null.
    /// Наличие файла проверяется один раз на узел (сетевой диск).
    /// </summary>
    public string? LinkPath
    {
        get
        {
            if (Type != NodeType.Unresolved) return null;
            var path = Kit!.RefPath.Replace('/', '\\');
            if (_linkExists == null)
            {
                try { _linkExists = System.IO.Path.IsPathFullyQualified(path) && System.IO.File.Exists(path); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { _linkExists = false; }
            }
            return _linkExists == true ? path : null;
        }
    }

    /// <summary>
    /// Значок неоткрытого изделия: по имени файла, иначе по расширению в обозначении/наименовании (часто «….SLDASM»),
    /// иначе — значок сборки SOLIDWORKS (изделие — всегда сборка).
    /// </summary>
    private static string StubIconFile(ProductItem p)
    {
        if (!string.IsNullOrWhiteSpace(p.FileName)) return p.FileName!;
        foreach (var text in new[] { p.Title, p.Name, p.Designation })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var ext = System.IO.Path.GetExtension(text);
            // «АБВ.001» — не расширение: настоящее расширение CAD — латинские буквы (sldasm, a3d, tcasm…).
            if (ext.Length is > 2 and <= 8 && ext.Skip(1).All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                && ext.Skip(1).Any(char.IsLetter)) return text!;
        }
        return "изделие.sldasm";
    }

    /// <summary>Почему нечего показать в просмотре.</summary>
    public string PreviewEmptyText => IsProductStub ? "Изделие не открыто. Раскройте его в дереве — оно откроется в рабочую папку, и модель появится здесь." : Type switch
    {
        NodeType.Group => "Выберите документ.",
        NodeType.Unresolved => "Файла по ссылке нет: " + Kit!.RefPath,
        _ => "Документа нет в рабочей папке — откройте изделие или «Обновить до последней», чтобы он появился.",
    };

    /// <summary>Файл, который открывается из узла: в рабочей папке или, для не учтённого в PDM, по ссылке.</summary>
    public string? OpenablePath => FullPath ?? LinkPath;

    /// <summary>Родитель-документ (для узла «не учтён в PDM» — чья это ссылка).</summary>
    public ProductNodeViewModel? ParentDocument
    {
        get
        {
            var p = Parent;
            while (p != null && !p.IsDocument) p = p.Parent;
            return p;
        }
    }

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
            if (Type == NodeType.Unresolved)
                return LinkPath != null ? string.Empty : "ссылка на файл, не учтённый в PDM, — по этому пути файла нет";
            if (!IsDocument || IsProductStub) return string.Empty;
            var parts = new List<string>();
            if (File == null) parts.Add("документа нет в рабочей папке");
            else if (File.Local == LocalFileState.MissingLocally) parts.Add("файла нет в рабочей папке");
            if (IsModified && !IsMine) parts.Add("изменён без взятия в работу — сдать нельзя");
            if (File?.LockRemovedByOthers == true) parts.Add("блокировку сняли принудительно");
            if (Kit!.HasVersionConflict) parts.Add("в изделии в нескольких версиях");
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
    public string Subtitle => IsProductStub ? Product!.Subtitle : Type switch
    {
        NodeType.Group => $"{Children.Count}",
        NodeType.Unresolved => LinkPath != null ? "не учтён в PDM, по ссылке" : "не учтён в PDM, файла нет",
        _ => Name ?? string.Empty,
    };

    public string KindText => IsProductStub ? "Изделие" : Type switch
    {
        NodeType.Part => "Деталь",
        NodeType.Assembly => "Сборка",
        NodeType.Drawing => "Чертёж",
        NodeType.Draft => "Новый файл (" + Format.Kind(File!.Kind) + ")",
        NodeType.Unresolved => LinkPath != null ? "Файл по ссылке (не учтён в PDM)" : "Отсутствующий файл",
        NodeType.Group => "Новые файлы",
        _ => "Документ",
    };

    /// <summary>
    /// Файл для системного значка типа (как в Проводнике): документ, черновик, файл по ссылке.
    /// null — служебный узел или ссылка на несуществующий файл: показывается глиф.
    /// </summary>
    public string? IconFile => IsProductStub ? StubIconFile(Product!) : Type switch
    {
        NodeType.Group => null,
        NodeType.Unresolved => LinkPath,
        _ => FileName.Length > 0 ? FileName : null,
    };

    public string Glyph => Type == NodeType.Unresolved && LinkPath is { } link
        ? System.IO.Path.GetExtension(link).ToLowerInvariant() switch
        {
            ".sldasm" or ".a3d" or ".tcasm" => Glyphs.For(NodeType.Assembly),
            ".slddrw" or ".cdw" or ".tcdrw" => Glyphs.For(NodeType.Drawing),
            _ => Glyphs.For(NodeType.Part),
        }
        : Glyphs.For(Type);

    /// <summary>Цветовая метка: свободен — без метки.</summary>
    public StateMark Mark
    {
        get
        {
            if (IsProductStub) return StateMark.None;
            switch (Type)
            {
                case NodeType.Group: return StateMark.None;
                case NodeType.Draft: return StateMark.Draft;
                case NodeType.Unresolved: return LinkPath != null ? StateMark.Draft : StateMark.Missing;
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
            if (IsProductStub) return string.Empty;
            var state = BaseStateText;
            if (!NeedsResave || IsModified) return state;
            return state.Length == 0 ? "надо пересохранить" : state + ", надо пересохранить";
        }
    }

    private string BaseStateText
    {
        get
        {
            switch (Type)
            {
                case NodeType.Group: return string.Empty;
                case NodeType.Draft: return "черновик";
                case NodeType.Unresolved: return LinkPath != null ? "по ссылке" : "нет файла";
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
            if (IsProductStub) return "изделие не открыто — раскройте его или «Открыть», чтобы работать с составом";
            if (Type == NodeType.Draft) return "черновик — ещё не поставлен на учёт; сдайте, чтобы он появился в хранилище";
            if (Type == NodeType.Unresolved)
                return LinkPath != null
                    ? "не учтён в PDM — CAD открывает его по ссылке; «Поставить на учёт», чтобы брать в работу и сдавать"
                    : "ссылка на файл, не учтённый в PDM; по этому пути файла нет";
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
            if (NeedsResave && !IsModified) parts.Add(Type == NodeType.Drawing ? "модель изменилась — пересохраните чертёж" : "входящая модель изменилась — пересохраните");
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
            if (!IsDocument || IsProductStub) return string.Empty;
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
            if (IsProductStub) return Product!.Title + (Product.Details.Length > 0 ? "\n" + Product.Details : string.Empty) + "\nРаскройте или дважды щёлкните, чтобы открыть изделие.";
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

    private ProductNodeViewModel? _root;
    private ProductNodeViewModel? _drafts;
    private List<ProductItem> _products = new();
    private readonly Dictionary<Guid, ProductNodeViewModel> _stubs = new();

    /// <summary>Открытое изделие окна (дерево его состава); null — не открыто.</summary>
    public ProductNodeViewModel? Root => _root;

    /// <summary>Узел «Новые файлы» (null — черновиков нет).</summary>
    public ProductNodeViewModel? DraftsGroup => _drafts;

    /// <summary>Раскрыли неоткрытое изделие верхнего уровня — окно должно его открыть.</summary>
    public event Action<ProductItem>? ProductRequested;

    internal void RequestProduct(ProductItem product) => ProductRequested?.Invoke(product);

    /// <summary>
    /// Изделия верхнего уровня дерева (все: закреплённые, недавние, корневые сборки сервера). Открытое показано составом,
    /// остальные — свёрнутыми заглушками. Порядок — по обозначению.
    /// </summary>
    public void SetProducts(IEnumerable<ProductItem> products)
    {
        _products = products.GroupBy(p => p.DocumentId).Select(g => g.First())
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        ComposeRoots();
        ApplyFilter();
    }

    private void ComposeRoots()
    {
        var list = new List<ProductNodeViewModel>();
        var activeId = _root?.DocumentId;
        if (_root != null && _products.All(p => p.DocumentId != activeId)) list.Add(_root);
        foreach (var p in _products)
        {
            if (p.DocumentId == activeId)
            {
                list.Add(_root!);
                continue;
            }
            if (!_stubs.TryGetValue(p.DocumentId, out var stub) || stub.Product!.Title != p.Title)
                _stubs[p.DocumentId] = stub = new ProductNodeViewModel(p, this);
            list.Add(stub);
        }
        if (_drafts != null) list.Add(_drafts);
        Roots.ReplaceWith(list);
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(DraftsGroup));
    }

    public bool IsFiltered => FilterText.Trim().Length > 0 || ShowMine || ShowModified || ShowOutdated || ShowProblems;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowMineChanged(bool value) => ApplyFilter();
    partial void OnShowModifiedChanged(bool value) => ApplyFilter();
    partial void OnShowOutdatedChanged(bool value) => ApplyFilter();
    partial void OnShowProblemsChanged(bool value) => ApplyFilter();

    public IEnumerable<ProductNodeViewModel> AllNodes() => Roots.SelectMany(r => r.SelfAndDescendants());

    /// <summary>Первый узел документа (документ может входить в изделие несколько раз).</summary>
    public ProductNodeViewModel? Find(Guid documentId) =>
        AllNodes().FirstOrDefault(n => n.DocumentId == documentId && !n.IsProductStub) ?? AllNodes().FirstOrDefault(n => n.DocumentId == documentId);

    public void Clear()
    {
        _root = null;
        _drafts = null;
        ComposeRoots();
        SelectedNode = null;
        MineCount = ModifiedCount = OutdatedCount = ProblemCount = 0;
        HasNoMatches = false;
    }

    /// <summary>Построить дерево. Раскрытие и выделение сохраняются по ключам узлов.</summary>
    public void Load(KitTreeNode tree, WorkspaceStatus? status)
    {
        var previousRoot = Root?.Key;
        var expanded = new HashSet<string>(AllNodes().Where(n => n.IsExpanded && !n.IsProductStub).Select(n => n.Key), StringComparer.Ordinal);
        var selected = SelectedNode?.Key;

        var root = Build(tree, null, status);
        // Раскрытие и выделение сохраняются только при обновлении того же изделия.
        var hadNodes = previousRoot == root.Key;
        if (!hadNodes) selected = null;
        var nodes = new List<ProductNodeViewModel> { root };
        var drafts = status?.Files.Where(f => f.Local == LocalFileState.Draft).OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<FileStatus>();
        ProductNodeViewModel? group = null;
        if (drafts.Count > 0)
        {
            group = new ProductNodeViewModel("Новые файлы", this);
            foreach (var d in drafts) group.Children.Add(new ProductNodeViewModel(d, group, this));
            nodes.Add(group);
        }

        foreach (var n in nodes.SelectMany(r => r.SelfAndDescendants()))
            n.IsExpanded = hadNodes ? expanded.Contains(n.Key) : n.Parent == null || n.Parent.Parent == null && n.Children.Count < 40;
        root.IsExpanded = true;

        SelectedNode = null;
        _root = root;
        _drafts = group;
        ComposeRoots();

        ComputeNeedsResave(root);

        var docs = AllNodes().Where(n => n.IsDocument && !n.IsProductStub).GroupBy(n => n.DocumentId).Select(g => g.First()).ToList();
        MineCount = docs.Count(n => n.IsMine);
        ModifiedCount = docs.Count(n => n.IsModified) + drafts.Count;
        OutdatedCount = docs.Count(n => n.IsOutdated);
        ProblemCount = docs.Count(n => n.HasProblem) + AllNodes().Count(n => n.Type == NodeType.Unresolved && n.HasProblem);
        ApplyFilter();

        var target = (selected == null ? null : AllNodes().FirstOrDefault(n => n.Key == selected)) ?? root;
        Select(target);
    }

    /// <summary>
    /// Жёлтый кружок: сборка — если любая её составная часть (кроме чертежей) изменена, требует пересохранения или
    /// зафиксирована в сборке в версии старше текущей; чертёж — то же для модели, которую он изображает (версия — та,
    /// на которую ссылается сам чертёж). Сначала сборки снизу вверх, затем чертежи.
    /// </summary>
    internal static void ComputeNeedsResave(ProductNodeViewModel root)
    {
        // Ссылка на модель отстала: модель изменена, сама требует пересохранения, или зафиксирована версия старше текущей
        // (текущая — более новая из версии в рабочей папке и последней в хранилище).
        static bool Behind(int boundVersion, ProductNodeViewModel model) =>
            model.IsModified || model.NeedsResave || boundVersion < model.CurrentVersion;

        void Assemblies(ProductNodeViewModel n)
        {
            foreach (var c in n.Children) Assemblies(c);
            if (n.IsDocument && !n.IsProductStub && n.Type == NodeType.Assembly)
                n.NeedsResave = n.Children.Any(c => c.IsDocument && c.Type != NodeType.Drawing && Behind(c.Kit!.Version, c));
        }
        Assemblies(root);

        foreach (var d in root.SelfAndDescendants().Where(n => n.IsDocument && n.Type == NodeType.Drawing))
        {
            if (d.Parent is not { IsDocument: true } model)
            {
                d.NeedsResave = false;
                continue;
            }
            var bound = d.Kit!.DrawnVersions.TryGetValue(model.DocumentId!.Value, out var v) ? v : model.Kit!.Version;
            d.NeedsResave = Behind(bound, model);
        }
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
               (ShowProblems && n.HasProblem);
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
        // Неоткрытые изделия не трогаем: их раскрытие открывает изделие.
        foreach (var n in AllNodes().Where(n => !n.IsProductStub)) n.IsExpanded = true;
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var n in AllNodes().Where(n => !n.IsProductStub)) n.IsExpanded = n.Parent == null;
    }

    /// <summary>Развернуть узел со всеми вложенными.</summary>
    public static void ExpandBranch(ProductNodeViewModel node)
    {
        foreach (var n in node.SelfAndDescendants().Where(n => !n.IsProductStub)) n.IsExpanded = true;
    }

    /// <summary>Свернуть узел со всеми вложенными (при следующем раскрытии вложенные тоже свёрнуты).</summary>
    public static void CollapseBranch(ProductNodeViewModel node)
    {
        foreach (var n in node.SelfAndDescendants()) n.IsExpanded = false;
    }
}
