using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client.ViewModels;

/// <summary>Что вкладке панели узла нужно от окна изделия.</summary>
public interface INodeHost
{
    AppSession Session { get; }

    /// <summary>Узел дерева по документу (первое вхождение) или null.</summary>
    ProductNodeViewModel? FindNode(Guid documentId);

    /// <summary>Выделить документ в дереве. false — его нет в изделии.</summary>
    bool SelectDocument(Guid documentId);

    /// <summary>«Открыть» выбранную версию документа в рабочую папку окна.</summary>
    Task OpenVersionAsync(Guid documentId, int version, string label);
}

/// <summary>Узел, для которого создаются вкладки, и общий для вкладок доступ к сведениям о документе.</summary>
public sealed class NodeContext
{
    private readonly CancellationTokenSource _cts = new();
    private Task<DocumentDetailDto>? _detail;

    public NodeContext(ProductNodeViewModel node, INodeHost host)
    {
        Node = node;
        Host = host;
    }

    public ProductNodeViewModel Node { get; }
    public INodeHost Host { get; }
    public AppSession Session => Host.Session;
    public NodeType Type => Node.Type;

    /// <summary>Отменяется, когда выбран другой узел.</summary>
    public CancellationToken Cancellation => _cts.Token;

    public void Cancel() => _cts.Cancel();

    /// <summary>Документ и его версии (запрашивается один раз на узел, общий для всех вкладок).</summary>
    public Task<DocumentDetailDto> GetDetailAsync()
    {
        if (Node.DocumentId is not { } id) throw new InvalidOperationException("Узел не является документом PDM.");
        var api = Session.Api;
        var ct = Cancellation;
        lock (_cts) return _detail ??= Task.Run(() => api.GetDocumentAsync(id, ct), ct);
    }

    /// <summary>Версия, показанная в панели (в рабочей папке, иначе в изделии).</summary>
    public static VersionDto? VersionOf(DocumentDetailDto detail, int? number) =>
        detail.Versions.FirstOrDefault(v => v.Number == number) ?? detail.Versions.OrderByDescending(v => v.Number).FirstOrDefault();
}

/// <summary>
/// Вкладка панели узла — подключаемый модуль: заголовок, для каких узлов, своя ViewModel.
/// Новая функция (спецификация, PDF, нормоконтроль…) добавляется новой вкладкой, не трогая остальной интерфейс.
/// </summary>
public interface INodeTab
{
    /// <summary>Постоянный идентификатор (для запоминания выбранной вкладки).</summary>
    string Id { get; }

    string Header { get; }

    bool AppliesTo(NodeContext context);

    ObservableObject CreateViewModel(NodeContext context);
}

/// <summary>Вкладка, заданная функциями (регистрация одной строкой).</summary>
public sealed class NodeTab : INodeTab
{
    private readonly Func<NodeContext, bool> _appliesTo;
    private readonly Func<NodeContext, ObservableObject> _create;

    public NodeTab(string id, string header, Func<NodeContext, bool> appliesTo, Func<NodeContext, ObservableObject> create)
    {
        Id = id;
        Header = header;
        _appliesTo = appliesTo;
        _create = create;
    }

    public string Id { get; }
    public string Header { get; }
    public bool AppliesTo(NodeContext context) => _appliesTo(context);
    public ObservableObject CreateViewModel(NodeContext context) => _create(context);
}

public static class NodeTabIds
{
    public const string Card = "card";
    public const string Composition = "composition";
    public const string Model = "model";
    public const string Versions = "versions";
    public const string WhereUsed = "where-used";
    public const string Journal = "journal";
}

/// <summary>Реестр вкладок панели узла; порядок регистрации — порядок вкладок.</summary>
public sealed class NodeTabRegistry
{
    private readonly List<INodeTab> _tabs = new();

    public IReadOnlyList<INodeTab> Tabs => _tabs;

    public NodeTabRegistry Register(INodeTab tab)
    {
        if (_tabs.Any(t => t.Id == tab.Id)) throw new ArgumentException($"Вкладка «{tab.Id}» уже зарегистрирована.", nameof(tab));
        _tabs.Add(tab);
        return this;
    }

    public NodeTabRegistry Register(string id, string header, Func<NodeContext, bool> appliesTo, Func<NodeContext, ObservableObject> create) =>
        Register(new NodeTab(id, header, appliesTo, create));

    public IEnumerable<INodeTab> For(NodeContext context) => _tabs.Where(t => t.AppliesTo(context));

