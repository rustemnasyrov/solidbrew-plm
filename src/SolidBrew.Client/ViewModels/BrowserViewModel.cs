using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Обзор хранилища» (6.1 п. 2, 7.8): проекты, библиотека, поиск, таблица документов.</summary>
public sealed partial class BrowserViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public BrowserViewModel(MainViewModel main) => _main = main;

    private AppSession Session => _main.Session;
    private WorkflowController Flow => _main.Flow;

    public ObservableCollection<ProjectItem> Projects { get; } = new();
    public ObservableCollection<DocumentRow> Documents { get; } = new();

    [ObservableProperty]
    private ProjectItem? _selectedProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ShowKitCommand), nameof(ShowDocumentCommand), nameof(ExportCommand), nameof(WhereUsedCommand), nameof(MarkUnusedCommand))]
    private DocumentRow? _selectedDocument;

    /// <summary>Обозначение, наименование или имя файла.</summary>
    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    /// <summary>«Показать неиспользуемые».</summary>
    [ObservableProperty]
    private bool _includeUnused;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public bool CanModify => Session.CanModify;

    public async Task LoadProjectsAsync()
    {
        var outcome = await Flow.Busy.RunAsync("Загрузка проектов", (_, ct) => Session.Api.GetProjectsAsync(ct));
        if (!outcome.Ok) return;
        var keep = SelectedProject?.Id;
        var items = new List<ProjectItem> { new(null, "Все документы", "Хранилище", null) };
        items.AddRange(outcome.Value!.Where(p => p.Kind == ProjectKind.Project).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProjectItem(p.Id, p.Name, "Проекты", p)));
        items.AddRange(outcome.Value!.Where(p => p.Kind == ProjectKind.Library).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProjectItem(p.Id, p.Name, "Библиотека", p)));
        Projects.ReplaceWith(items);
        _suppressSearch = true;
        SelectedProject = items.FirstOrDefault(i => i.Id == keep) ?? items[0];
        _suppressSearch = false;
        await SearchAsync();
    }

    private bool _suppressSearch;

    partial void OnSelectedProjectChanged(ProjectItem? value)
    {
        if (!_suppressSearch) _ = SearchAsync();
    }

    partial void OnIncludeUnusedChanged(bool value) => _ = SearchAsync();

    [RelayCommand]
    public async Task SearchAsync()
    {
        var q =string.IsNullOrWhiteSpace(Query) ? null : Query.Trim();
        var author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim();
        var project = SelectedProject?.Id;
        var unused = IncludeUnused;
        var outcome = await Flow.Busy.RunAsync("Поиск", (_, ct) => Session.Api.SearchDocumentsAsync(q, project, author, unused, 500, ct));
        if (!outcome.Ok) return;
        var keep = SelectedDocument?.Id;
        var me = Session.Me.Id;
        Documents.ReplaceWith(outcome.Value!.Select(d => new DocumentRow(d, me)));
        SelectedDocument = Documents.FirstOrDefault(d => d.Id == keep);
        ResultText = Documents.Count >= 500 ? "Показаны первые 500 документов — уточните поиск." : $"Найдено документов: {Documents.Count}";
        Flow.Busy.Done(ResultText);
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        Query = string.Empty;
        Author = string.Empty;
        await SearchAsync();
    }

    private bool HasSelection() => SelectedDocument != null;

    /// <summary>«Открыть» последнюю версию выбранного документа в рабочую папку.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenAsync() => Flow.OpenAsync(SelectedDocument!.Id, null, SelectedDocument.Label);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ShowKitAsync() => _main.ShowKitAsync(SelectedDocument!.Id, null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ShowDocumentAsync() => _main.ShowDocumentAsync(SelectedDocument!.Id, null, DocumentSection.Properties);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task WhereUsedAsync() => _main.ShowDocumentAsync(SelectedDocument!.Id, null, DocumentSection.WhereUsed);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export() => Flow.Export(SelectedDocument!.Id, null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MarkUnusedAsync() => Flow.MarkUnusedAsync(SelectedDocument!.Id, !SelectedDocument.IsUnused, SelectedDocument.Label);
}

public sealed record ProjectItem(Guid? Id, string Name, string Group, ProjectDto? Project)
{
    public string Icon => Group switch
    {
        "Библиотека" => "",
        "Проекты" => "",
        _ => "",
    };
}

/// <summary>Строка таблицы документов.</summary>
public sealed class DocumentRow
{
    public DocumentRow(DocumentSummaryDto d, Guid me)
    {
        Summary = d;
        LockText = Format.Lock(d.Lock, me);
        Mark = d.Status == DocumentStatus.Unused ? StateMark.None
            : d.Lock == null ? StateMark.Free : d.Lock.UserId == me ? StateMark.Mine : StateMark.Other;
    }

    public DocumentSummaryDto Summary { get; }
    public Guid Id => Summary.Id;
    public string? Designation => Summary.Designation;
    public string? Name => Summary.Name;
    public string FileName => Summary.FileName;
    public string RelPath => Summary.RelPath;
    public string ProjectName => Summary.ProjectName;
    public string KindText => Format.Kind(Summary.Kind);
    public string Cad => Summary.Cad;
    public int LatestVersion => Summary.LatestVersion;
    public string VersionText => $"v{Summary.LatestVersion}";
    public string Author => string.IsNullOrWhiteSpace(Summary.LatestAuthorDisplayName) ? Summary.LatestAuthorLogin : Summary.LatestAuthorDisplayName;
    public string DateText => Format.Time(Summary.LatestAt);
    public string LockText { get; }
    public bool IsUnused => Summary.Status == DocumentStatus.Unused;
    public string StatusText => IsUnused ? "не используется" : string.Empty;
    public StateMark Mark { get; }
    public string Label => Format.Doc(Summary.RelPath, Summary.Designation);
}
