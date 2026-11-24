using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Изделие, которое нужно открыть в окне: документ и (если известна) рабочая папка.</summary>
public sealed record ProductRef(Guid DocumentId, string? Designation = null, string? Name = null, string? WorkRoot = null)
{
    public string Title => string.IsNullOrWhiteSpace(Designation) ? Name ?? "изделие" : Designation!;
}

/// <summary>Рабочие папки изделий: недавняя папка изделия, папка по умолчанию, известные рабочие папки.</summary>
public static class ProductFolders
{
    /// <summary>Папка — рабочая папка SolidBrew и в ней есть документ.</summary>
    public static bool Contains(string? workRoot, Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(workRoot)) return false;
        try
        {
            return WorkspaceState.TryLoad(workRoot)?.FindByDocument(documentId) != null;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Последняя рабочая папка, в которую открывалось изделие (и где оно ещё лежит).</summary>
    public static string? RecentFolder(ClientProfile profile, Guid documentId) =>
        profile.RecentProducts.Where(r => r.DocumentId == documentId).OrderByDescending(r => r.At)
            .Select(r => r.WorkRoot).FirstOrDefault(r => Contains(r, documentId));

    /// <summary>Последнее открытое изделие, папка которого ещё существует (открывается при запуске).</summary>
    public static ProductRef? LastProduct(ClientProfile profile) =>
        profile.RecentProducts.OrderByDescending(r => r.At).Where(r => Contains(r.WorkRoot, r.DocumentId))
            .Select(r => new ProductRef(r.DocumentId, r.Designation, r.Name, r.WorkRoot)).FirstOrDefault();

    /// <summary>Папка по умолчанию: <c>&lt;корень&gt;\&lt;обозначение&gt;</c> (или наименование, или имя файла).</summary>
    public static string DefaultFolder(string parent, string? designation, string? name, string? fileName)
    {
        var label = !string.IsNullOrWhiteSpace(designation) ? designation!
            : !string.IsNullOrWhiteSpace(name) ? name!
            : Path.GetFileNameWithoutExtension(fileName ?? "Изделие");
        return Path.Combine(parent, WorkspaceService.ProjectFolderName(label));
    }

    /// <summary>Все известные рабочие папки пользователя (недавние папки и папки недавних изделий).</summary>
    public static List<string> KnownWorkspaces(ClientProfile profile) =>
        profile.RecentWorkspaces.Concat(profile.RecentProducts.Select(r => r.WorkRoot))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Строка переключателя изделий.</summary>
public sealed partial class ProductItem : ObservableObject
{
    public const string PinnedGroup = "Закреплённые";
    public const string RecentGroup = "Недавние";
    public const string RootsGroup = "Корневые изделия";
    public const string FoundGroup = "Найдено";

    public ProductItem(Guid documentId, string? designation, string? name, string group, string? workRoot = null, string? note = null)
    {
        DocumentId = documentId;
        Designation = designation;
        Name = name;
        Group = group;
        WorkRoot = workRoot;
        Note = note;
    }

    public Guid DocumentId { get; }
    public string? Designation { get; }
    public string? Name { get; }
    public string Group { get; }
    public string? WorkRoot { get; }
    public string? Note { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinGlyph))]
    private bool _isPinned;

    public string PinGlyph => IsPinned ? "" : "";

    public string Title => string.IsNullOrWhiteSpace(Designation) ? Name ?? DocumentId.ToString("D") : Designation!;
    public string Subtitle => string.IsNullOrWhiteSpace(Designation) ? string.Empty : Name ?? string.Empty;

    /// <summary>Вторая строка: рабочая папка или проект.</summary>
    public string Details => WorkRoot ?? Note ?? string.Empty;

    public ProductRef ToRef() => new(DocumentId, Designation, Name, WorkRoot);

    public override string ToString() => (Title + " " + Subtitle).Trim();
}

/// <summary>
/// Список изделий для стартового окна и переключателя: закреплённые, недавние (изделие + папка), корневые сборки с сервера,
/// поиск по всем сборкам.
/// </summary>
public sealed partial class ProductCatalogViewModel : ObservableObject
{
    private readonly AppSession _session;
    private List<ProductItem> _roots = new();
    private CancellationTokenSource? _searchCts;
    private readonly object _sync = new();
    private Task? _rootsLoad;
    private int _rootsVersion;