    /// <summary>
    /// Вкладки этапа 1. Вкладки следующих этапов добавляются здесь одной строкой, например:
    /// <c>.Register("spec", "Спецификация", c =&gt; c.Type == NodeType.Assembly, c =&gt; new SpecificationTabViewModel(c))</c>.
    /// </summary>
    public static NodeTabRegistry CreateDefault() => new NodeTabRegistry()
        .Register(NodeTabIds.Card, "Карточка", c => c.Node.IsDocument || c.Node.IsDraft, c => new CardTabViewModel(c))
        .Register(NodeTabIds.Composition, "Состав", c => c.Type == NodeType.Assembly, c => new CompositionTabViewModel(c))
        .Register(NodeTabIds.Model, "Модель", c => c.Type == NodeType.Drawing, c => new ModelTabViewModel(c))
        .Register(NodeTabIds.Versions, "Версии", c => c.Node.IsDocument, c => new VersionsTabViewModel(c))
        .Register(NodeTabIds.WhereUsed, "Где применяется", c => c.Node.IsDocument, c => new WhereUsedTabViewModel(c))
        .Register(NodeTabIds.Journal, "Журнал", c => c.Node.IsDocument, c => new JournalTabViewModel(c));
}

/// <summary>Вкладка в панели: заголовок и содержимое.</summary>
public sealed class NodeTabItem
{
    public NodeTabItem(string id, string header, ObservableObject content)
    {
        Id = id;
        Header = header;
        Content = content;
    }

    public string Id { get; }
    public string Header { get; }
    public ObservableObject Content { get; }

    public override string ToString() => Header;
}

/// <summary>Основа вкладки: загрузка при первом показе, текст ошибки или пустого состояния.</summary>
public abstract partial class NodeTabViewModel : ObservableObject
{
    private Task? _load;

    protected NodeTabViewModel(NodeContext context) => Context = context;

