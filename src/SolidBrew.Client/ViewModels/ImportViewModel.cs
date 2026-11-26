using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Экран «Постановка на учёт» (6.1 п. 6, 7.1): головной файл, доп. папки поиска, отчёт по категориям ссылок,
/// решения (подтвердить / выбрать вариант), проект, комментарий.
/// </summary>
/// <summary>
/// Постановка файла, оставленного ссылкой: головной файл задан, новые документы кладутся в папку проекта
/// <see cref="RelPathPrefix"/>, уже учтённое содержимое берётся из хранилища. <see cref="HeadSavedPath"/> — путь из ссылки
/// родителя (где файл лежал раньше): если папку переименовали, ссылки файла ищутся на прежнем месте относительно него.
/// </summary>
public sealed record ImportPreset(string HeadFile, string RelPathPrefix, string ParentLabel, string? HeadSavedPath = null);

public sealed partial class ImportViewModel : DialogViewModel
{
    private readonly AppSession _session;
    private readonly Guid? _preferredProject;
    private readonly ImportPreset? _preset;
    private readonly Dictionary<ImportReference, ImportReferenceRow> _refRows = new();
    private readonly Dictionary<ImportDocument, ImportDocumentRow> _docRows = new();
    private bool _executed;

    public ImportViewModel(AppSession session, Guid? preferredProject = null, ImportPreset? preset = null, string? headFile = null) : base(session.Services.Dialogs)
    {
        _session = session;
        _preferredProject = preferredProject;
        _preset = preset;
        if (preset != null) HeadFile = preset.HeadFile;
        else if (!string.IsNullOrWhiteSpace(headFile)) HeadFile = headFile;
        _analyzeOnStart = preset != null || !string.IsNullOrWhiteSpace(headFile);
    }

    private readonly bool _analyzeOnStart;

    public override string Title => _preset == null ? "Поставить на учёт" : $"Поставить на учёт и привязать к {_preset.ParentLabel}";

    private WorkspaceService Workspace => _session.Workspace;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private string _headFile = string.Empty;

    [ObservableProperty]
    private string? _selectedFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private ImportReport? _report;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private ProjectDto? _selectedProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    private bool _allowMissing;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>После постановки — открыть в рабочую папку.</summary>
    public bool OpenAfter { get; private set; }

    public ImportResult? Result { get; private set; }

