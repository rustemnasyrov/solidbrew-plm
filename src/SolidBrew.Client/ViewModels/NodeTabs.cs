using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Cad;
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

    /// <summary>
    /// Записать свойства CAD в файл документа и сдать новую версию. После успеха панель узла показывается заново
    /// (с новой версией). false — не записано (причина уже показана).
    /// </summary>
    Task<bool> WritePropertiesAsync(Guid documentId, IReadOnlyList<CadPropertyChange> changes);

    /// <summary>Заполненные в карточках и ещё не записанные значения свойств (записываются по «Сдать»).</summary>
    PendingPropertyEdits PendingProperties { get; }
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
    public PropertyPivotRow(string name, string[] values, string[]? scopes = null, bool editable = false, Action? changed = null, string[]? alsoScopes = null)
    {
        Name = name;
        Values = values;
        Cells = values.Select((v, i) => new PropertyCell(scopes?[i] ?? string.Empty, v, editable && string.IsNullOrWhiteSpace(v), changed, alsoScopes)).ToArray();
    }

    public string Name { get; }
    /// <summary>Значения из сданной версии (до правки в карточке).</summary>
    public string[] Values { get; }
    /// <summary>Ячейки по столбцам; пустую можно заполнить, если документ можно менять.</summary>
    public PropertyCell[] Cells { get; }

    /// <summary>Значения нет ни в одной конфигурации.</summary>
    public bool IsEmpty => Values.All(string.IsNullOrWhiteSpace);
}

/// <summary>Значение свойства в одном столбце (уровень файла или конфигурация).</summary>
public sealed partial class PropertyCell : ObservableObject
{
    private readonly Action? _changed;

    public PropertyCell(string scope, string original, bool isEditable, Action? changed, string[]? alsoScopes = null)
    {
        Scope = scope;
        Original = original;
        _value = original;
        IsEditable = isEditable;
        _changed = changed;
        AlsoScopes = alsoScopes ?? Array.Empty<string>();
    }

    /// <summary>Конфигурация; «» — уровень файла.</summary>
    public string Scope { get; }
    /// <summary>Куда ещё записать то же значение (конфигурации, где свойство есть, но пустое) — режим «одно значение».</summary>
    public string[] AlsoScopes { get; }
    public string Original { get; }
    /// <summary>Пустое в файле — можно заполнить (записывается в файл CAD и сдаётся новой версией).</summary>
    public bool IsEditable { get; }

    [ObservableProperty]
    private string _value;

    /// <summary>Заполнено в карточке и ещё не записано.</summary>
    public bool IsChanged => IsEditable && !string.IsNullOrWhiteSpace(Value) && !string.Equals(Value.Trim(), Original.Trim(), StringComparison.Ordinal);

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
        _changed?.Invoke();
    }
}

/// <summary>
/// «Карточка»: свойства CAD по конфигурациям, файл, версия CAD, проект. Источник истины — файл: пустые свойства можно
/// заполнить здесь, и PDM запишет их в файл CAD и сдаст новую версию («Записать в файл»).
/// </summary>
public sealed partial class CardTabViewModel : NodeTabViewModel
{
    public const string FileLevelColumn = "Файл (общие)";

    public CardTabViewModel(NodeContext context) : base(context)
    {
    }

    /// <summary>Пустые свойства можно заполнить: документ в рабочей папке, последняя версия, не взят другим, есть права.</summary>
    public bool CanEdit => Context.Session.CanModify && Node.IsDocument && !Node.IsProductStub && Node.LocalRelPath != null
                           && !Node.IsOutdated && !Node.IsLockedByOther;

    /// <summary>Почему заполнять нельзя (подсказка под таблицей); null — можно.</summary>
    public string? EditHint => CanEdit || !Node.IsDocument ? null
        : !Context.Session.CanModify ? "Заполнять свойства может конструктор или администратор."
        : Node.IsLockedByOther ? "Документ взят в работу другим пользователем — свойства заполнить нельзя."
        : Node.IsOutdated ? "В рабочей папке не последняя версия — обновите документ, чтобы заполнить свойства."
        : "Документа нет в рабочей папке — откройте изделие, чтобы заполнить свойства.";

    /// <summary>Сколько заполненных и ещё не записанных значений.</summary>
    public int PendingCount => _allProperties.Sum(r => r.Cells.Count(c => c.IsChanged));
    public bool HasPending => PendingCount > 0;
    public string SaveText => "Записать сейчас";

    /// <summary>Подсказка над кнопками: сколько заполнено здесь и в других документах.</summary>
    public string PendingText
    {
        get
        {
            var others = Context.Host.PendingProperties.Documents.Count(d => d != Node.DocumentId);
            return $"Заполнено значений: {PendingCount}." + (others > 0 ? $" Ещё в других документах: {others}." : string.Empty) +
                   " «Сдать» вверху окна запишет их во все файлы и сдаст документы новыми версиями; «Записать сейчас» — только этот документ.";
        }
    }

