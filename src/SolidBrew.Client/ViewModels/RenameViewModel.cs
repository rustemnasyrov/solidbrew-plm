using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>«Переименовать / переместить» (7.9): новое имя, кто ссылается и кто их держит, одна сдача.</summary>
public sealed partial class RenameViewModel : DialogViewModel
{
    private readonly WorkspaceService _workspace;

    public RenameViewModel(WorkspaceService workspace, IDialogService dialogs, string workRoot, string relPath) : base(dialogs)
    {
        _workspace = workspace;
        WorkRoot = workRoot;
        OldRelPath = relPath;
        _newRelPath = relPath;
    }

    public override string Title => "Переименовать";

    public string WorkRoot { get; }
    public string OldRelPath { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(RenameCommand))]
    private string _newRelPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private RenamePlan? _plan;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    private string _summary = "Укажите новый путь относительно рабочей папки (можно сменить подпапку) и нажмите «Проверить».";

    public ObservableCollection<RenameParentRow> Parents { get; } = new();
    public ObservableCollection<ProblemItem> Problems { get; } = new();

    public RenameResult? Result { get; private set; }

    private string Normalized => NewRelPath.Trim().Replace('\\', '/');

    private bool CanCheck() => !Busy.IsBusy && !string.IsNullOrWhiteSpace(NewRelPath) &&
                               !string.Equals(Normalized, OldRelPath, StringComparison.Ordinal);

    partial void OnNewRelPathChanged(string value) => Plan = null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync()
    {
        var target = Normalized;
        var outcome = await Busy.RunAsync("Проверка переименования", (_, ct) => _workspace.PrepareRenameAsync(WorkRoot, OldRelPath, target, ct));
        if (!outcome.Ok) return;
        var plan = outcome.Value!;
        Parents.ReplaceWith(plan.Parents.Select(p => new RenameParentRow(p)));
        Problems.ReplaceWith(ProblemItem.From(plan.Problems));
        Summary = plan.Summary;
        Plan = plan;
    }

    private bool CanRename() => !Busy.IsBusy && Plan != null && Plan.CanExecute;

    [RelayCommand(CanExecute = nameof(CanRename))]
    public async Task RenameAsync()
    {
        var plan = Plan!;
        var comment = string.IsNullOrWhiteSpace(Comment) ? $"Переименование «{plan.OldRelPath}» → «{plan.NewRelPath}»" : Comment.Trim();
        var outcome = await Busy.RunAsync("Переименование", (p, ct) => _workspace.ExecuteRenameAsync(plan, comment, p, ct));
        if (!outcome.Ok) return;
        var result = outcome.Value!;
        if (!result.Success)
        {
            Problems.ReplaceWith(ProblemItem.From(result.Problems));
            Dialogs.ShowError(Title, ErrorText.FromProblems(result.Message, result.Problems));
            return;
        }
        Result = result;
        Busy.Done(result.Message);
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}

public sealed class RenameParentRow
{
    public RenameParentRow(RenameParent p)
    {
        RelPath = p.RelPath;
        Designation = p.Designation;
        Name = p.Name;
        VersionText = $"v{p.LatestVersion}" + (p.LocalVersion is { } l && l != p.LatestVersion ? $" (в папке v{l})" : string.Empty);
        LockText = p.LockedByMe ? "взят мной" : p.LockedByOther ? $"взят: {p.LockInfo!.UserDisplayName} — операция невозможна" : "свободен, будет взят";
        Mark = p.LockedByOther ? StateMark.Other : p.LockedByMe ? StateMark.Mine : StateMark.Free;
    }

    public string RelPath { get; }
    public string? Designation { get; }
    public string? Name { get; }
    public string VersionText { get; }
    public string LockText { get; }
    public StateMark Mark { get; }
}