    public ObservableCollection<string> ExtraFolders { get; } = new();
    public ObservableCollection<ProjectDto> Projects { get; } = new();
    public ObservableCollection<ImportDocumentRow> Documents { get; } = new();
    public ObservableCollection<ImportReferenceRow> Found { get; } = new();
    public ObservableCollection<ImportReferenceRow> Outside { get; } = new();
    public ObservableCollection<ImportReferenceRow> NameOnly { get; } = new();
    public ObservableCollection<ImportReferenceRow> Relocated { get; } = new();
    public ObservableCollection<ImportReferenceRow> Ambiguous { get; } = new();
    public ObservableCollection<ImportReferenceRow> Missing { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();

    public string FoundHeader => $"Найдены ({Found.Count})";
    public string OutsideHeader => $"За пределами папок ({Outside.Count})";
    public string NameOnlyHeader => $"Совпали только по имени ({NameOnly.Count})";
    public string RelocatedHeader => $"Папка перенесена ({Relocated.Count})";
    public string AmbiguousHeader => $"Неоднозначно ({Ambiguous.Count})";
    public string MissingHeader => $"Отсутствуют ({Missing.Count})";
    public string DocumentsHeader => Report == null ? "Документы" :
        $"Документы (загрузить {Report.NewDocuments.Count()}, из хранилища {Report.FromStorageDocuments.Count()}, по ссылке {Report.ByLinkDocuments.Count()})";

    public override async Task InitializeAsync()
    {
        var outcome = await Busy.RunAsync("Загрузка проектов", (_, ct) => _session.Api.GetProjectsAsync(ct));
        if (!outcome.Ok) return;
        var projects = outcome.Value!.Where(p => p.Kind == ProjectKind.Project || _session.IsAdmin)
            .OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Projects.ReplaceWith(projects);
        SelectedProject = projects.FirstOrDefault(p => p.Id == _preferredProject) ?? (projects.Count == 1 ? projects[0] : null);
        if (_analyzeOnStart)
        {
            // Файл задан — сразу отчёт (для файла «по ссылке» документы ложатся рядом с родителем).
            await AnalyzeAsync();
            return;
        }
        Busy.Done("Выберите головной документ и, при необходимости, папки с чертежами.");
    }

    [RelayCommand]
    private void BrowseHead()
    {
        var exts = Workspace.Adapters.All.SelectMany(a => a.Extensions).Select(e => "*" + e).ToList();
        var filter = $"Документы CAD ({string.Join(", ", exts)})|{string.Join(";", exts)}|Все файлы (*.*)|*.*";
        var file = Dialogs.PickFile("Головной документ", filter, string.IsNullOrEmpty(HeadFile) ? null : Path.GetDirectoryName(HeadFile));
        if (file != null) HeadFile = file;
    }

    [RelayCommand]
    private void AddFolder()
    {
        var start = string.IsNullOrEmpty(HeadFile) ? null : Path.GetDirectoryName(HeadFile);
        var folder = Dialogs.PickFolder("Дополнительная папка поиска (чертежи, спецификации)", start);
        if (folder != null && !ExtraFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) ExtraFolders.Add(folder);
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder != null) ExtraFolders.Remove(SelectedFolder);
    }

    private bool CanAnalyze() => !Busy.IsBusy && !string.IsNullOrWhiteSpace(HeadFile);

    /// <summary>Прочитать исходники и построить отчёт по ссылкам. Исходники не меняются.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    public async Task AnalyzeAsync()
    {
        var head = HeadFile.Trim();
        var folders = ExtraFolders.ToList();
        var options = _preset == null ? null : new ImportOptions
        {
            RelPathPrefix = _preset.RelPathPrefix, AllowExistingHead = true,
            // Выбран другой файл, чем по ссылке («Выбрать файл и сдать…»), — его прежнего места мы не знаем.
            HeadSavedPath = string.Equals(head, _preset.HeadFile, StringComparison.OrdinalIgnoreCase) ? _preset.HeadSavedPath : null,
        };
        var outcome = await Busy.RunAsync("Чтение исходников", (p, ct) => Workspace.PrepareImportAsync(head, folders, p, ct, options));
        if (!outcome.Ok) return;
        Report?.Dispose();
        _refRows.Clear();
        _docRows.Clear();
        Report = outcome.Value;
        Rebuild();
        Busy.Done(Summary);
    }

    private void OnDecisionChanged()
    {
        Report?.Recompute();
        Rebuild();
    }