    public NodeContext Context { get; }
    public ProductNodeViewModel Node => Context.Node;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Сообщение вместо содержимого (ошибка, «нет данных»).</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>Загрузить при первом показе (повторно не загружается).</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_loadSync) return _load ??= RunLoadAsync();
    }

    private readonly object _loadSync = new();

    private async Task RunLoadAsync()
    {
        IsLoading = true;
        try
        {
            await LoadCoreAsync(Context.Cancellation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Message = ErrorText.Describe(ex).Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected abstract Task LoadCoreAsync(CancellationToken ct);
}

// ---------------------------------------------------------------------------------------------- Карточка

public sealed record InfoRow(string Label, string Value);

/// <summary>Строка таблицы свойств: имя свойства и значения по столбцам (уровень файла и конфигурации).</summary>
public sealed class PropertyPivotRow
{
    public PropertyPivotRow(string name, string[] values)
    {
        Name = name;
        Values = values;
    }

    public string Name { get; }
    public string[] Values { get; }

    /// <summary>Значения нет ни в одной конфигурации.</summary>
    public bool IsEmpty => Values.All(string.IsNullOrWhiteSpace);
}

/// <summary>«Карточка»: свойства CAD по конфигурациям (только чтение — источник истины файл), файл, версия CAD, проект.</summary>
public sealed partial class CardTabViewModel : NodeTabViewModel
{
    public const string FileLevelColumn = "Файл (общие)";

    public CardTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<InfoRow> Info { get; } = new();

    /// <summary>Заголовки столбцов значений (уровень файла, затем конфигурации).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _columns = Array.Empty<string>();

    /// <summary>Показанные строки: по умолчанию только свойства со значением хотя бы в одной конфигурации.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyPivotRow> _properties = Array.Empty<PropertyPivotRow>();

    /// <summary>Все свойства файла, включая пустые (в шаблонах ГОСТ их десятки).</summary>
    private IReadOnlyList<PropertyPivotRow> _allProperties = Array.Empty<PropertyPivotRow>();

    /// <summary>Показывать и пустые свойства.</summary>
    [ObservableProperty]
    private bool _showEmpty;

    /// <summary>Сколько свойств без значения ни в одной конфигурации.</summary>
    [ObservableProperty]
    private int _emptyCount;

    public bool HasProperties => Properties.Count > 0;
    public bool HasEmpty => EmptyCount > 0;
    public bool HasAnyProperties => _allProperties.Count > 0;
    public string ShowEmptyText => $"Показать пустые ({EmptyCount})";

    partial void OnPropertiesChanged(IReadOnlyList<PropertyPivotRow> value) => OnPropertyChanged(nameof(HasProperties));

    partial void OnShowEmptyChanged(bool value) => ApplyEmptyFilter();

    partial void OnEmptyCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasEmpty));
        OnPropertyChanged(nameof(ShowEmptyText));
    }

    private void ApplyEmptyFilter() =>
        Properties = ShowEmpty ? _allProperties : _allProperties.Where(p => !p.IsEmpty).ToList();

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var node = Node;
        if (node.IsDraft)
        {
            var f = node.File!;
            Info.Add(new InfoRow("Файл", f.RelPath));
            Info.Add(new InfoRow("Вид", Format.Kind(f.Kind)));
            Info.Add(new InfoRow("CAD", f.Cad));
            Info.Add(new InfoRow("Состояние", "черновик — ещё не поставлен на учёт"));
            Message = "Свойства CAD появятся в карточке после сдачи файла.";
            return;
        }
        var detail = await Context.GetDetailAsync();
        var doc = detail.Document;
        var v = NodeContext.VersionOf(detail, node.ShownVersion);
        Info.Add(new InfoRow("Файл", node.LocalRelPath ?? doc.RelPath));
        if (v != null) Info.Add(new InfoRow("Версия", $"v{v.Number} от {Format.Time(v.At)}, {Author(v)}" + (v.Number == doc.LatestVersion ? " (последняя)" : $" (последняя v{doc.LatestVersion})")));
        Info.Add(new InfoRow("Проект", doc.ProjectName + (doc.ProjectKind == ProjectKind.Library ? " (библиотека)" : string.Empty)));
        Info.Add(new InfoRow("CAD", doc.Cad + (v?.CadVersion is { Length: > 0 } cv ? ", " + cv : string.Empty)));
        if (v != null && v.Configurations.Count > 0) Info.Add(new InfoRow("Конфигурации", string.Join(", ", v.Configurations)));
        if (v != null) Info.Add(new InfoRow("Размер", Format.Bytes(v.Size)));
        if (doc.Status == DocumentStatus.Unused) Info.Add(new InfoRow("Статус", "не используется"));
        if (v == null)
        {
            Message = "У документа нет версий.";
            return;
        }

        var keys = new List<string>();
        if (v.Properties.ContainsKey(string.Empty)) keys.Add(string.Empty);
        keys.AddRange(v.Configurations.Where(c => v.Properties.ContainsKey(c)));
        keys.AddRange(v.Properties.Keys.Where(k => k.Length > 0 && !keys.Contains(k)));
        var names = keys.SelectMany(k => v.Properties[k].Keys).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        Columns = keys.Select(k => k.Length == 0 ? FileLevelColumn : k).ToList();
        _allProperties = names.Select(n => new PropertyPivotRow(n, keys.Select(k => Find(v.Properties[k], n)).ToArray())).ToList();
        EmptyCount = _allProperties.Count(p => p.IsEmpty);
        OnPropertyChanged(nameof(HasAnyProperties));
        ApplyEmptyFilter();
        if (names.Count == 0) Message = "В файле нет свойств.";
        else if (Properties.Count == 0) Message = "Все свойства файла пустые.";
    }

    private static string Find(Dictionary<string, string> props, string name) =>
        props.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.CurrentCultureIgnoreCase)).Value ?? string.Empty;

    internal static string Author(VersionDto v) => string.IsNullOrWhiteSpace(v.AuthorDisplayName) ? v.AuthorLogin : v.AuthorDisplayName;
}

// ---------------------------------------------------------------------------------------------- Состав

