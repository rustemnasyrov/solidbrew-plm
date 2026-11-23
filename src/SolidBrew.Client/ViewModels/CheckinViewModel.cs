using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Экран «Сдача» (6.1 п. 5, 7.4): кандидаты с отметками, причина изменения, проблемы, комментарий,
/// «Сдать» / «Сдать и оставить в работе». Сдача недоступна при блокирующих ошибках.
/// </summary>
public sealed partial class CheckinViewModel : DialogViewModel
{
    private readonly WorkspaceService _workspace;

    public CheckinViewModel(WorkspaceService workspace, IDialogService dialogs, string workRoot) : base(dialogs)
    {
        _workspace = workspace;
        WorkRoot = workRoot;
    }

    public override string Title => "Сдать";

    public string WorkRoot { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckinCommand), nameof(CheckinKeepLocksCommand))]
    private CheckinPlan? _plan;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _serverMessage;

    public ObservableCollection<CheckinCandidateRow> Items { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();

    /// <summary>Итог успешной сдачи.</summary>
    public CheckinResult? Result { get; private set; }

    public bool HasBlockingProblems => Problems.Any(p => p.IsBlocking);
    public bool IsEmpty => Plan != null && Items.Count == 0;

    public override Task InitializeAsync() => LoadAsync();

    [RelayCommand]
    public async Task LoadAsync()
    {
        var outcome = await Busy.RunAsync("Поиск изменённых и новых документов", (p, ct) => _workspace.PrepareCheckinAsync(WorkRoot, p, ct));
        if (!outcome.Ok) return;
        Plan = outcome.Value;
        ServerMessage = null;
        Items.ReplaceWith(Plan!.Items.OrderBy(i => !i.Include).ThenBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(i => new CheckinCandidateRow(i, OnIncludeChanged)));
        Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        Busy.Done(Items.Count == 0 ? "Нечего сдавать: нет изменённых взятых документов и черновиков." : Plan.Summary);
    }

    private void OnIncludeChanged()
    {
        Plan?.Revalidate();
        ServerMessage = null;
        Refresh();
    }

    private void Refresh()
    {
        if (Plan == null) return;
        var all = Plan.Problems.Concat(Plan.ServerProblems);
        Problems.ReplaceWith(ProblemItem.From(all));
        Summary = Plan.Summary;
        OnPropertyChanged(nameof(HasBlockingProblems));
        CheckinCommand.NotifyCanExecuteChanged();
        CheckinKeepLocksCommand.NotifyCanExecuteChanged();
    }

    private bool CanCheckin() => !Busy.IsBusy && Plan != null && Plan.CanExecute;

    /// <summary>«Сдать»: опубликовать и снять блокировки.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckin))]
    private Task CheckinAsync() => SubmitAsync(keepLocks: false);

    /// <summary>«Сдать и оставить в работе»: опубликовать и сохранить блокировки.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckin))]
    private Task CheckinKeepLocksAsync() => SubmitAsync(keepLocks: true);

    private async Task SubmitAsync(bool keepLocks)
    {
        var plan = Plan;
        if (plan == null || !plan.CanExecute) return;
        var acceptNewer = false;
        if (plan.HasNewerCadVersion)
        {
            var newer = plan.Included.Where(i => i.IsNewerCadVersion).Select(i => $"{Format.Doc(i.RelPath, i.Designation)} — {i.CadVersion}").ToList();
            if (!Dialogs.Confirm("Версия CAD новее рабочей",
                    "Некоторые файлы сохранены в версии CAD новее рабочей версии предприятия. Коллеги с рабочей версией не смогут их открыть. Всё равно сдать?",
                    "Сдать", "Отмена", newer))
                return;
            acceptNewer = true;
        }
        var comment = string.IsNullOrWhiteSpace(Comment) ? null : Comment.Trim();
        var outcome = await Busy.RunAsync("Сдача", (p, ct) => _workspace.ExecuteCheckinAsync(plan, comment, keepLocks, acceptNewer, p, ct));
        if (!outcome.Ok)
        {
            Refresh();
            return;
        }
        var result = outcome.Value!;
        if (result.Success)
        {
            Result = result;
            Busy.Done(result.Message);
            Close(true);
            return;
        }
        ServerMessage = result.Message;
        Problems.ReplaceWith(ProblemItem.From(plan.Problems.Concat(result.Problems)));
        OnPropertyChanged(nameof(HasBlockingProblems));
        Busy.Done("Сдача не выполнена: " + result.Message);
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var i in Items) i.SetIncludeSilently(value);
        OnIncludeChanged();
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}

public sealed partial class CheckinCandidateRow : ObservableObject
{
    private readonly Action _changed;

    public CheckinCandidateRow(CheckinCandidate candidate, Action changed)
    {
        Candidate = candidate;
        _changed = changed;
    }

    public CheckinCandidate Candidate { get; }

    public bool Include
    {
        get => Candidate.Include;
        set
        {
            if (Candidate.Include == value) return;
            Candidate.Include = value;
            OnPropertyChanged();
            _changed();
        }
    }

    internal void SetIncludeSilently(bool value)
    {
        if (Candidate.Include == value) return;
        Candidate.Include = value;
        OnPropertyChanged(nameof(Include));
    }

    public string RelPath => Candidate.RelPath;
    public string FileName => Candidate.FileName;
    public string? Designation => Candidate.Designation;
    public string? Name => Candidate.Name;
    public string KindText => Format.Kind(Candidate.Kind);
    public string BaseText => Candidate.IsDraft ? "новый" : $"v{Candidate.BaseVersion}";
    public string? CadVersion => Candidate.CadVersion;
    public bool IsNewerCadVersion => Candidate.IsNewerCadVersion;

    /// <summary>Причина: новый документ, изменено содержимое, изменены привязки.</summary>
    public string ChangeText
    {
        get
        {
            if (Candidate.IsDraft) return "новый документ";
            if (Candidate.ContentChanged && Candidate.ReferencesChanged) return "содержимое и привязки";
            if (Candidate.ContentChanged) return "содержимое";
            if (Candidate.ReferencesChanged) return "привязки";
            return "без изменений";
        }
    }

    public string Reason => Candidate.Reason;

    public StateMark Mark => Candidate.IsDraft ? StateMark.Draft : Candidate.IsChanged ? StateMark.Modified : StateMark.Mine;
}
