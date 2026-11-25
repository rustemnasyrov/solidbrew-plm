using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Окно изделия: одно окно — одно изделие (любая сборка) и его рабочая папка. Слева дерево изделия с фильтром,
/// справа панель выбранного узла с действиями и вкладками. Изделие переключается в том же окне; «Открыть в новом окне» —
/// второе окно со своим изделием и своей папкой.
/// </summary>
public sealed partial class ProductWindowViewModel : ObservableObject, INodeHost, IDisposable
{
    private readonly WorkspaceWatcher _watcher = new();
    private bool _watchPending;
    private bool _opening;
    private bool _unseenNotifications;
    private int _refreshVersion;

    public ProductWindowViewModel(AppSession session, NodeTabRegistry? tabs = null, IEnumerable<NotificationDto>? notifications = null)
    {
        Session = session;
        Busy.ErrorHandler = (title, error) => Dialogs.ShowError(title, error);
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BusyState.IsBusy)) return;
            NotifyAll();
            // Файлы менялись, пока шла операция, — обновиться, когда она закончится.
            if (!Busy.IsBusy && _watchPending) _ = OnWorkspaceChangedAsync();
        };
        Flow = new WorkflowController(session, Busy, restoreLast: false);
        Flow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowController.WorkRoot)) _watcher.Root = Flow.WorkRoot;
        };
        // Сохранили файл в CAD (или он изменился иначе) — состояние и действия обновляются сами, без F5.
        _watcher.Changed += async (_, _) => await OnWorkspaceChangedAsync();
        Flow.Changed += async (_, _) =>
        {
            if (_opening) return;
            PendingRefresh = RefreshAsync();
            await PendingRefresh;
        };
        Flow.Notices += (_, items) => AddNotices(items);
        Tree = new ProductTreeViewModel();
        Tree.PropertyChanged += OnTreeChanged;
        Tree.ProductRequested += async p =>
        {
            if (Busy.IsBusy) return;
            await OpenProductAsync(p.ToRef());
        };
        Panel = new NodePanelViewModel(this, tabs ?? NodeTabRegistry.CreateDefault(), CommandFor);
        Switcher = new ProductCatalogViewModel(session);
        Notices.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNotices));
            OnPropertyChanged(nameof(FirstNotice));
            OnPropertyChanged(nameof(HasMoreNotices));
            OnPropertyChanged(nameof(MoreNoticesText));
        };
        if (notifications != null)
        {
            foreach (var n in notifications) Notices.Add($"{Format.Time(n.At)}: {n.Text}");
            _unseenNotifications = Notices.Count > 0;
        }
    }

    public AppSession Session { get; }
    public BusyState Busy { get; } = new();
    public WorkflowController Flow { get; }
    public ProductTreeViewModel Tree { get; }
    public NodePanelViewModel Panel { get; }

    /// <summary>Переключатель изделий: закреплённые, недавние, корневые, поиск.</summary>
    public ProductCatalogViewModel Switcher { get; }

    private IDialogService Dialogs => Session.Services.Dialogs;

    public string UserText => Session.UserText;
    public string ServerText => Session.Profile.ServerUrl;
    public bool IsAdmin => Session.IsAdmin;
    public bool CanModify => Session.CanModify;
    public bool IsReader => !Session.CanModify;

    /// <summary>Изделие окна (null — не выбрано).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProduct), nameof(ProductTitle), nameof(ProductSubtitle), nameof(WindowTitle), nameof(IsPinned), nameof(PinText), nameof(PinGlyph))]
    private ProductRef? _product;

    /// <summary>Сведения о документе изделия с сервера.</summary>
    [ObservableProperty]
    private DocumentSummaryDto? _productDocument;

    [ObservableProperty]
    private string _productVersionText = string.Empty;

    /// <summary>Сколько документов сдавать: изменённые взятые и черновики.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckinText), nameof(HasCheckin))]
    [NotifyCanExecuteChangedFor(nameof(CheckinAllCommand))]
    private int _checkinCount;

    [ObservableProperty]
    private bool _isSwitcherOpen;

    [ObservableProperty]
    private string _emptyText = "Изделие не выбрано.";

    /// <summary>Обновление дерева, начатое после команды (тесты ждут его окончания).</summary>
    public Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Загрузка списка переключателя, начатая его открытием.</summary>
    public Task SwitcherLoading { get; private set; } = Task.CompletedTask;

    public bool HasProduct => Product != null;
    public string ProductTitle => Product?.Title ?? "Изделие не выбрано";
    public string ProductSubtitle => Product == null || string.IsNullOrWhiteSpace(Product.Designation) ? string.Empty : Product.Name ?? string.Empty;
    public string WindowTitle => Product == null ? "SolidBrew PDM" : $"{ProductTitle} {ProductSubtitle} — SolidBrew PDM".Replace("  ", " ");
    public string WorkRootText => Flow.WorkRoot ?? string.Empty;
    public string CheckinText => CheckinCount > 0 ? $"Сдать ({CheckinCount})" : "Сдать";
    public bool HasCheckin => CheckinCount > 0;
    public bool IsPinned => Product != null && Session.Profile.IsPinned(Product.DocumentId);
    public string PinText => IsPinned ? "Открепить изделие" : "Закрепить изделие в списке изделий";
    public string PinGlyph => IsPinned ? "" : "";

    /// <summary>Уведомления: принудительно снятые блокировки и т. п.</summary>
    public ObservableCollection<string> Notices { get; } = new();

    public bool HasNotices => Notices.Count > 0;

    /// <summary>Полоса уведомлений неброская: одна строка, остальное — по «ещё N».</summary>
    public string FirstNotice => Notices.FirstOrDefault() ?? string.Empty;

    public bool HasMoreNotices => Notices.Count > 1;
    public string MoreNoticesText => $"ещё {Notices.Count - 1}";

    /// <summary>«Открыть в новом окне» (null — выбрать изделие в стартовом окне).</summary>
    public event EventHandler<ProductRef?>? NewWindowRequested;

    /// <summary>Выход: все окна закрываются, показывается вход.</summary>
    public event EventHandler? LogoutRequested;

    // ------------------------------------------------------------------ изделие

    /// <summary>
    /// Начало работы окна: в дереве — все изделия (закреплённые, недавние, корневые сборки сервера);
    /// если задано изделие — оно открывается.
    /// </summary>
    public async Task StartAsync(ProductRef? product)
    {
        await LoadProductsAsync(reload: false);
        if (product != null) await OpenProductAsync(product);
    }

    /// <summary>Список изделий верхнего уровня дерева (с сервера — один раз; <paramref name="reload"/> — заново).</summary>
    public async Task LoadProductsAsync(bool reload)
    {
        Switcher.RebuildLocal();
        Tree.SetProducts(Switcher.AllProducts());
        await (reload ? Switcher.ReloadAsync() : Switcher.LoadAsync());
        Tree.SetProducts(Switcher.AllProducts());
        if (!string.IsNullOrEmpty(Switcher.StatusText)) AddNotices(new[] { Switcher.StatusText });
    }

    /// <summary>
    /// Открыть изделие в этом окне. Если для него есть недавняя рабочая папка — открывается она; иначе папка спрашивается
    /// (по умолчанию <c>C:\PDM\&lt;обозначение&gt;</c>) и выполняется «Открыть» с чертежами (окно конфликтов — только если они есть).
    /// </summary>
    public async Task<bool> OpenProductAsync(ProductRef? product)
    {
        if (product == null) return false;
        _opening = true;
        try
        {
            var info = await Busy.RunAsync("Изделие", (_, ct) => Session.Api.GetDocumentAsync(product.DocumentId, ct));
            if (!info.Ok) return false;
            var doc = info.Value!.Document;
            var label = Format.Doc(doc.RelPath, doc.Designation);

            string? root = null;
            if (product.WorkRoot != null && ProductFolders.Contains(product.WorkRoot, doc.Id)) root = Path.GetFullPath(product.WorkRoot).TrimEnd('\\', '/');
            root ??= ProductFolders.RecentFolder(Session.Profile, doc.Id);
            if (root == null)
            {
                var suggested = ProductFolders.DefaultFolder(Session.Services.ProductsRoot, doc.Designation, doc.Name, doc.FileName);
                var choice = Dialogs.Choose("Открыть изделие",
                    $"Изделие {label}{(string.IsNullOrWhiteSpace(doc.Name) ? string.Empty : " — " + doc.Name)} ещё не открыто на этом компьютере.\n" +
                    $"Открыть его вместе с чертежами в рабочую папку «{suggested}»?",
                    new[] { "Открыть в эту папку", "Выбрать другую папку…", "Отмена" });
                if (choice == 1)
                {
                    root = Dialogs.PickFolder("Рабочая папка изделия", Directory.Exists(Session.Services.ProductsRoot) ? Session.Services.ProductsRoot : null);
                    if (root == null) return false;
                }
                else if (choice == 0)
                {
                    root = suggested;
                }
                else
                {
                    return false;
                }
                root = await Flow.OpenIntoAsync(root, doc.Id, null, label, includeDrawings: true);
                if (root == null) return false;
            }

            Flow.SetWorkRoot(root);
            Session.Profile.RememberProduct(doc.Id, root, doc.Designation, doc.Name ?? doc.FileName);
            SaveProfile();
            ProductDocument = doc;
            Product = new ProductRef(doc.Id, doc.Designation, doc.Name ?? doc.FileName, root);
            OnPropertyChanged(nameof(WorkRootText));
            Switcher.RebuildLocal();
            Tree.SetProducts(Switcher.AllProducts());
        }
        finally
        {
            _opening = false;
        }
        await RefreshAsync();
        return true;
    }

    /// <summary>Перестроить дерево изделия и состояние рабочей папки.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Product is not { } product || Flow.WorkRoot is not { } root) return;
        var ws = Session.Workspace;
        var api = Session.Api;
        var me = Session.Me.Id;
        var version = Interlocked.Increment(ref _refreshVersion);
        var outcome = await Busy.RunAsync("Обновление изделия", async (p, ct) =>
        {
            var state = ws.LoadState(root);
            var status = await ws.GetStatusAsync(root, true, p, ct);
            var docVersion = state.FindByDocument(product.DocumentId)?.Version
                          ?? (await api.GetDocumentAsync(product.DocumentId, ct)).Document.LatestVersion;
            var kit = await api.GetKitAsync(product.DocumentId, docVersion, true, ct);
            return (Tree: KitTreeBuilder.Build(kit, status, me), Status: status);
        });
        // Пока шла эта загрузка, началась более новая (или сменилось изделие) — её результат и применится.
        if (version != _refreshVersion || !ReferenceEquals(product, Product)) return;
        if (!outcome.Ok)
        {
            EmptyText = "Изделие не загружено: " + outcome.Error?.Message;
            return;
        }
        var (tree, status) = outcome.Value;
        Tree.Load(tree, status);
        CheckinCount = status.Files.Count(f => f.Local == LocalFileState.Draft || (f.Local == LocalFileState.ModifiedLocally && f.Lock == LockState.LockedByMe));
        var r = Tree.Root;
        ProductVersionText = r?.ShownVersion is { } v ? $"v{v}" + (r.LatestVersion is { } l && l > v ? $", есть v{l}" : string.Empty) : string.Empty;
        if (status.Messages.Count > 0) AddNotices(status.Messages);
        Busy.Done(status.ServerChecked ? $"Документов в изделии: {Tree.AllNodes().Count(n => n.IsDocument)}." : "Сервер не опрошен: " + status.ServerError);
    }

    /// <summary>В рабочей папке изменились файлы: обновить дерево (во время операции — после неё).</summary>
    private async Task OnWorkspaceChangedAsync()
    {
        if (Product == null) return;
        if (_opening || Busy.IsBusy)
        {
            _watchPending = true;
            return;
        }
        _watchPending = false;
        PendingRefresh = RefreshAsync();
        await PendingRefresh;
    }

    public void Dispose() => _watcher.Dispose();

    private void OnTreeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProductTreeViewModel.SelectedNode)) return;
        var node = Tree.SelectedNode;
        Panel.Show(node, Session.CanModify, node != null && ReferenceEquals(node, Tree.Root));
        this.NotifyCommands();
    }

    public void AddNotices(IEnumerable<string> items)
    {
        foreach (var i in items.Where(i => !Notices.Contains(i))) Notices.Add(i);
    }

    [RelayCommand]
    private async Task DismissNoticesAsync()
    {
        Notices.Clear();
        if (_unseenNotifications)
        {
            _unseenNotifications = false;
            await Busy.RunAsync("Уведомления", (_, ct) => Session.Api.MarkNotificationsSeenAsync(ct), reportErrors: false);
        }
    }

    private void SaveProfile()
    {
        try
        {
            Session.SaveProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ переключатель изделий

    partial void OnIsSwitcherOpenChanged(bool value)
    {
        if (!value) return;
        Switcher.RebuildLocal();
        SwitcherLoading = Switcher.LoadAsync(); // одна загрузка на окно; повторное открытие её не повторяет
    }

    /// <summary>Открыть выбранное в переключателе изделие в этом окне.</summary>
    [RelayCommand]
    private async Task SwitchToAsync(ProductItem? item)
    {
        if (item == null) return;
        IsSwitcherOpen = false;
        if (Product != null && item.DocumentId == Product.DocumentId &&
            (item.WorkRoot == null || string.Equals(item.WorkRoot, Flow.WorkRoot, StringComparison.OrdinalIgnoreCase))) return;
        await OpenProductAsync(item.ToRef());
    }

    [RelayCommand]
    private void OpenItemInNewWindow(ProductItem? item)
    {
        if (item == null) return;
        IsSwitcherOpen = false;
        NewWindowRequested?.Invoke(this, item.ToRef());
    }

    /// <summary>Стартовое окно: выбрать другое изделие для этого окна.</summary>
    [RelayCommand]
    private async Task ChangeProductAsync()
    {
        IsSwitcherOpen = false;
        var start = new StartViewModel(Session);
        if (Dialogs.ShowDialog(start) == true && start.Choice is { } choice) await OpenProductAsync(choice);
    }

    /// <summary>Новое окно с другим изделием.</summary>
    [RelayCommand]
    private void NewWindow()
    {
        IsSwitcherOpen = false;
        NewWindowRequested?.Invoke(this, null);
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Product == null) return;
        if (Session.Profile.IsPinned(Product.DocumentId)) Session.Profile.Unpin(Product.DocumentId);
        else Session.Profile.Pin(Product.DocumentId, Product.Designation, Product.Name);
        SaveProfile();
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinText));
        OnPropertyChanged(nameof(PinGlyph));
        Switcher.RebuildLocal();
    }

    [RelayCommand]
    private void ShowWorkRoot()
    {
        if (Flow.WorkRoot != null) Flow.ShowInExplorer(Flow.WorkRoot);
    }

    // ------------------------------------------------------------------ команды верхней полосы и меню

    private bool CanCheckinAll() => Session.CanModify && CheckinCount > 0 && !Busy.IsBusy;

    /// <summary>«Сдать (N)» — окно сдачи рабочей папки.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckinAll))]
    private Task CheckinAllAsync() => Flow.CheckinAsync();

    private bool HasProductAndIdle() => Product != null && !Busy.IsBusy;

    [RelayCommand(CanExecute = nameof(HasProductAndIdle))]
    private void Export()
    {
        if (Product != null) Flow.Export(Product.DocumentId, Tree.Root?.ShownVersion);
    }

    private bool CanImport() => Session.CanModify && !Busy.IsBusy;

    /// <summary>
    /// «Добавить проект…»: выбрать сборку SOLIDWORKS (или другой документ CAD) и поставить её на учёт;
    /// новое изделие появляется в дереве и открывается, если так выбрано в конце постановки.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task AddProjectAsync()
    {
        var all = Session.Workspace.Adapters.All.SelectMany(a => a.Extensions).Select(e => "*" + e).ToList();
        var filter = "Сборки SOLIDWORKS (*.sldasm)|*.sldasm|" +
                     $"Документы CAD ({string.Join(", ", all)})|{string.Join(";", all)}|Все файлы (*.*)|*.*";
        var start = Flow.WorkRoot != null && Directory.Exists(Flow.WorkRoot) ? Path.GetDirectoryName(Flow.WorkRoot) : null;
        var file = Dialogs.PickFile("Добавить проект: выберите сборку SOLIDWORKS", filter, start);
        if (file == null) return;
        var created = Flow.Import(ProductDocument?.ProjectKind == ProjectKind.Project ? ProductDocument.ProjectId : null, file);
        await LoadProductsAsync(reload: true);
        if (created != null) await OpenProductAsync(created);
    }

    /// <summary>Двойной щелчок по неоткрытому изделию в дереве — открыть его.</summary>
    [RelayCommand]
    private async Task OpenStubAsync()
    {
        if (Busy.IsBusy || Node is not { IsProductStub: true } n) return;
        await OpenProductAsync(n.Product!.ToRef());
    }

    /// <summary>«Поставить на учёт»; новое изделие (если выбрано «Открыть») открывается в этом окне.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var created = Flow.Import(ProductDocument?.ProjectKind == ProjectKind.Project ? ProductDocument.ProjectId : null);
        if (created != null) await OpenProductAsync(created);
    }

    [RelayCommand]
    private void OpenAdmin()
    {
        if (!Session.IsAdmin) return;
        Dialogs.ShowDialog(new AdminViewModel(Session));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(Session.Services, Session.Profile);
        if (Dialogs.ShowDialog(vm) != true) return;
        if (vm.WorkspaceChanged)
        {
            Session.RebuildWorkspace();
            _ = RefreshAsync();
        }
        if (vm.ServerChanged && Dialogs.Confirm("Адрес сервера изменён", "Чтобы подключиться к новому серверу, нужно войти заново. Выйти сейчас?", "Выйти", "Позже"))
            _ = LogoutAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (Busy.IsBusy && !Dialogs.Confirm("Выход", "Идёт операция. Отменить её и выйти?", "Выйти", "Остаться")) return;
        Busy.CancelCommand.Execute(null);
        try
        {
            await Session.Api.LogoutAsync();
        }
        catch (Core.Api.PdmApiException)
        {
        }
        Session.Profile.Token = null;
        Session.Profile.TokenExpiresAt = null;
        SaveProfile();
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ действия над узлом

    private ProductNodeViewModel? Node => Tree.SelectedNode;

    private bool HasNode() => Node != null && !Busy.IsBusy;

    private ICommand CommandFor(NodeActionKind kind) => kind switch
    {
        NodeActionKind.OpenInCad => OpenInCadCommand,
        NodeActionKind.CheckOut => CheckOutCommand,
        NodeActionKind.Checkin => CheckinNodeCommand,
        NodeActionKind.UndoCheckOut => UndoCheckOutCommand,
        NodeActionKind.UpdateToLatest => UpdateToLatestCommand,
        NodeActionKind.Rename => RenameCommand,
        NodeActionKind.WhereUsed => WhereUsedCommand,
        NodeActionKind.MarkUnused => MarkUnusedCommand,
        NodeActionKind.ReturnToWork => MarkUnusedCommand,
        NodeActionKind.ShowInExplorer => ShowNodeInExplorerCommand,
        NodeActionKind.OpenAsProduct => OpenAsProductCommand,
        NodeActionKind.OpenAsProductInNewWindow => OpenAsProductInNewWindowCommand,
        NodeActionKind.RegisterLinked => RegisterLinkedCommand,
        NodeActionKind.AddProject => AddProjectCommand,
        NodeActionKind.RegisterLinkedPick => RegisterLinkedPickCommand,
        NodeActionKind.ExpandBranch => ExpandBranchCommand,
        NodeActionKind.CollapseBranch => CollapseBranchCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Label(ProductNodeViewModel n) => Format.Doc(n.LocalRelPath ?? n.FileName, n.Designation);

    /// <summary>Просмотр должен отпустить файл (перед открытием в CAD).</summary>
    public event EventHandler? ReleasePreviewRequested;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void OpenInCad()
    {
        if (Node?.OpenablePath is not { } path) return;
        ReleasePreviewRequested?.Invoke(this, EventArgs.Empty);
        Flow.OpenFile(path);
    }

    [RelayCommand]
    private void ExpandBranch()
    {
        if (Node is { } n) ProductTreeViewModel.ExpandBranch(n);
    }

    [RelayCommand]
    private void CollapseBranch()
    {
        if (Node is { } n) ProductTreeViewModel.CollapseBranch(n);
    }

    /// <summary>
    /// Ссылка на файл, не учтённый в PDM (файла по ссылке нет или нужен другой): пользователь указывает файл модели,
    /// он ставится на учёт рядом с родителем, и ссылка родителя привязывается к нему.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasNode))]
    private async Task RegisterLinkedPickAsync()
    {
        if (Node is not { Type: NodeType.Unresolved } n || n.ParentDocument is not { DocumentId: { } parentId } parent) return;
        var refPath = n.Kit!.RefPath;
        var ext = Path.GetExtension(refPath);
        var all = Session.Workspace.Adapters.All.SelectMany(a => a.Extensions).Select(e => "*" + e).ToList();
        var filter = (ext.Length > 1 ? $"«{Path.GetFileName(refPath)}» и файлы *{ext}|*{ext}|" : string.Empty) +
                     $"Документы CAD ({string.Join(", ", all)})|{string.Join(";", all)}|Все файлы (*.*)|*.*";
        string? start = null;
        try
        {
            var dir = Path.GetDirectoryName(refPath.Replace('/', '\\'));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) start = dir;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        start ??= Flow.WorkRoot;
        var file = Dialogs.PickFile($"Файл модели для «{Path.GetFileName(refPath)}»", filter, start);
        if (file == null) return;
        await Flow.RegisterLinkedAsync(parentId, parent.Kit!.ProjectId, parent.Kit.RelPath, Label(parent), refPath, file);
    }

    /// <summary>Файл «по ссылке» — поставить на учёт и привязать к нему ссылку сборки-родителя.</summary>
    [RelayCommand(CanExecute = nameof(HasNode))]
    private async Task RegisterLinkedAsync()
    {
        if (Node is not { Type: NodeType.Unresolved, LinkPath: { } link } n || n.ParentDocument is not { DocumentId: { } parentId } parent) return;
        await Flow.RegisterLinkedAsync(parentId, parent.Kit!.ProjectId, parent.Kit.RelPath, Label(parent), n.Kit!.RefPath, link);
    }

    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task CheckOutAsync() => Node?.DocumentId is { } id ? Flow.CheckOutAsync(new[] { id }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task CheckinNodeAsync() => Flow.CheckinAsync();

    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task UndoCheckOutAsync() => Node?.DocumentId is { } id ? Flow.UndoCheckOutAsync(new[] { id }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task UpdateToLatestAsync() => Node?.DocumentId is { } id ? Flow.UpdateToLatestAsync(new[] { id }) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void Rename()
    {
        if (Node?.LocalRelPath is { } rel) Flow.Rename(rel);
    }

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void WhereUsed() => Panel.SelectTab(NodeTabIds.WhereUsed);

    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task MarkUnusedAsync() =>
        Node is { DocumentId: { } id } n ? Flow.MarkUnusedAsync(id, !n.IsUnused, Label(n)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void ShowNodeInExplorer()
    {
        if (Node?.OpenablePath is { } path) Flow.ShowInExplorer(path);
    }

    /// <summary>«Открыть как изделие»: сборка становится корнем этого окна.</summary>
    [RelayCommand(CanExecute = nameof(HasNode))]
    private Task OpenAsProductAsync() =>
        Node is { DocumentId: { } id } n ? OpenProductAsync(n.Product?.ToRef() ?? new ProductRef(id, n.Designation, n.Name)) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasNode))]
    private void OpenAsProductInNewWindow()
    {
        if (Node is { DocumentId: { } id } n) NewWindowRequested?.Invoke(this, new ProductRef(id, n.Designation, n.Name));
    }

    // ------------------------------------------------------------------ INodeHost

    public ProductNodeViewModel? FindNode(Guid documentId) => Tree.Find(documentId);

    public bool SelectDocument(Guid documentId) => Tree.Select(documentId);

    public Task OpenVersionAsync(Guid documentId, int version, string label) => Flow.OpenAsync(documentId, version, label);

    private void NotifyAll()
    {
        this.NotifyCommands();
        Tree.NotifyCommands();
    }
}
