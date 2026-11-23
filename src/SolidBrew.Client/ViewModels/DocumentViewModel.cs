using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Экран «Документ» (6.1 п. 4, 7.7, 7.8): свойства версии по конфигурациям, история версий, зависимости,
/// «Где применяется» (с отметкой не последней версии), производные файлы, журнал.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    public const string FileLevel = "Общие (уровень файла)";

    private readonly MainViewModel _main;
    private bool _loading;

    public DocumentViewModel(MainViewModel main) => _main = main;

    private AppSession Session => _main.Session;
    private WorkflowController Flow => _main.Flow;

    [ObservableProperty]
    private DocumentSummaryDto? _document;

    [ObservableProperty]
    private VersionRow? _selectedVersion;

    [ObservableProperty]
    private string? _selectedConfiguration;

    [ObservableProperty]
    private int _selectedSection;

    [ObservableProperty]
    private string _whereUsedText = string.Empty;

    public ObservableCollection<VersionRow> Versions { get; } = new();
    public ObservableCollection<string> Configurations { get; } = new();
    public ObservableCollection<PropertyRow> Properties { get; } = new();
    public ObservableCollection<ReferenceRow> References { get; } = new();
    public ObservableCollection<WhereUsedRow> WhereUsed { get; } = new();
    public ObservableCollection<DerivedRow> Derived { get; } = new();
    public ObservableCollection<JournalRow> Journal { get; } = new();

    public bool HasDocument => Document != null;
    public bool CanModify => Session.CanModify;

    public string HeaderText => Document == null ? "Документ не выбран." :
        (Document.Designation is { Length: > 0 } d ? d + " — " : string.Empty) + (Document.Name ?? Document.FileName);

    public string InfoText => Document == null ? string.Empty :
        $"Файл: {Document.RelPath}. Проект: {Document.ProjectName}{(Document.ProjectKind == ProjectKind.Library ? " (библиотека)" : string.Empty)}. " +
        $"Вид: {Format.Kind(Document.Kind)}, CAD: {Document.Cad}. Последняя версия: v{Document.LatestVersion}. " +
        $"Блокировка: {Format.Lock(Document.Lock, Session.Me.Id)}." + (Document.Status == DocumentStatus.Unused ? " Помечен «не используется»." : string.Empty);

    public string UnusedCommandText => Document?.Status == DocumentStatus.Unused ? "Вернуть в работу" : "Не используется";

    public async Task LoadAsync(Guid documentId, int? version, DocumentSection section)
    {
        var api = Session.Api;
        var outcome = await Flow.Busy.RunAsync("Загрузка документа", async (_, ct) =>
        {
            var detail = await api.GetDocumentAsync(documentId, ct);
            List<JournalEntryDto> journal;
            try
            {
                journal = await api.GetDocumentJournalAsync(documentId, ct);
            }
            catch (Core.Api.PdmApiException ex) when (!ex.IsNetworkError)
            {
                journal = new List<JournalEntryDto>();
            }
            return (detail, journal);
        });
        if (!outcome.Ok) return;
        var (detail, journalEntries) = outcome.Value;
        _loading = true;
        Document = detail.Document;
        Versions.ReplaceWith(detail.Versions.OrderByDescending(v => v.Number).Select(v => new VersionRow(v, detail.Document.LatestVersion)));
        Journal.ReplaceWith(journalEntries.OrderByDescending(j => j.At).Select(j => new JournalRow(j, Versions)));
        SelectedVersion = Versions.FirstOrDefault(v => v.Number == (version ?? detail.Document.LatestVersion)) ?? Versions.FirstOrDefault();
        _loading = false;
        SelectedSection = (int)section;
        OnPropertyChanged(nameof(HasDocument));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(InfoText));
        OnPropertyChanged(nameof(UnusedCommandText));
        await ShowVersionAsync();
    }

    partial void OnSelectedVersionChanged(VersionRow? value)
    {
        if (!_loading) _ = ShowVersionAsync();
    }

    partial void OnSelectedConfigurationChanged(string? value) => FillProperties();

    private async Task ShowVersionAsync()
    {
        var v = SelectedVersion?.Version;
        if (v == null || Document == null)
        {
            Configurations.Clear();
            Properties.Clear();
            References.Clear();
            Derived.Clear();
            WhereUsed.Clear();
            return;
        }
        var configs = new List<string>();
        if (v.Properties.ContainsKey(string.Empty) || v.Configurations.Count == 0) configs.Add(FileLevel);
        configs.AddRange(v.Configurations);
        foreach (var k in v.Properties.Keys.Where(k => k.Length > 0 && !configs.Contains(k))) configs.Add(k);
        Configurations.ReplaceWith(configs);
        SelectedConfiguration = configs.FirstOrDefault();
        FillProperties();
        References.ReplaceWith(v.References.OrderBy(r => r.RefPath, StringComparer.OrdinalIgnoreCase).Select(r => new ReferenceRow(r)));
        Derived.ReplaceWith(v.Derived.Select(d => new DerivedRow(d)));

        var ws = Session.Workspace;
        var id = Document.Id;
        var number = v.Number;
        var outcome = await Flow.Busy.RunAsync("«Где применяется»", (_, ct) => ws.GetWhereUsedAsync(id, number, ct));
        if (!outcome.Ok) return;
        var view = outcome.Value!;
        WhereUsed.ReplaceWith(view.ThisVersion.Select(i => new WhereUsedRow(i, true)).Concat(view.OtherVersions.Select(i => new WhereUsedRow(i, false))));
        var notLatest = view.NotLatest.Count();
        WhereUsedText = WhereUsed.Count == 0
            ? $"Версия v{number} нигде не применяется (нет сборок, чертежей и спецификаций, которые на неё ссылаются)."
            : $"Применений v{number}: {view.ThisVersion.Count}; других версий: {view.OtherVersions.Count}. Используют не последнюю версию: {notLatest}.";
    }

    private void FillProperties()
    {
        var v = SelectedVersion?.Version;
        Properties.Clear();
        if (v == null || SelectedConfiguration == null) return;
        var key = SelectedConfiguration == FileLevel ? string.Empty : SelectedConfiguration;
        if (v.Properties.TryGetValue(key, out var props))
        {
            foreach (var p in props.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
                Properties.Add(new PropertyRow(p.Key, p.Value));
        }
    }

    [RelayCommand]
    private Task OpenAsync() => Document == null ? Task.CompletedTask : Flow.OpenAsync(Document.Id, SelectedVersion?.Number, Format.Doc(Document.RelPath, Document.Designation));

    [RelayCommand]
    private Task ShowKitAsync() => Document == null ? Task.CompletedTask : _main.ShowKitAsync(Document.Id, SelectedVersion?.Number);

    [RelayCommand]
    private void Export()
    {
        if (Document != null) Flow.Export(Document.Id, SelectedVersion?.Number);
    }

    [RelayCommand]
    private async Task MarkUnusedAsync()
    {
        if (Document == null) return;
        if (await Flow.MarkUnusedAsync(Document.Id, Document.Status != DocumentStatus.Unused, Format.Doc(Document.RelPath, Document.Designation)))
            await LoadAsync(Document.Id, SelectedVersion?.Number, (DocumentSection)SelectedSection);
    }

    [RelayCommand]
    private Task RefreshAsync() => Document == null ? Task.CompletedTask : LoadAsync(Document.Id, SelectedVersion?.Number, (DocumentSection)SelectedSection);

    [RelayCommand]
    private Task GoToAsync(object? row) => row switch
    {
        ReferenceRow { ChildDocumentId: { } id } r => LoadAsync(id, r.ChildVersion, DocumentSection.Properties),
        WhereUsedRow w => LoadAsync(w.ParentDocumentId, w.ParentVersion, DocumentSection.Properties),
        _ => Task.CompletedTask,
    };
}