    public ProductCatalogViewModel(AppSession session)
    {
        _session = session;
        RebuildLocal();
    }

    public ObservableCollection<ProductItem> Pinned { get; } = new();
    public ObservableCollection<ProductItem> Recent { get; } = new();
    public ObservableCollection<ProductItem> Roots { get; } = new();
    public ObservableCollection<ProductItem> Found { get; } = new();

    /// <summary>Что показывать: без запроса — закреплённые, недавние, корневые; с запросом — найденные сборки.</summary>
    public ObservableCollection<ProductItem> Items { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private ProductItem? _selectedItem;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public bool IsEmpty => Items.Count == 0 && !IsLoading;

    partial void OnQueryChanged(string value) => _ = SearchAsync();

    /// <summary>Закреплённые и недавние из профиля.</summary>
    public void RebuildLocal()
    {
        lock (_sync) RebuildLocalCore();
    }

    private void RebuildLocalCore()
    {
        var profile = _session.Profile;
        Pinned.ReplaceWith(profile.PinnedProducts.Select(p =>
        {
            var recent = profile.RecentProducts.Where(r => r.DocumentId == p.DocumentId).OrderByDescending(r => r.At).FirstOrDefault();
            return new ProductItem(p.DocumentId, p.Designation, p.Name, ProductItem.PinnedGroup, recent?.WorkRoot) { IsPinned = true };
        }));
        Recent.ReplaceWith(profile.RecentProducts.OrderByDescending(r => r.At)
            .Select(r => new ProductItem(r.DocumentId, r.Designation, r.Name, ProductItem.RecentGroup, r.WorkRoot) { IsPinned = profile.IsPinned(r.DocumentId) }));
        foreach (var r in _roots) r.IsPinned = profile.IsPinned(r.DocumentId);
        RebuildItems();
    }

    /// <summary>
    /// Корневые сборки с сервера. Загружаются один раз: повторный вызов (например, открытие переключателя и стартовое окно)
    /// возвращает ту же загрузку, а не запускает вторую параллельно.
    /// </summary>
    public Task LoadAsync() => _rootsLoad ??= LoadRootsAsync();

    /// <summary>Загрузить корневые заново (после постановки на учёт). Результат прежней загрузки отбрасывается.</summary>
    public Task ReloadAsync() => _rootsLoad = LoadRootsAsync();

    private async Task LoadRootsAsync()
    {
        var version = Interlocked.Increment(ref _rootsVersion);
        IsLoading = true;
        List<ProductItem>? roots = null;
        string status;
        try
        {
            var api = _session.Api;
            var found = await Task.Run(() => api.SearchDocumentsAsync(limit: 200, roots: true));
            roots = found.Where(d => d.Kind == DocumentKind.Assembly)
                .OrderBy(d => d.Designation ?? d.FileName, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new ProductItem(d.Id, d.Designation, d.Name ?? d.FileName, ProductItem.RootsGroup, note: Note(d)) { IsPinned = _session.Profile.IsPinned(d.Id) })
                .ToList();
            status = string.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = "Корневые изделия не загружены: " + ErrorText.Describe(ex).Message;
        }
        lock (_sync)
        {
            if (version != _rootsVersion) return; // устаревшая загрузка
            if (roots != null)
            {
                _roots = roots;
                Roots.ReplaceWith(roots);
            }
            StatusText = status;
            IsLoading = false;
            RebuildItems();
        }
    }

    private static string Note(DocumentSummaryDto d) =>
        $"{d.ProjectName} · v{d.LatestVersion}" + (d.Lock != null ? $" · взят: {(string.IsNullOrWhiteSpace(d.Lock.UserDisplayName) ? d.Lock.UserLogin : d.Lock.UserDisplayName)}" : string.Empty);

    /// <summary>Поиск по всем сборкам (обозначение, наименование, имя файла).</summary>
    [RelayCommand]
    public async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var q = Query.Trim();
        if (q.Length == 0)
        {
            lock (_sync)
            {
                _searchCts = null;
                Found.Clear();
                IsLoading = false;
                RebuildItems();
            }
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token); // ввод ещё идёт
            var api = _session.Api;
            IsLoading = true;
            var found = await Task.Run(() => api.SearchDocumentsAsync(q, limit: 100, ct: cts.Token), cts.Token);
            var items = found.Where(d => d.Kind == DocumentKind.Assembly)
                .OrderBy(d => d.UsedInCount > 0).ThenBy(d => d.Designation ?? d.FileName, StringComparer.CurrentCultureIgnoreCase)
                .Select(d => new ProductItem(d.Id, d.Designation, d.Name ?? d.FileName, ProductItem.FoundGroup, note: Note(d)) { IsPinned = _session.Profile.IsPinned(d.Id) })
                .ToList();
            lock (_sync)
            {
                if (!ReferenceEquals(_searchCts, cts) || cts.IsCancellationRequested) return; // запрос уже другой
                Found.ReplaceWith(items);
                StatusText = Found.Count == 0 ? $"Сборок по запросу «{q}» не найдено." : string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Поиск не выполнен: " + ErrorText.Describe(ex).Message;
        }
        lock (_sync)
        {
            if (!ReferenceEquals(_searchCts, cts)) return;
            IsLoading = false;
            RebuildItems();
        }
    }