public sealed class ComponentRow
{
    public Guid? DocumentId { get; init; }
    public string Glyph { get; init; } = Glyphs.Document;
    public string Title { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string VersionText { get; init; } = string.Empty;
    public int Count { get; init; }
    public StateMark Mark { get; init; }
    public string NoteText { get; init; } = string.Empty;
}

/// <summary>«Состав» сборки: прямые компоненты с версией и числом вхождений (ссылок в файле сборки).</summary>
public sealed partial class CompositionTabViewModel : NodeTabViewModel
{
    public CompositionTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<ComponentRow> Components { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var detail = await Context.GetDetailAsync();
        var v = NodeContext.VersionOf(detail, Node.ShownVersion);
        if (v == null) return;
        var rows = new List<ComponentRow>();
        foreach (var g in v.References.Where(r => r.ChildDocumentId != null).GroupBy(r => (r.ChildDocumentId!.Value, r.ChildVersion)))
        {
            var r = g.First();
            var tree = Context.Host.FindNode(g.Key.Value);
            var file = Format.FileName(r.ChildRelPath ?? r.RefPath);
            var outdated = r.ChildLatestVersion is { } l && r.ChildVersion is { } cv && l > cv;
            rows.Add(new ComponentRow
            {
                DocumentId = g.Key.Value,
                Glyph = tree?.Glyph ?? Glyphs.Document,
                Title = tree?.Designation is { Length: > 0 } d ? d : file,
                Name = tree?.Name ?? string.Empty,
                FileName = file,
                VersionText = $"v{r.ChildVersion}" + (outdated ? $", есть v{r.ChildLatestVersion}" : string.Empty),
                Count = g.Count(),
                Mark = outdated ? StateMark.Outdated : StateMark.None,
            });
        }
        foreach (var r in v.References.Where(r => r.ChildDocumentId == null))
        {
            rows.Add(new ComponentRow
            {
                Glyph = Glyphs.Missing,
                Title = Format.FileName(r.RefPath),
                FileName = r.RefPath,
                Count = 1,
                Mark = StateMark.Missing,
                NoteText = "не учтён в PDM",
            });
        }
        Components.ReplaceWith(rows.OrderBy(r => r.Mark == StateMark.Missing).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        Summary = $"Компонентов в v{v.Number}: {Components.Count}.";
        if (Components.Count == 0) Message = "Сборка не ссылается на другие документы.";
    }

    [RelayCommand]
    private void GoTo(ComponentRow? row)
    {
        if (row?.DocumentId is { } id) Context.Host.SelectDocument(id);
    }
}

// ---------------------------------------------------------------------------------------------- Модель

public sealed class ModelRow
{
    public Guid? DocumentId { get; init; }
    public string Glyph { get; init; } = Glyphs.Document;
    public string Title { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DrawnWithText { get; init; } = string.Empty;
    public string InProductText { get; init; } = string.Empty;
    public bool IsMismatch { get; init; }
    public StateMark Mark => IsMismatch ? StateMark.Outdated : StateMark.None;
}

/// <summary>«Модель» чертежа: что он изображает и с какой версией модели сдан.</summary>
public sealed partial class ModelTabViewModel : NodeTabViewModel
{
    public ModelTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<ModelRow> Models { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _hasMismatch;

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var detail = await Context.GetDetailAsync();
        var v = NodeContext.VersionOf(detail, Node.ShownVersion);
        if (v == null) return;
        var rows = new List<ModelRow>();
        foreach (var r in v.References)
        {
            if (r.ChildDocumentId is not { } id)
            {
                rows.Add(new ModelRow { Glyph = Glyphs.Missing, Title = Format.FileName(r.RefPath), Name = "не учтён в PDM" });
                continue;
            }
            var tree = Context.Host.FindNode(id);
            var file = Format.FileName(r.ChildRelPath ?? r.RefPath);
            var inProduct = tree?.ShownVersion;
            rows.Add(new ModelRow
            {
                DocumentId = id,
                Glyph = tree?.Glyph ?? Glyphs.Document,
                Title = tree?.Designation is { Length: > 0 } d ? d : file,
                Name = tree?.Name ?? string.Empty,
                DrawnWithText = r.ChildVersion is { } cv ? $"v{cv}" + (r.ChildLatestVersion is { } l && l != cv ? $" (последняя v{l})" : string.Empty) : string.Empty,
                InProductText = inProduct is { } ip ? $"v{ip}" : "нет в изделии",
                IsMismatch = inProduct != null && r.ChildVersion != null && inProduct != r.ChildVersion,
            });
        }
        Models.ReplaceWith(rows);
        HasMismatch = Node.Kit?.DrawingModelVersionMismatch == true || rows.Any(r => r.IsMismatch);
        Summary = rows.Count == 0
            ? "Чертёж не ссылается на модели."
            : HasMismatch
                ? $"Чертёж v{v.Number} сдан с другой версией модели, чем в изделии. Чтобы обновить его: возьмите чертёж в работу, откройте в CAD с текущей моделью и сдайте."
                : $"Чертёж v{v.Number} сдан с теми же версиями моделей, что в изделии.";
    }