public sealed class VersionRow
{
    public VersionRow(VersionDto v, int latest)
    {
        Version = v;
        IsLatest = v.Number == latest;
    }

    public VersionDto Version { get; }
    public int Number => Version.Number;
    public bool IsLatest { get; }
    public string Text => $"v{Version.Number}" + (IsLatest ? " (последняя)" : string.Empty);
    public string Author => string.IsNullOrWhiteSpace(Version.AuthorDisplayName) ? Version.AuthorLogin : Version.AuthorDisplayName;
    public string DateText => Format.Time(Version.At);
    public string Comment => Version.Comment ?? string.Empty;
    public string? CadVersion => Version.CadVersion;
    public string RelPath => Version.RelPath;
    public string SizeText => Format.Bytes(Version.Size);
    public string Configurations => string.Join(", ", Version.Configurations);
    public string Sha => Version.Sha256.Length > 12 ? Version.Sha256[..12] + "…" : Version.Sha256;
    public override string ToString() => Text;
}

public sealed record PropertyRow(string Name, string Value);

public sealed class ReferenceRow
{
    public ReferenceRow(VersionRefDto r)
    {
        RefPath = r.RefPath;
        ChildDocumentId = r.ChildDocumentId;
        ChildVersion = r.ChildVersion;
        ChildRelPath = r.ChildRelPath ?? "— не учтён в PDM (отсутствующая ссылка)";
        VersionText = r.ChildVersion is { } v ? (r.ChildLatestVersion is { } l && l != v ? $"v{v} (есть v{l})" : $"v{v}") : string.Empty;
        Mark = r.ChildDocumentId == null ? StateMark.Missing : r.ChildLatestVersion is { } l2 && l2 != r.ChildVersion ? StateMark.Outdated : StateMark.Free;
    }