    /// <summary>Пересобрать видимый список (вызывается под <see cref="_sync"/>).</summary>
    private void RebuildItems()
    {
        if (Query.Trim().Length > 0)
        {
            Items.ReplaceWith(Found);
        }
        else
        {
            var shown = new HashSet<Guid>(Pinned.Select(p => p.DocumentId));
            var items = new List<ProductItem>(Pinned);
            items.AddRange(Recent);
            foreach (var r in Recent) shown.Add(r.DocumentId);
            items.AddRange(Roots.Where(r => !shown.Contains(r.DocumentId)));
            Items.ReplaceWith(items);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Закрепить или открепить изделие.</summary>
    [RelayCommand]
    public void TogglePin(ProductItem? item)
    {
        if (item == null) return;
        var profile = _session.Profile;
        if (profile.IsPinned(item.DocumentId)) profile.Unpin(item.DocumentId);
        else profile.Pin(item.DocumentId, item.Designation, item.Name);
        try
        {
            _session.SaveProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        RebuildLocal();
        foreach (var f in Found) f.IsPinned = profile.IsPinned(f.DocumentId);
    }

    /// <summary>Убрать изделие из недавних (папка не удаляется).</summary>
    [RelayCommand]
    public void Forget(ProductItem? item)
    {
        if (item == null || item.Group != ProductItem.RecentGroup) return;
        _session.Profile.ForgetProduct(item.DocumentId, item.WorkRoot);
        try
        {
            _session.SaveProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        RebuildLocal();
    }
}

/// <summary>Стартовое окно: выбор изделия или постановка на учёт нового.</summary>
public sealed partial class StartViewModel : DialogViewModel
{
    private readonly AppSession _session;

    public StartViewModel(AppSession session) : base(session.Services.Dialogs)
    {
        _session = session;
        Catalog = new ProductCatalogViewModel(session);
    }

    public override string Title => "SolidBrew PDM — выбор изделия";

    public ProductCatalogViewModel Catalog { get; }

    public string UserText => _session.UserText;
    public bool CanModify => _session.CanModify;

    /// <summary>Выбранное изделие (итог окна).</summary>
    public ProductRef? Choice { get; private set; }

    public override Task InitializeAsync() => Catalog.LoadAsync();

    [RelayCommand]
    private void Open(ProductItem? item)
    {
        item ??= Catalog.SelectedItem;
        if (item == null) return;
        Choice = item.ToRef();
        Close(true);
    }

    /// <summary>«Поставить на учёт новое изделие»; после постановки (если выбрано «Открыть») изделие открывается.</summary>
    [RelayCommand]
    private void Import()
    {
        var vm = new ImportViewModel(_session);
        if (Dialogs.ShowDialog(vm) != true || vm.Result is not { Success: true } result) return;
        if (vm.OpenAfter)
        {
            Choice = new ProductRef(result.HeadDocumentId);
            Close(true);
            return;
        }
        Catalog.RebuildLocal();
        _ = Catalog.ReloadAsync();
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