    private void Rebuild()
    {
        var report = Report;
        if (report == null) return;
        ImportReferenceRow Row(ImportReference r)
        {
            if (!_refRows.TryGetValue(r, out var row))
            {
                var doc = report.Documents.FirstOrDefault(d => d.References.Contains(r));
                _refRows[r] = row = new ImportReferenceRow(r, doc, OnDecisionChanged);
            }
            row.Refresh();
            return row;
        }
        var refs = report.References.ToList();
        Found.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.Found).Select(Row));
        Outside.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.OutsideFolders).Select(Row));
        NameOnly.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.NameOnly).Select(Row));
        Relocated.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.Relocated).Select(Row));
        Ambiguous.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.Ambiguous).Select(Row));
        Missing.ReplaceWith(refs.Where(r => r.Category == ImportReferenceCategory.Missing).Select(Row));

        ImportDocumentRow DocRow(ImportDocument d)
        {
            if (!_docRows.TryGetValue(d, out var row)) _docRows[d] = row = new ImportDocumentRow(d, OnDecisionChanged);
            row.Refresh();
            return row;
        }
        // Порядок строк постоянный (по пути исходника), чтобы галочка не «прыгала» после щелчка.
        Documents.ReplaceWith(report.Documents.Where(d => d.Disposition != ImportDisposition.NotUsed || d.IsExtra)
            .OrderByDescending(d => d.IsHead).ThenBy(d => d.IsExtra).ThenBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(DocRow));
        Problems.ReplaceWith(ProblemItem.From(report.Problems));

        Summary = $"Загрузить в PDM: {report.NewDocuments.Count()}, взять из хранилища: {report.FromStorageDocuments.Count()}, оставить ссылкой: {report.ByLinkDocuments.Count()}. " +
                  $"Ссылки: найдены {Found.Count}, за пределами папок {Outside.Count}, только по имени {NameOnly.Count}, " +
                  (Relocated.Count > 0 ? $"папка перенесена {Relocated.Count}, " : string.Empty) +
                  $"неоднозначно {Ambiguous.Count}, отсутствуют {Missing.Count}." +
                  (report.CommonRoot != null ? $" Общая папка: {report.CommonRoot}." : string.Empty);
        foreach (var name in new[] { nameof(FoundHeader), nameof(OutsideHeader), nameof(NameOnlyHeader), nameof(RelocatedHeader), nameof(AmbiguousHeader), nameof(MissingHeader), nameof(DocumentsHeader) })
            OnPropertyChanged(name);
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanImport() => !Busy.IsBusy && !_executed && Report != null && SelectedProject != null && Report.CanExecute(AllowMissing);

    [RelayCommand(CanExecute = nameof(CanImport))]
    public async Task ImportAsync()
    {
        var report = Report!;
        var project = SelectedProject!;
        report.Recompute();
        if (!report.CanExecute(AllowMissing))
        {
            Rebuild();
            return;
        }
        if (AllowMissing && report.HasMissing && !Dialogs.Confirm("Отсутствующие ссылки",
                "В комплекте есть отсутствующие или неподтверждённые ссылки. Документы будут поставлены на учёт с этими ссылками, CAD не найдёт такие компоненты. Продолжить?",
                "Поставить на учёт", "Отмена", report.Problems.Where(p => p.Code is LocalProblemCodes.MissingReference or LocalProblemCodes.Unconfirmed).Select(p => p.Message)))
            return;
        var acceptNewer = false;
        if (report.HasNewerCadVersion)
        {
            if (!Dialogs.Confirm("Версия CAD новее рабочей",
                    "Некоторые файлы сохранены в версии CAD новее рабочей версии предприятия. Коллеги с рабочей версией не смогут их открыть. Всё равно поставить на учёт?",
                    "Поставить на учёт", "Отмена",
                    report.NewDocuments.Where(d => d.IsNewerCadVersion).Select(d => $"«{d.FileName}» — {d.CadVersion}")))
                return;
            acceptNewer = true;
        }
        var comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();
        var allowMissing = AllowMissing;
        var outcome = await Busy.RunAsync("Постановка на учёт", (p, ct) => Workspace.ExecuteImportAsync(report, project.Id, comment, allowMissing, acceptNewer, p, ct));
        if (!outcome.Ok) return;
        var result = outcome.Value!;
        if (!result.Success)
        {
            Problems.ReplaceWith(ProblemItem.From(report.Problems.Concat(result.Problems)));
            Dialogs.ShowError(Title, ErrorText.FromProblems(result.Message, result.Problems));
            return;
        }
        _executed = true;
        Result = result;
        Busy.Done(result.Message);
        if (_preset != null)
        {
            Close(true); // дальше привязка ссылки родителя — её делает вызывающий
            return;
        }
        OpenAfter = Dialogs.Confirm("Поставлено на учёт",
            $"{result.Message}\nОткрыть его как изделие (v{result.HeadVersion}, с чертежами)?", "Открыть", "Не сейчас");
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(_executed);

    public override void OnClosed()
    {
        if (!_executed) Report?.Dispose();
    }
}

