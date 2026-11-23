using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Cad;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Открытие» (и «Обновить до последней»): план, конфликты, проверка в CAD, отчёт.</summary>
public sealed partial class OpenViewModel : DialogViewModel
{
    public delegate Task<OpenPlan> PlanFactory(string workRoot, IProgress<WorkProgress> progress, CancellationToken ct);

    private readonly WorkspaceService _workspace;
    private readonly IShellService _shell;
    private readonly PlanFactory _prepare;
    private readonly string _title;
    private bool _defaultsSet;

    public OpenViewModel(WorkspaceService workspace, IDialogService dialogs, IShellService shell, string workRoot, PlanFactory prepare, string title)
        : base(dialogs)
    {
        _workspace = workspace;
        _shell = shell;
        _prepare = prepare;
        _title = title;
        _workRoot = workRoot;
    }

    public override string Title => _title;

    [ObservableProperty]
    private string _workRoot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private OpenPlan? _plan;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>«Проверить в CAD» — открыть головной документ и проверить, откуда загружены компоненты.</summary>
    [ObservableProperty]
    private bool _verifyInCad;

    /// <summary>Открыть головной документ в CAD после раскладки (без проверки).</summary>
    [ObservableProperty]
    private bool _openHeadAfter;

    [ObservableProperty]
    private bool _isDone;

    [ObservableProperty]
    private OpenResult? _result;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public ObservableCollection<OpenItemRow> Items { get; } = new();
    public ObservableCollection<ConflictRow> Conflicts { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();
    public ObservableCollection<ProblemItem> ResultProblems { get; } = new();
    public ObservableCollection<LoadedComponentRow> CadComponents { get; } = new();

    public bool HasConflicts => Conflicts.Count > 0;
    public bool HasCadReport => CadComponents.Count > 0 || Result?.CadReport != null;
    public bool IsUpdate => Plan?.IsUpdate == true;

    public static IReadOnlyList<ResolutionOption> ResolutionOptions { get; } = new[]
    {
        new ResolutionOption(ConflictResolution.Replace, "Заменить"),
        new ResolutionOption(ConflictResolution.Keep, "Оставить"),
    };

    public override Task InitializeAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsDone = false;
        var root = WorkRoot;
        var outcome = await Busy.RunAsync("Подготовка открытия", (p, ct) => _prepare(root, p, ct));
        if (!outcome.Ok) return;
        var plan = outcome.Value!;
        Plan = plan;
        if (!_defaultsSet)
        {
            // «Проверить в CAD» по умолчанию включено для SOLIDWORKS.
            VerifyInCad = string.Equals(plan.HeadCad, "solidworks", StringComparison.OrdinalIgnoreCase) && !plan.IsUpdate;
            _defaultsSet = true;
        }
        Items.ReplaceWith(plan.Items.OrderBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase).Select(i => new OpenItemRow(i)));
        Conflicts.ReplaceWith(plan.Items.SelectMany(i => i.Conflicts.Select(c => new ConflictRow(c, i, OnResolutionChanged))));
        Problems.ReplaceWith(ProblemItem.From(plan.Problems));
        Summary = plan.Summary;
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(IsUpdate));
        Busy.Done(plan.CanExecute ? "План открытия готов." : "Открыть нельзя: есть ошибки.");
    }

    private void OnResolutionChanged()
    {
        if (Plan == null) return;
        Summary = Plan.Summary;
        foreach (var i in Items) i.Refresh();
    }

    /// <summary>Выбрать другую рабочую папку и подготовить план заново.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeFolder))]
    private async Task ChangeFolderAsync()
    {
        var folder = Dialogs.PickFolder("Рабочая папка", Directory.Exists(WorkRoot) ? WorkRoot : null);
        if (folder == null) return;
        WorkRoot = folder;
        await LoadAsync();
    }

    private bool CanChangeFolder() => !Busy.IsBusy && !IsDone && Plan?.IsUpdate != true;

    private bool CanOpen() => !Busy.IsBusy && !IsDone && Plan != null && Plan.CanExecute;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    public async Task OpenAsync()
    {
        var plan = Plan!;
        var losing = plan.Conflicts.Where(c => c.Kind is OpenConflictKind.LocalChanges or OpenConflictKind.UnknownFile && c.Resolution == ConflictResolution.Replace).ToList();
        if (losing.Count > 0 && !Dialogs.Confirm("Замена файлов с правками",
                $"Будут заменены файлы с несданными правками или неучтённые файлы ({losing.Count}). Правки в них будут потеряны. Продолжить?",
                "Заменить", "Отмена", losing.Select(c => c.RelPath)))
            return;

        var verify = VerifyInCad;
        var outcome = await Busy.RunAsync(plan.IsUpdate ? "Обновление" : "Открытие", (p, ct) => _workspace.ExecuteOpenAsync(plan, verify, p, ct));
        if (!outcome.Ok) return;
        var result = outcome.Value!;
        Result = result;
        ResultProblems.ReplaceWith(ProblemItem.From(result.Problems));
        CadComponents.Clear();
        if (result.CadReport is { } report)
        {
            foreach (var c in report.Configurations)
            {
                foreach (var comp in c.Components)
                    CadComponents.Add(new LoadedComponentRow(c.Name, comp));
            }
            foreach (var p in report.Problems.Where(p => ResultProblems.All(r => r.Message != p)))
                ResultProblems.Add(new ProblemItem(ProblemSeverity.Warning, p));
        }
        ResultText = result.Message + (result.CadReport is { } r2
            ? r2.Ok ? " Проверка в CAD: все компоненты загружены из рабочей папки в нужных версиях." : " Проверка в CAD: есть замечания — см. список."
            : string.Empty);
        OnPropertyChanged(nameof(HasCadReport));
        IsDone = result.Success;
        Busy.Done(result.Message);
        if (!result.Success)
        {
            Dialogs.ShowError(Title, ErrorText.FromProblems(result.Message, result.Problems));
            return;
        }
        if (OpenHeadAfter && !verify && File.Exists(result.HeadFullPath))
        {
            try
            {
                _shell.OpenFile(result.HeadFullPath);
            }
            catch (Exception ex)
            {
                Dialogs.ShowError("Открыть в CAD", ErrorText.Describe(ex));
            }
        }
        this.NotifyCommands();
    }

    [RelayCommand]
    private void ShowFolder()
    {
        var path = Result?.HeadFullPath is { Length: > 0 } h && File.Exists(h) ? h : WorkRoot;
        try
        {
            _shell.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Показать в проводнике", ErrorText.Describe(ex));
        }
    }

    [RelayCommand]
    private void CloseWindow() => Close(IsDone);

    partial void OnIsDoneChanged(bool value) => this.NotifyCommands();
}