    [RelayCommand]
    private void GoTo(ModelRow? row)
    {
        if (row?.DocumentId is { } id) Context.Host.SelectDocument(id);
    }
}

// ---------------------------------------------------------------------------------------------- Версии

public sealed class VersionItem
{
    public VersionItem(VersionDto v, int latest, int? inFolder)
    {
        Version = v;
        IsLatest = v.Number == latest;
        IsInFolder = v.Number == inFolder;
    }

    public VersionDto Version { get; }
    public int Number => Version.Number;
    public bool IsLatest { get; }
    public bool IsInFolder { get; }
    public string NumberText => $"v{Version.Number}";
    public string Author => CardTabViewModel.Author(Version);
    public string DateText => Format.Time(Version.At);
    public string Comment => Version.Comment ?? string.Empty;
    public string CadVersion => Version.CadVersion ?? string.Empty;

    public string NoteText => string.Join(", ", new[] { IsInFolder ? "в рабочей папке" : null, IsLatest ? "последняя" : null }.Where(s => s != null));
}

/// <summary>«Версии»: история с авторами, датами и комментариями; «Открыть эту версию».</summary>
public sealed partial class VersionsTabViewModel : NodeTabViewModel
{
    public VersionsTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<VersionItem> Versions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenVersionCommand))]
    private VersionItem? _selectedVersion;

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var detail = await Context.GetDetailAsync();
        Versions.ReplaceWith(detail.Versions.OrderByDescending(v => v.Number).Select(v => new VersionItem(v, detail.Document.LatestVersion, Node.LocalVersion)));
        SelectedVersion = Versions.FirstOrDefault(v => v.IsInFolder) ?? Versions.FirstOrDefault();
    }

    private bool CanOpen(VersionItem? item) => (item ?? SelectedVersion) is { IsInFolder: false } && Node.File != null;

    /// <summary>Положить выбранную версию (с её комплектом) в рабочую папку окна.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenVersionAsync(VersionItem? item)
    {
        var v = item ?? SelectedVersion;
        if (v == null || Node.DocumentId is not { } id) return Task.CompletedTask;
        return Context.Host.OpenVersionAsync(id, v.Number, Format.Doc(Node.LocalRelPath ?? Node.FileName, Node.Designation));
    }
}

// ---------------------------------------------------------------------------------------------- Где применяется

/// <summary>«Где применяется»: сборки и чертежи, ссылающиеся на документ; отметка «не последняя версия».</summary>
public sealed partial class WhereUsedTabViewModel : NodeTabViewModel
{
    public WhereUsedTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<WhereUsedRow> Items { get; } = new();

    [ObservableProperty]
    private string _summary = string.Empty;

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var ws = Context.Session.Workspace;
        var id = Node.DocumentId!.Value;
        var version = Node.ShownVersion!.Value;
        var view = await Task.Run(() => ws.GetWhereUsedAsync(id, version, ct), ct);
        Items.ReplaceWith(view.ThisVersion.Select(i => new WhereUsedRow(i, true)).Concat(view.OtherVersions.Select(i => new WhereUsedRow(i, false))));
        var notLatest = Items.Count(i => i.UsesNotLatest);
        Summary = Items.Count == 0
            ? $"v{version} нигде не применяется: на документ не ссылаются сборки и чертежи."
            : $"Применений v{version}: {view.ThisVersion.Count}, других версий: {view.OtherVersions.Count}." +
              (notLatest > 0 ? $" Используют не последнюю версию: {notLatest}." : string.Empty);
    }

    [RelayCommand]
    private void GoTo(WhereUsedRow? row)
    {
        if (row != null) Context.Host.SelectDocument(row.ParentDocumentId);
    }
}

// ---------------------------------------------------------------------------------------------- Журнал

/// <summary>«Журнал»: кто что делал с документом.</summary>
public sealed partial class JournalTabViewModel : NodeTabViewModel
{
    public JournalTabViewModel(NodeContext context) : base(context)
    {
    }

    public ObservableCollection<JournalRow> Entries { get; } = new();

    protected override async Task LoadCoreAsync(CancellationToken ct)
    {
        var api = Context.Session.Api;
        var id = Node.DocumentId!.Value;
        var entries = await Task.Run(() => api.GetDocumentJournalAsync(id, ct), ct);
        Entries.ReplaceWith(entries.OrderByDescending(e => e.At).Select(e => new JournalRow(e)));
        if (Entries.Count == 0) Message = "Записей в журнале нет.";
    }
}