public sealed partial class ImportReferenceRow : ObservableObject
{
    private readonly Action _changed;

    public ImportReferenceRow(ImportReference reference, ImportDocument? source, Action changed)
    {
        Reference = reference;
        Source = source;
        _changed = changed;
    }

    public ImportReference Reference { get; }
    public ImportDocument? Source { get; }
    public string SourceName => Source?.FileName ?? Path.GetFileName(Reference.SourceDocument);
    public string FileName => Reference.FileName;
    public string SavedPath => Reference.SavedPath;
    public string? TargetPath => Reference.TargetPath;
    public string CategoryText => Reference.CategoryText;
    /// <summary>Папка перенесена: «было → стало» для ссылающегося документа.</summary>
    public string? MovedText => Reference.MovedFrom == null ? null : $"{Reference.MovedFrom} → {Reference.MovedTo}";
    public IReadOnlyList<string> Candidates => Reference.Candidates;

    /// <summary>Подтверждение (за пределами папок, только по имени).</summary>
    public bool Accept
    {
        get => Reference.Accept;
        set
        {
            if (Reference.Accept == value) return;
            Reference.Accept = value;
            OnPropertyChanged();
            _changed();
        }
    }

    /// <summary>Выбранный вариант (неоднозначная ссылка).</summary>
    public string? Chosen
    {
        get => Reference.Chosen;
        set
        {
            if (string.Equals(Reference.Chosen, value, StringComparison.OrdinalIgnoreCase)) return;
            Reference.Chosen = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public string EffectiveText => Reference.EffectiveTarget ?? "— ссылка останется отсутствующей";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Accept));
        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(EffectiveText));
    }
}

public sealed partial class ImportDocumentRow : ObservableObject
{
    private readonly Action _changed;

    public ImportDocumentRow(ImportDocument document, Action changed)
    {
        Document = document;
        _changed = changed;
    }

    public ImportDocument Document { get; }
    public string FileName => Document.FileName;
    public string RelPath => Document.RelPath.Length > 0 ? Document.RelPath : Document.SourcePath;
    public string SourcePath => Document.SourcePath;
    public string? Designation => Document.Designation;
    public string? Name => Document.Name;
    public string KindText => Format.Kind(Document.Kind);
    public string? CadVersion => Document.CadVersion;
    public bool IsNewerCadVersion => Document.IsNewerCadVersion;
    public bool IsIncluded => Document.IsIncluded;
    public bool IsExtra => Document.IsExtra;
    public string? ReadError => Document.ReadError;
    /// <summary>Галочку можно снять у любого документа, кроме головного.</summary>
    public bool CanToggle => !Document.IsHead;

    public string RoleText => Document.IsHead ? "головной" : Document.IsExtra ? "чертёж из папок поиска" : "компонент";

    /// <summary>Что будет с документом.</summary>
    public string DispositionText => Document.Disposition switch
    {
        ImportDisposition.New => "загрузится в PDM",
        ImportDisposition.FromStorage => "уже в PDM, берётся из хранилища: " + ImportReport.Describe(Document.Existing!),
        ImportDisposition.ByLink => "не загружается, CAD откроет по ссылке",
        _ => Document.IsExtra && !Document.Include ? "не загружается" : "не используется",
    };

    /// <summary>Галочка «загружать». Без неё файл не загружается в PDM и остаётся ссылкой.</summary>
    public bool Include
    {
        get => Document.Include;
        set
        {
            if (Document.Include == value) return;
            Document.Include = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(RelPath));
        OnPropertyChanged(nameof(Include));
        OnPropertyChanged(nameof(DispositionText));
    }
}