public sealed record ResolutionOption(ConflictResolution Value, string Text);

public sealed partial class OpenItemRow : ObservableObject
{
    private readonly OpenPlanItem _item;

    public OpenItemRow(OpenPlanItem item) => _item = item;

    public OpenPlanItem Item => _item;
    public string RelPath => _item.RelPath;
    public string FileName => _item.FileName;
    public string? Designation => _item.Designation;
    public string? Name => _item.Name;
    public string VersionText => _item.Version == _item.LatestVersion ? $"v{_item.Version}" : $"v{_item.Version} (последняя v{_item.LatestVersion})";
    public string LocalText => _item.LocalVersion is { } v ? $"v{v}" : string.Empty;
    public string? CadVersion => _item.CadVersion;
    public string CacheText => _item.InCache ? "в кэше" : Format.Bytes(_item.Size);
    public string LockText => _item.LockedByMe ? "взят мной" : _item.LockInfo != null ? $"взят: {_item.LockInfo.UserDisplayName}" : string.Empty;

    public string ActionText => _item.Action switch
    {
        OpenAction.Place => "разложить",
        OpenAction.AlreadyInPlace => "уже на месте",
        OpenAction.Adopt => "учесть лежащий файл",
        _ => _item.WillWrite ? "заменить" : "оставить как есть",
    };

    public StateMark Mark => _item.Action switch
    {
        OpenAction.AlreadyInPlace or OpenAction.Adopt => StateMark.Free,
        OpenAction.Place => StateMark.None,
        _ => _item.WillWrite ? StateMark.Outdated : StateMark.Modified,
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(Mark));
    }
}

public sealed partial class ConflictRow : ObservableObject
{
    private readonly OpenConflict _conflict;
    private readonly Action _changed;

    public ConflictRow(OpenConflict conflict, OpenPlanItem item, Action changed)
    {
        _conflict = conflict;
        _changed = changed;
        Item = item;
    }

    public OpenConflict Conflict => _conflict;
    public OpenPlanItem Item { get; }
    public string RelPath => _conflict.RelPath;
    public string Message => _conflict.Message;
    public bool HasLocalChanges => _conflict.Kind is OpenConflictKind.LocalChanges or OpenConflictKind.UnknownFile;

    public string KindText => _conflict.Kind switch
    {
        OpenConflictKind.OtherVersion => $"другая версия (в папке v{_conflict.LocalVersion}, нужна v{_conflict.NewVersion})",
        OpenConflictKind.OtherDocument => "другой документ по этому пути",
        OpenConflictKind.LocalChanges => "несданные правки",
        OpenConflictKind.UnknownFile => "неучтённый файл",
        OpenConflictKind.MovedDocument => $"документ лежит под другим путём ({_conflict.OldRelPath})",
        _ => string.Empty,
    };

    public ConflictResolution Resolution
    {
        get => _conflict.Resolution;
        set
        {
            if (_conflict.Resolution == value) return;
            _conflict.Resolution = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReplace));
            _changed();
        }
    }

    public bool IsReplace
    {
        get => Resolution == ConflictResolution.Replace;
        set => Resolution = value ? ConflictResolution.Replace : ConflictResolution.Keep;
    }
}

public sealed class LoadedComponentRow
{
    public LoadedComponentRow(string configuration, LoadedComponent c)
    {
        Configuration = configuration;
        Name = c.Name;
        LoadedPath = c.LoadedPath;
        InsideWorkRoot = c.InsideWorkRoot;
    }

    public string Configuration { get; }
    public string Name { get; }
    public string LoadedPath { get; }
    public bool InsideWorkRoot { get; }
    public string WhereText => InsideWorkRoot ? "из рабочей папки" : "НЕ из рабочей папки";
}
