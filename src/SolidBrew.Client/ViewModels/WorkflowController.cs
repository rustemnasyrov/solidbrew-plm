using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Команды раздела 7 над текущей рабочей папкой: открыть, взять в работу, сдать, отменить взятие,
/// обновить до последней, поставить на учёт, выгрузить, переименовать. Общие для всех экранов главного окна.
/// </summary>
public sealed partial class WorkflowController : ObservableObject
{
    private readonly AppSession _session;

    public WorkflowController(AppSession session, BusyState busy)
    {
        _session = session;
        Busy = busy;
        foreach (var w in session.Profile.RecentWorkspaces) RecentWorkspaces.Add(w);
        var last = session.Profile.LastWorkspace;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) _workRoot = last;
    }

    public BusyState Busy { get; }
    private IDialogService Dialogs => _session.Services.Dialogs;
    private IShellService Shell => _session.Services.Shell;
    private WorkspaceService Workspace => _session.Workspace;

    /// <summary>Текущая рабочая папка.</summary>
    [ObservableProperty]
    private string? _workRoot;

    public ObservableCollection<string> RecentWorkspaces { get; } = new();

    /// <summary>Что-то изменилось в рабочей папке или на сервере — экраны обновляются.</summary>
    public event EventHandler? Changed;

    /// <summary>Сообщения для полосы уведомлений.</summary>
    public event EventHandler<IReadOnlyList<string>>? Notices;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------ рабочая папка

    public void SetWorkRoot(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Dialogs.ShowError("Рабочая папка", new ErrorInfo($"Неверный путь к папке: {path}."));
            return;
        }
        var changed = !string.Equals(WorkRoot, full, StringComparison.OrdinalIgnoreCase);
        WorkRoot = full;
        _session.Profile.RememberWorkspace(full);
        RecentWorkspaces.ReplaceWith(_session.Profile.RecentWorkspaces);
        try
        {
            _session.SaveProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (changed) RaiseChanged();
    }

    /// <summary>Выбрать рабочую папку.</summary>
    public bool ChooseWorkRoot(string? title = null)
    {
        var initial = WorkRoot != null && Directory.Exists(WorkRoot) ? WorkRoot : DefaultParent();
        var folder = Dialogs.PickFolder(title ?? "Рабочая папка", initial);
        if (folder == null) return false;
        SetWorkRoot(folder);
        return true;
    }

    private static string DefaultParent()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SolidBrew");
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        return root;
    }

    /// <summary>Текущая рабочая папка SolidBrew; если не задана — предложить выбрать.</summary>
    private string? RequireWorkspace(string action)
    {
        if (WorkRoot != null && WorkspaceService.IsWorkspace(WorkRoot)) return WorkRoot;
        var text = WorkRoot == null
            ? $"Для команды «{action}» нужна рабочая папка. Выберите папку, в которую открыт комплект."
            : $"Папка «{WorkRoot}» ещё не рабочая папка SolidBrew: в неё ничего не открывалось. Сначала откройте документ («Открыть») или выберите другую папку.";
        if (!Dialogs.Confirm(action, text, "Выбрать папку…", "Отмена")) return null;
        if (!ChooseWorkRoot()) return null;
        if (WorkspaceService.IsWorkspace(WorkRoot!)) return WorkRoot;
        Dialogs.ShowError(action, new ErrorInfo($"Папка «{WorkRoot}» не является рабочей папкой SolidBrew. Откройте в неё документ командой «Открыть»."));
        return null;
    }

    private bool EnsureCanModify(string action)
    {
        if (_session.CanModify) return true;
        Dialogs.ShowError(action, new ErrorInfo("Роль «читатель» позволяет только просматривать и выгружать комплекты."));
        return false;
    }

    // ------------------------------------------------------------------ открыть

    /// <summary>«Открыть»: выбранная версия комплекта в рабочую папку.</summary>
    public async Task<bool> OpenAsync(Guid documentId, int? version, string? label = null)
    {
        var root = WorkRoot;
        if (root == null)
        {
            if (!ChooseWorkRoot("Рабочая папка для открытия" + (label != null ? $" {label}" : string.Empty))) return false;
            root = WorkRoot!;
        }
        var ws = Workspace;
        var vm = new OpenViewModel(ws, Dialogs, Shell, root,
            (r, p, ct) => ws.PrepareOpenAsync(r, documentId, version, p, ct),
            "Открыть" + (label != null ? $" {label}" : string.Empty) + (version != null ? $" v{version}" : string.Empty));
        var result = Dialogs.ShowDialog(vm);
        await Task.CompletedTask;
        if (vm.IsDone)
        {
            SetWorkRoot(vm.WorkRoot);
            Busy.Done(vm.Result?.Message ?? "Открыто.");
            RaiseChanged();
        }
        return result == true || vm.IsDone;
    }

    /// <summary>«Обновить до последней»: документы рабочей папки — до последних версий (со своими комплектами).</summary>
    public async Task<bool> UpdateToLatestAsync(IReadOnlyCollection<Guid> documentIds)
    {
        if (documentIds.Count == 0) return false;
        var root = RequireWorkspace("Обновить до последней");
        if (root == null) return false;
        var ws = Workspace;
        var ids = documentIds.ToList();
        var vm = new OpenViewModel(ws, Dialogs, Shell, root, (r, p, ct) => ws.PrepareUpdateToLatestAsync(r, ids, p, ct), "Обновить до последней версии");
        Dialogs.ShowDialog(vm);
        await Task.CompletedTask;
        if (vm.IsDone)
        {
            Busy.Done(vm.Result?.Message ?? "Обновлено.");
            RaiseChanged();
        }
        return vm.IsDone;
    }

    // ------------------------------------------------------------------ блокировки

    /// <summary>«Взять в работу».</summary>
    public async Task<bool> CheckOutAsync(IReadOnlyCollection<Guid> documentIds)
    {
        if (documentIds.Count == 0 || !EnsureCanModify("Взять в работу")) return false;
        var root = RequireWorkspace("Взять в работу");
        if (root == null) return false;
        var ws = Workspace;
        var ids = documentIds.ToList();
        var outcome = await Busy.RunAsync("Взять в работу", (_, ct) => ws.CheckOutAsync(root, ids, ct));
        if (!outcome.Ok) return false;
        var result = outcome.Value!;
        Busy.Done(result.Message);
        RaiseChanged();
        if (!result.Success)
        {
            Dialogs.ShowError("Взять в работу", new ErrorInfo(result.Message, result.Items.Where(i => !i.Success).Select(i => i.Message).ToList()));
            return false;
        }
        return true;
    }

    /// <summary>
    /// «Отменить взятие». Если есть несданные правки — предупредить и спросить: оставить правки в файлах
    /// или вернуть сданную версию из хранилища. Молча ничего не удаляется.
    /// </summary>
    public async Task<bool> UndoCheckOutAsync(IReadOnlyCollection<Guid> documentIds)
    {
        if (documentIds.Count == 0 || !EnsureCanModify("Отменить взятие")) return false;
        var root = RequireWorkspace("Отменить взятие");
        if (root == null) return false;
        var ws = Workspace;
        var ids = documentIds.ToList();
        var prepared = await Busy.RunAsync("Проверка правок", (_, ct) => ws.PrepareUndoCheckOutAsync(root, ids, ct));
        if (!prepared.Ok) return false;
        var plan = prepared.Value!;
        if (plan.Items.Count == 0)
        {
            Dialogs.ShowError("Отменить взятие", ErrorText.FromProblems("Нечего отменять: выбранных документов нет в рабочей папке.", plan.Problems));
            return false;
        }

        bool? revertAll;
        var docs = plan.Items.Select(i => Format.Doc(i.RelPath, i.Designation)).ToList();
        if (plan.HasLocalChanges)
        {
            var changed = plan.Items.Where(i => i.HasLocalChanges).Select(i => Format.Doc(i.RelPath, i.Designation) + " — есть несданные правки").ToList();
            var choice = Dialogs.Choose("Отменить взятие",
                "В документах есть несданные правки. Блокировка будет снята, файлы станут только для чтения. Что сделать с правками?",
                new[] { "Оставить правки в файлах", "Вернуть версию из хранилища (правки будут потеряны)", "Не отменять взятие" },
                changed);
            if (choice == 0) revertAll = false;
            else if (choice == 1)
            {
                if (!Dialogs.Confirm("Вернуть версию из хранилища", "Несданные правки в этих файлах будут потеряны безвозвратно. Продолжить?", "Вернуть", "Отмена", changed))
                    return false;
                revertAll = true;
            }
            else return false;
        }
        else
        {
            if (!Dialogs.Confirm("Отменить взятие", $"Снять блокировку без публикации ({docs.Count})? Файлы станут только для чтения.", "Отменить взятие", "Не отменять", docs))
                return false;
            revertAll = false;
        }

        var outcome = await Busy.RunAsync("Отменить взятие", (p, ct) => ws.ExecuteUndoCheckOutAsync(plan, revertAll, p, ct));
        if (!outcome.Ok) return false;
        var result = outcome.Value!;
        Busy.Done(result.Message);
        RaiseChanged();
        if (!result.Success)
        {
            Dialogs.ShowError("Отменить взятие", ErrorText.FromProblems(result.Message, result.Problems));
            return false;
        }
        var infos = result.Problems.Where(p => !p.IsBlocking).Select(p => p.Message).ToList();
        if (infos.Count > 0) Notices?.Invoke(this, infos);
        return true;
    }

    // ------------------------------------------------------------------ сдача и постановка на учёт

    /// <summary>«Сдать».</summary>
    public async Task<bool> CheckinAsync()
    {
        if (!EnsureCanModify("Сдать")) return false;
        var root = RequireWorkspace("Сдать");
        if (root == null) return false;
        var vm = new CheckinViewModel(Workspace, Dialogs, root);
        var ok = Dialogs.ShowDialog(vm) == true;
        await Task.CompletedTask;
        if (ok && vm.Result != null)
        {
            Busy.Done(vm.Result.Message);
            var infos = vm.Result.Problems.Where(p => !p.IsBlocking).Select(p => p.Message).ToList();
            if (infos.Count > 0) Notices?.Invoke(this, infos);
        }
        RaiseChanged();
        return ok;
    }

    /// <summary>«Поставить на учёт»; после — предложение открыть в рабочую папку.</summary>
    public async Task<bool> ImportAsync(Guid? preferredProject)
    {
        if (!EnsureCanModify("Поставить на учёт")) return false;
        var vm = new ImportViewModel(_session, preferredProject);
        var ok = Dialogs.ShowDialog(vm) == true && vm.Result is { Success: true };
        if (!ok) return false;
        Busy.Done(vm.Result!.Message);
        RaiseChanged();
        if (vm.OpenAfter)
        {
            // Для нового проекта нужна своя рабочая папка.
            if (WorkRoot != null && WorkspaceService.IsWorkspace(WorkRoot))
            {
                var state = Workspace.LoadState(WorkRoot);
                var project = vm.SelectedProject?.Id;
                if (project != null && state.ProjectId != project &&
                    !ChooseWorkRoot("Рабочая папка для нового проекта (папка текущей рабочей папки относится к другому проекту)"))
                    return true;
            }
            await OpenAsync(vm.Result.HeadDocumentId, vm.Result.HeadVersion);
        }
        return true;
    }

    // ------------------------------------------------------------------ прочее

    /// <summary>«Выгрузить комплект».</summary>
    public void Export(Guid documentId, int? version)
    {
        var vm = new ExportViewModel(_session, documentId, version);
        Dialogs.ShowDialog(vm);
    }

    /// <summary>«Переименовать» документ рабочей папки.</summary>
    public bool Rename(string relPath)
    {
        if (!EnsureCanModify("Переименовать")) return false;
        var root = RequireWorkspace("Переименовать");
        if (root == null) return false;
        var vm = new RenameViewModel(Workspace, Dialogs, root, relPath);
        var ok = Dialogs.ShowDialog(vm) == true;
        if (ok)
        {
            Busy.Done(vm.Result?.Message ?? "Переименовано.");
            RaiseChanged();
        }
        return ok;
    }

    /// <summary>Пометить документ «не используется» или вернуть.</summary>
    public async Task<bool> MarkUnusedAsync(Guid documentId, bool unused, string label)
    {
        if (!EnsureCanModify("Не используется")) return false;
        var text = unused
            ? $"Пометить {label} как «не используется»? Документ пропадёт из поиска по умолчанию, история сохранится."
            : $"Вернуть {label} в работу (снять отметку «не используется»)?";
        if (!Dialogs.Confirm("Не используется", text, unused ? "Пометить" : "Вернуть", "Отмена")) return false;
        var ws = Workspace;
        var outcome = await Busy.RunAsync("Не используется", (_, ct) => ws.MarkUnusedAsync(documentId, unused, ct));
        if (!outcome.Ok) return false;
        Busy.Done(unused ? $"{label} помечен как «не используется»." : $"{label} снова используется.");
        RaiseChanged();
        return true;
    }

    /// <summary>Открыть файл рабочей папки в его программе (CAD).</summary>
    public void OpenFile(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) throw new WorkspaceException($"Файла «{fullPath}» нет на диске. Откройте документ из хранилища.");
            Shell.OpenFile(fullPath);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Открыть файл", ErrorText.Describe(ex));
        }
    }

    public void ShowInExplorer(string path)
    {
        try
        {
            Shell.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            Dialogs.ShowError("Показать в проводнике", ErrorText.Describe(ex));
        }
    }
}