    /// <summary>Строки строятся заново — значения из хранилища подставляются, не записываясь в него повторно.</summary>
    private bool _building;

    private void OnCellChanged()
    {
        // Заполненное хранится в окне изделия: переход к другому узлу его не теряет, «Сдать» записывает всё сразу.
        if (!_building && _editable && Node.DocumentId is { } id)
            Context.Host.PendingProperties.Set(id, Format.Doc(Node.LocalRelPath ?? Node.FileName, Node.Designation), PendingChanges());
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Заполненные значения для записи.</summary>
    public List<CadPropertyChange> PendingChanges() => _allProperties
        .SelectMany(r => r.Cells.Where(c => c.IsChanged).SelectMany(c => new[] { c.Scope }.Concat(c.AlsoScopes).Distinct(StringComparer.Ordinal)
            .Select(scope => new CadPropertyChange(scope, r.Name, c.Value.Trim()))))
        .ToList();

    private bool _saving;

    private bool CanSave() => HasPending && !_saving;

    /// <summary>Записать заполненные свойства в файл CAD и сдать новую версию.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Node.DocumentId is not { } id) return;
        _saving = true;
        SaveCommand.NotifyCanExecuteChanged();
        try
        {
            await Context.Host.WritePropertiesAsync(id, PendingChanges());
        }
        finally
        {
            _saving = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Сбросить заполненное в карточке.</summary>
    [RelayCommand(CanExecute = nameof(HasPending))]
    private void Revert()
    {
        foreach (var c in _allProperties.SelectMany(r => r.Cells).Where(c => c.IsEditable)) c.Value = c.Original;
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

    private void ApplyEmptyFilter()
    {
        Properties = ShowEmpty ? _allProperties : _allProperties.Where(p => !p.IsEmpty).ToList();
        // Один столбец значений — таблица делится на две половины рядом (по порядку: первая половина слева).
        var half = (Properties.Count + 1) / 2;
        LeftProperties = Properties.Take(half).ToList();
        RightProperties = Properties.Skip(half).ToList();
        OnPropertyChanged(nameof(IsTwoColumn));
        OnPropertyChanged(nameof(IsOneTable));
    }

    /// <summary>Левая и правая половины свойств — в режиме одного значения таблица показывается в две колонки.</summary>
    [ObservableProperty]
    private IReadOnlyList<PropertyPivotRow> _leftProperties = Array.Empty<PropertyPivotRow>();

    [ObservableProperty]
    private IReadOnlyList<PropertyPivotRow> _rightProperties = Array.Empty<PropertyPivotRow>();

    /// <summary>Две таблицы рядом: режим одного значения и есть что показать.</summary>
    public bool IsTwoColumn => !ByConfiguration && HasProperties;
    /// <summary>Одна широкая таблица: режим «По конфигурациям».</summary>
    public bool IsOneTable => ByConfiguration && HasProperties;

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
        // Версия файла уже содержит название CAD («SOLIDWORKS 2026»); без неё — название CAD, а не внутренний код.
        Info.Add(new InfoRow("CAD", v?.CadVersion is { Length: > 0 } cv ? cv
            : Context.Session.Workspace.Adapters.Get(doc.Cad)?.DisplayName ?? doc.Cad));
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
        _version = v;
        _keys = keys;
        // Служебное свойство PDM (убранные ссылки) в карточке не показывается.
        _names = keys.SelectMany(k => v.Properties[k].Keys)
            .Where(n => !string.Equals(n, DocumentProperties.RemovedReferencesKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Карточка показывает версию из рабочей папки; заполнять — только её, и только если она последняя.
        _editable = CanEdit && v.Number == doc.LatestVersion;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(EditHint));
        OnPropertyChanged(nameof(HasConfigurations));
        BuildRows();
        if (_names.Count == 0) Message = "В файле нет свойств.";
        else if (Properties.Count == 0) Message = "Все свойства файла пустые.";
    }

    public const string ValueColumn = "Значение";

    private VersionDto? _version;
    private List<string> _keys = new();
    private List<string> _names = new();
    private bool _editable;

    /// <summary>
    /// Показывать значения по конфигурациям (столбцы «Файл (общие)» и каждая конфигурация). По умолчанию — один столбец
    /// «Значение»: общее значение, а если его нет — значение конфигураций.
    /// </summary>
    [ObservableProperty]
    private bool _byConfiguration;

    partial void OnByConfigurationChanged(bool value) => BuildRows();

    /// <summary>Есть конфигурации со своими свойствами (кроме служебных развёрток листового металла) — есть что показывать по ним.</summary>
    public bool HasConfigurations => _keys.Any(k => k.Length > 0 && !IsFlatPattern(k));

    /// <summary>Служебная конфигурация развёртки листового металла («По умолчаниюSM-FLAT-PATTERN»): её SOLIDWORKS создаёт сам.</summary>
    public static bool IsFlatPattern(string configuration) => configuration.IndexOf("FLAT-PATTERN", StringComparison.OrdinalIgnoreCase) >= 0;

    private void BuildRows()
    {
        if (_version is not { } v) return;
        string ValueOf(string k, string n) => v.Properties.TryGetValue(k, out var p) ? Find(p, n) : string.Empty;
        bool Has(string k, string n) => v.Properties.TryGetValue(k, out var p) && p.Keys.Any(x => string.Equals(x, n, StringComparison.CurrentCultureIgnoreCase));
        var configs = _keys.Where(k => k.Length > 0).ToList();
        var visible = configs.Where(k => !IsFlatPattern(k)).ToList();

        if (ByConfiguration)
        {
            // Столбец «Файл (общие)» есть всегда, когда можно заполнять (у чертежа и детали без общих свойств — тоже).
            var keys = new List<string>();
            if (v.Properties.ContainsKey(string.Empty) || _editable) keys.Add(string.Empty);
            keys.AddRange(visible);
            Columns = keys.Select(k => k.Length == 0 ? FileLevelColumn : k).ToList();
            _allProperties = _names.Select(n => new PropertyPivotRow(n, keys.Select(k => ValueOf(k, n)).ToArray(), keys.ToArray(), _editable, OnCellChanged)).ToList();
        }
        else
        {
            Columns = new[] { ValueColumn };
            _allProperties = _names.Select(n =>
            {
                var shared = ValueOf(string.Empty, n);
                string shown;
                if (!string.IsNullOrWhiteSpace(shared))
                {
                    shown = shared;
                }
                else
                {
                    var own = visible.Select(k => (Config: k, Value: ValueOf(k, n))).Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
                    shown = own.Select(x => x.Value).Distinct(StringComparer.Ordinal).Count() switch
                    {
                        0 => string.Empty,
                        1 => own[0].Value,
                        _ => string.Join("; ", own.Select(x => $"{x.Config}: {x.Value}")),
                    };
                }
                // Заполненное здесь пишется в общие свойства и в конфигурации, где свойство есть, но пустое:
                // иначе пустое свойство конфигурации перекрыло бы общее в спецификации и штампе.
                var also = configs.Where(k => Has(k, n)).ToArray();
                return new PropertyPivotRow(n, new[] { shown }, new[] { string.Empty }, _editable, OnCellChanged, also);
            }).ToList();
        }

        // Заполненное раньше (в этой карточке или до перехода к другому узлу) — обратно в ячейки.
        if (Node.DocumentId is { } id && Context.Host.PendingProperties.For(id) is { Count: > 0 } saved)
        {
            _building = true;
            try
            {
                foreach (var row in _allProperties)
                    foreach (var cell in row.Cells.Where(c => c.IsEditable))
                    {
                        var hit = saved.FirstOrDefault(c => c.Configuration == cell.Scope && string.Equals(c.Name, row.Name, StringComparison.CurrentCultureIgnoreCase));
                        if (hit != null) cell.Value = hit.Value;
                    }
            }
            finally
            {
                _building = false;
            }
        }
        OnCellChanged();
        EmptyCount = _allProperties.Count(p => p.IsEmpty);
        OnPropertyChanged(nameof(HasAnyProperties));
        ApplyEmptyFilter();
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
    /// <summary>Файл для системного значка (null — глиф).</summary>
    public string? IconFile { get; init; }
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
                IconFile = file,
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
                IconFile = LinkExists(r.RefPath) ? Format.FileName(r.RefPath) : null,
                Count = 1,
                Mark = StateMark.Missing,
                NoteText = LinkExists(r.RefPath) ? "не учтён в PDM, по ссылке" : "не учтён в PDM, файла нет",
            });
        }
        Components.ReplaceWith(rows.OrderBy(r => r.Mark == StateMark.Missing).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase));
        Summary = $"Компонентов в v{v.Number}: {Components.Count}.";
        if (Components.Count == 0) Message = "Сборка не ссылается на другие документы.";
    }

    private static bool LinkExists(string refPath)
    {
        try
        {
            var p = refPath.Replace('/', '\\');
            return Path.IsPathFullyQualified(p) && File.Exists(p);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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
    /// <summary>Файл для системного значка (null — глиф).</summary>
    public string? IconFile { get; init; }
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
                IconFile = file,
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