    public string RefPath { get; }
    public Guid? ChildDocumentId { get; }
    public int? ChildVersion { get; }
    public string ChildRelPath { get; }
    public string VersionText { get; }
    public StateMark Mark { get; }
}

public sealed class WhereUsedRow
{
    public WhereUsedRow(WhereUsedItemDto i, bool thisVersion)
    {
        Item = i;
        IsThisVersion = thisVersion;
    }

    public WhereUsedItemDto Item { get; }
    public bool IsThisVersion { get; }
    public Guid ParentDocumentId => Item.ParentDocumentId;
    public int ParentVersion => Item.ParentVersion;
    public string ParentRelPath => Item.ParentRelPath;
    public string? ParentDesignation => Item.ParentDesignation;
    public string? ParentName => Item.ParentName;
    public string ParentVersionText => $"v{Item.ParentVersion}" + (Item.IsParentLatest ? " (последняя)" : $" (последняя v{Item.ParentLatestVersion})");
    public string ChildVersionText => $"v{Item.ChildVersion}";
    public bool UsesNotLatest => !Item.UsesLatestChild;
    public string NoteText => (IsThisVersion ? "эта версия" : "другая версия") + (Item.UsesLatestChild ? ", последняя" : ", НЕ последняя версия документа");
    public StateMark Mark => Item.UsesLatestChild ? StateMark.Free : StateMark.Outdated;
}

public sealed class DerivedRow
{
    public DerivedRow(DerivedFileDto d)
    {
        FileName = d.FileName;
        SizeText = Format.Bytes(d.Size);
        Sha = d.Sha256;
    }

    public string FileName { get; }
    public string SizeText { get; }
    public string Sha { get; }
}

public sealed class JournalRow
{
    public JournalRow(JournalEntryDto e, IEnumerable<VersionRow>? versions = null)
    {
        Entry = e;
        DateText = Format.Time(e.At);
        User = e.UserLogin;
        ActionText = ActionName(e.Action);
        VersionText = e.Version is { } v ? $"v{v}" : string.Empty;
        DetailsText = DescribeDetails(e.Details);
    }

    public JournalEntryDto Entry { get; }
    public string DateText { get; }
    public string User { get; }
    public string ActionText { get; }
    public string VersionText { get; }
    public string DetailsText { get; }
    public string DocumentText => Entry.DocumentId?.ToString() ?? string.Empty;

    public static string ActionName(string action) => action switch
    {
        JournalActions.Login => "вход",
        JournalActions.Checkin => "сдача",
        JournalActions.Lock => "взять в работу",
        JournalActions.Unlock => "отменить взятие",
        JournalActions.ForceUnlock => "снятие блокировки администратором",
        JournalActions.MarkUnused => "не используется",
        JournalActions.Export => "выгрузка комплекта",
        JournalActions.UserCreated => "создан пользователь",
        JournalActions.UserUpdated => "изменён пользователь",
        JournalActions.SettingsChanged => "изменены настройки",
        JournalActions.ProjectCreated => "создан проект",
        _ => action,
    };

    private static string DescribeDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(details);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return details;
            var parts = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
                if (!string.IsNullOrEmpty(value)) parts.Add($"{p.Name}: {value}");
            }
            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return details;
        }
    }
}
