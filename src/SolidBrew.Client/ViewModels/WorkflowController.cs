using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Cad;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Команды раздела 7 над текущей рабочей папкой: открыть, взять в работу, сдать, отменить взятие,
/// обновить до последней, поставить на учёт, выгрузить, переименовать. Общие для всех экранов главного окна.
/// </summary>
public sealed partial class WorkflowController : ObservableObject
{
    private readonly AppSession _session;

    /// <param name="restoreLast">Начать с последней рабочей папки профиля (окно изделия задаёт папку само).</param>
    public WorkflowController(AppSession session, BusyState busy, bool restoreLast = true)
    {
        _session = session;
        Busy = busy;
        foreach (var w in session.Profile.RecentWorkspaces) RecentWorkspaces.Add(w);
        var last = session.Profile.LastWorkspace;
        if (restoreLast && !string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) _workRoot = last;
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

    /// <summary>«Открыть»: выбранная версия комплекта в текущую рабочую папку (если не задана — спросить).</summary>
    public async Task<bool> OpenAsync(Guid documentId, int? version, string? label = null, bool includeDrawings = false)
    {
        var root = WorkRoot;
        if (root == null)
        {
            if (!ChooseWorkRoot("Рабочая папка для открытия" + (label != null ? $" {label}" : string.Empty))) return false;
            root = WorkRoot!;
        }
        var opened = await OpenIntoAsync(root, documentId, version, label, includeDrawings);
        if (opened == null) return false;
        SetWorkRoot(opened);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// «Открыть» в заданную папку. Без конфликтов и ошибок открывается сразу, ничего не спрашивая;
    /// иначе показывается окно конфликтов. Возвращает итоговую рабочую папку (в окне её можно сменить) или null.
    /// </summary>
    public Task<string?> OpenIntoAsync(string workRoot, Guid documentId, int? version, string? label, bool includeDrawings)
    {
        var ws = Workspace;
        var title = "Открыть" + (label != null ? $" {label}" : string.Empty) + (version != null ? $" v{version}" : string.Empty);
        return RunOpenPlanAsync(workRoot, (r, p, ct) => ws.PrepareOpenAsync(r, documentId, version, p, ct, includeDrawings), title);
    }

    /// <summary>«Обновить до последней»: документы рабочей папки — до последних версий (со своими комплектами).</summary>
    public async Task<bool> UpdateToLatestAsync(IReadOnlyCollection<Guid> documentIds)
    {
        if (documentIds.Count == 0) return false;
        var root = RequireWorkspace("Обновить до последней");
        if (root == null) return false;
        var ws = Workspace;
        var ids = documentIds.ToList();
        var done = await RunOpenPlanAsync(root, (r, p, ct) => ws.PrepareUpdateToLatestAsync(r, ids, p, ct), "Обновить до последней версии");
        RaiseChanged();
        return done != null;
    }

    /// <summary>Подготовить план; без конфликтов и ошибок — выполнить сразу, иначе — окно конфликтов.</summary>
    private async Task<string?> RunOpenPlanAsync(string root, OpenViewModel.PlanFactory prepare, string title)
    {
        var prepared = await Busy.RunAsync("Подготовка: " + title, (p, ct) => prepare(root, p, ct));
        if (!prepared.Ok) return null;
        var plan = prepared.Value!;
        var ws = Workspace;
        // Другая версия того же документа без правок заменяется молча; правки, чужие файлы и перемещения — решает пользователь.
        var needsDecision = plan.Conflicts.Any(c => c.Kind != OpenConflictKind.OtherVersion || c.Resolution != ConflictResolution.Replace);
        if (plan.CanExecute && !needsDecision)
        {
            var outcome = await Busy.RunAsync(title, (p, ct) => ws.ExecuteOpenAsync(plan, false, p, ct));
            if (!outcome.Ok) return null;
            var result = outcome.Value!;
            if (!result.Success)
            {
                Dialogs.ShowError(title, ErrorText.FromProblems(result.Message, result.Problems));
                return null;
            }
            Busy.Done(result.Message);
            var infos = plan.Problems.Concat(result.Problems).Where(p => !p.IsBlocking).Select(p => p.Message).Distinct().ToList();
            if (infos.Count > 0) Notices?.Invoke(this, infos);
            return root;
        }

        var first = true;
        var vm = new OpenViewModel(ws, Dialogs, Shell, root, (r, p, ct) =>
        {
            if (first && string.Equals(r, root, StringComparison.OrdinalIgnoreCase))
            {
                first = false;
                return Task.FromResult(plan);
            }
            return prepare(r, p, ct);
        }, title);
        Dialogs.ShowDialog(vm);
        if (!vm.IsDone) return null;
        Busy.Done(vm.Result?.Message ?? "Открыто.");
        return vm.WorkRoot;
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
        if (!await ConfirmOtherCopiesAsync("Взять в работу", root, ids, "Всё равно взять")) return false;
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
        var ws = Workspace;
        var changed = await Busy.RunAsync("Проверка правок", async (p, ct) =>
        {
            var status = await ws.GetStatusAsync(root, false, p, ct);
            return status.Files.Where(f => f.Local == LocalFileState.ModifiedLocally && f.DocumentId != null).Select(f => f.DocumentId!.Value).ToList();
        }, reportErrors: false);
        if (changed.Ok && !await ConfirmOtherCopiesAsync("Сдать", root, changed.Value!, "Всё равно сдать")) return false;
        var vm = new CheckinViewModel(ws, Dialogs, root);
        var ok = Dialogs.ShowDialog(vm) == true;
        if (ok && vm.Result != null)
        {
            Busy.Done(vm.Result.Message);
            var infos = vm.Result.Problems.Where(p => !p.IsBlocking).Select(p => p.Message).ToList();
            if (infos.Count > 0) Notices?.Invoke(this, infos);
        }
        RaiseChanged();
        return ok;
    }

    /// <summary>
    /// «Поставить на учёт». Возвращает новое изделие, если пользователь выбрал «Открыть» (его открывает окно изделия), иначе null.
    /// </summary>
    /// <param name="headFile">Головной файл (выбран заранее, например «Добавить проект…»): отчёт строится сразу.</param>
    public ProductRef? Import(Guid? preferredProject, string? headFile = null)
    {
        if (!EnsureCanModify("Поставить на учёт")) return null;
        var vm = new ImportViewModel(_session, preferredProject, headFile: headFile);
        var ok = Dialogs.ShowDialog(vm) == true && vm.Result is { Success: true };
        if (!ok) return null;
        Busy.Done(vm.Result!.Message);
        RaiseChanged();
        return vm.OpenAfter ? new ProductRef(vm.Result.HeadDocumentId) : null;
    }

    /// <summary>
    /// Записать свойства CAD в файл документа и сдать новую версию (взятие и сдача — внутри операции, см.
    /// <see cref="WorkspaceService.WritePropertiesAsync"/>). Если CAD преобразовал файл в версию новее рабочей — спросить.
    /// </summary>
    public Task<bool> WritePropertiesAsync(Guid documentId, IReadOnlyList<CadPropertyChange> changes) =>
        WritePropertiesAsync(new[] { new PropertyWrite(documentId, changes) });

    /// <summary>
    /// Пакетная запись свойств (заполненных в карточках нескольких документов): одной операцией взять, записать в файлы,
    /// сдать новыми версиями. <paramref name="confirmLines"/> — если задан, сначала спросить подтверждение с этим перечнем.
    /// </summary>
    public async Task<bool> WritePropertiesAsync(IReadOnlyList<PropertyWrite> writes, IReadOnlyList<string>? confirmLines = null)
    {
        const string title = "Записать свойства";
        if (writes.Count == 0 || writes.All(w => w.Changes.Count == 0)) return true;
        if (!EnsureCanModify(title)) return false;
        var root = RequireWorkspace(title);
        if (root == null) return false;
        var docs = writes.Select(w => w.DocumentId).Distinct().Count();
        if (confirmLines != null && !Dialogs.Confirm("Сдать",
                $"Записать заполненные свойства в файлы ({docs}) и сдать их новыми версиями? Документы будут взяты в работу и сданы автоматически.",
                "Записать и сдать", "Отмена", confirmLines))
            return false;
        var ws = Workspace;
        var outcome = await Busy.RunAsync("Запись свойств", (p, ct) => ws.WritePropertiesAsync(root, writes, null, false, p, ct));
        if (outcome.Ok && outcome.Value is { Success: false, NeedsNewerCadConfirmation: true } first)
        {
            if (!Dialogs.Confirm("Версия CAD новее рабочей",
                    first.Message + " Коллеги с рабочей версией CAD не смогут открыть файл.",
                    "Записать и сдать", "Отмена", first.Problems.Select(p => p.Message)))
            {
                RaiseChanged();
                return false;
            }
            outcome = await Busy.RunAsync("Запись свойств", (p, ct) => ws.WritePropertiesAsync(root, writes, null, true, p, ct));
        }
        if (!outcome.Ok)
        {
            RaiseChanged();
            return false;
        }
        var r = outcome.Value!;
        if (!r.Success)
        {
            Dialogs.ShowError(title, ErrorText.FromProblems(r.Message, r.Problems));
            RaiseChanged();
            return false;
        }
        Busy.Done(r.Message);
        Notices?.Invoke(this, new[] { r.Message });
        RaiseChanged();
        return true;
    }

    /// <summary>Файл «по ссылке» для сдачи: чья ссылка (родитель), путь в ней и найденный файл.</summary>
    public sealed record LinkedFile(Guid ParentId, Guid ProjectId, string ParentRelPath, string ParentLabel, string RefPath, string LinkPath);

    /// <summary>
    /// Файл, оставленный ссылкой (не учтён в PDM): поставить на учёт (рядом с родителем в проекте; уже учтённое содержимое
    /// берётся из хранилища), привязать к нему ссылку родителя (новая версия родителя) и разложить в рабочую папку.
    /// Дальше это обычный документ: взять в работу, сдать, обновить.
    /// </summary>
    public async Task<bool> RegisterLinkedAsync(Guid parentId, Guid projectId, string parentRelPath, string parentLabel, string refPath, string linkPath)
    {
        const string title = "Поставить на учёт";
        if (!EnsureCanModify(title)) return false;
        var root = RequireWorkspace(title);
        if (root == null) return false;

        var slash = parentRelPath.LastIndexOf('/');
        var prefix = slash > 0 ? parentRelPath.Substring(0, slash) : string.Empty;
        var vm = new ImportViewModel(_session, projectId, new ImportPreset(linkPath, prefix, parentLabel, refPath));
        if (Dialogs.ShowDialog(vm) != true || vm.Result is not { Success: true } imported) return false;

        var ws = Workspace;
        var outcome = await Busy.RunAsync("Привязка ссылки", (_, ct) =>
            ws.BindReferenceAsync(root, parentId, refPath, imported.HeadDocumentId, imported.HeadVersion, ct));
        if (!outcome.Ok)
        {
            RaiseChanged();
            return false;
        }
        var bind = outcome.Value!;
        if (!bind.Success)
        {
            RaiseChanged();
            Dialogs.ShowError(title, ErrorText.FromProblems(
                $"Файл поставлен на учёт ({imported.Message}), но ссылка {parentLabel} не привязана: {bind.Message} " +
                "Повторите «Поставить на учёт» — загружать заново ничего не придётся.", bind.Problems));
            return false;
        }
        Busy.Done(bind.Message);
        Notices?.Invoke(this, new[] { imported.Message, bind.Message });

        // Разложить: родитель без правок — новой версией (с переписанными ссылками); с правками — только новый документ.
        await UpdateToLatestAsync(bind.ParentModifiedLocally ? new[] { imported.HeadDocumentId } : new[] { parentId });
        return true;
    }

    /// <summary>
    /// Несколько файлов «по ссылке» (выделены в дереве) — сдать в хранилище разом, без окна постановки на каждый:
    /// каждый ставится на учёт рядом со своим родителем (если отчёт без вопросов — иначе он пропускается с объяснением),
    /// затем ссылки каждого родителя привязываются одной новой версией, и всё раскладывается в рабочую папку.
    /// </summary>
    public async Task<bool> RegisterLinkedManyAsync(IReadOnlyList<LinkedFile> files)
    {
        const string title = "Сдать в хранилище";
        if (files.Count == 0) return false;
        if (files.Count == 1)
        {
            var f = files[0];
            return await RegisterLinkedAsync(f.ParentId, f.ProjectId, f.ParentRelPath, f.ParentLabel, f.RefPath, f.LinkPath);
        }
        if (!EnsureCanModify(title)) return false;
        var root = RequireWorkspace(title);
        if (root == null) return false;
        if (!Dialogs.Confirm(title, $"Поставить на учёт {files.Count} файлов по ссылке и привязать к ним ссылки сборок?",
                "Сдать", "Отмена", files.Select(f => $"{Path.GetFileName(f.LinkPath)} — {f.LinkPath}")))
            return false;

        var ws = Workspace;
        var failures = new List<string>();
        var imported = new List<(LinkedFile File, Guid Id, int Version)>();
        var reports = new List<(LinkedFile File, ImportReport Report)>();
        try
        {
            // 1. Прочитать все файлы. Ссылки, где найден ровно один файл (перенос папки, совпадение по имени, вне папок),
            //    не повод сдавать файл отдельно: они подтверждаются одним окном на всю пакетную сдачу.
            var toConfirm = new List<(LinkedFile File, ImportReport Report, List<ImportReference> Refs)>();
            foreach (var f in files)
            {
                var slash = f.ParentRelPath.LastIndexOf('/');
                var options = new ImportOptions
                {
                    RelPathPrefix = slash > 0 ? f.ParentRelPath.Substring(0, slash) : string.Empty,
                    AllowExistingHead = true,
                    HeadSavedPath = f.RefPath,
                };
                var prepared = await Busy.RunAsync($"Чтение «{Path.GetFileName(f.LinkPath)}»", (p, ct) => ws.PrepareImportAsync(f.LinkPath, new List<string>(), p, ct, options));
                if (!prepared.Ok) return false; // отмена или ошибка — уже показаны
                var report = prepared.Value!;
                reports.Add((f, report));
                if (report.CanExecute(false)) continue;
                var accepted = report.AcceptConfirmable();
                if (accepted.Count > 0 && report.CanExecute(false))
                {
                    toConfirm.Add((f, report, accepted));
                    continue;
                }
                foreach (var r in accepted) r.Accept = false;
                report.Recompute();
                failures.Add($"«{Path.GetFileName(f.LinkPath)}»: нужны решения по ссылкам — сдайте его отдельно. " +
                             string.Join(" ", report.Problems.Where(p => p.IsBlocking).Select(p => p.Message).Take(2)));
                reports.Remove((f, report));
                report.Dispose();
            }

            // 2. Одно подтверждение на все найденные не по сохранённому пути ссылки.
            if (toConfirm.Count > 0)
            {
                var details = toConfirm.SelectMany(x => x.Refs.Select(r => $"«{Path.GetFileName(r.SourceDocument)}» → «{r.FileName}»: {r.DecisionText}"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var ok = Dialogs.Confirm(title,
                    $"Ссылки {toConfirm.Count} из {files.Count} файлов найдены не по сохранённому пути. PDM не принимает такие файлы сама — " +
                    "проверьте, что найдены нужные файлы. Они будут поставлены на учёт вместе с файлами по ссылке.",
                    "Подтвердить и сдать", "Пропустить эти файлы", details);
                if (!ok)
                {
                    foreach (var x in toConfirm)
                    {
                        failures.Add($"«{Path.GetFileName(x.File.LinkPath)}»: ссылки не подтверждены — пропущен.");
                        reports.Remove((x.File, x.Report));
                        x.Report.Dispose();
                    }
                }
            }

            // 3. Постановка по очереди. Общий компонент (например, исходная модель нескольких деталей) загружается первой
            //    постановкой, следующие берут его из хранилища.
            bool? acceptNewer = null;
            for (var i = 0; i < reports.Count; i++)
            {
                var (f, report) = reports[i];
                if (i > 0)
                {
                    if (!await Busy.RunAsync("Поиск уже учтённых файлов", (_, ct) => ws.RefreshExistingAsync(report, ct))) return false;
                }
                if (report.HasNewerCadVersion)
                {
                    acceptNewer ??= Dialogs.Confirm("Версия CAD новее рабочей",
                        "Некоторые файлы сохранены в версии CAD новее рабочей версии предприятия. Коллеги с рабочей версией не смогут их открыть. Всё равно поставить на учёт?",
                        "Поставить на учёт", "Пропустить такие");
                    if (acceptNewer != true)
                    {
                        failures.Add($"«{Path.GetFileName(f.LinkPath)}»: версия CAD новее рабочей — пропущен.");
                        continue;
                    }
                }
                var newer = acceptNewer == true;
                var done = await Busy.RunAsync($"Постановка на учёт «{Path.GetFileName(f.LinkPath)}»", (p, ct) => ws.ExecuteImportAsync(report, f.ProjectId, null, false, newer, p, ct));
                if (!done.Ok) return false;
                if (!done.Value!.Success)
                {
                    failures.Add($"«{Path.GetFileName(f.LinkPath)}»: {done.Value.Message}");
                    continue;
                }
                imported.Add((f, done.Value.HeadDocumentId, done.Value.HeadVersion));
            }
        }
        finally
        {
            // Временные копии непоставленных (успешная постановка удаляет их сама; повторное удаление безвредно).
            foreach (var (_, report) in reports) report.Dispose();
        }

        var update = new HashSet<Guid>();
        var messages = new List<string>();
        foreach (var group in imported.GroupBy(i => i.File.ParentId))
        {
            var bindings = group.Select(i => new ReferenceBinding(i.File.RefPath, i.Id, i.Version)).ToList();
            var label = group.First().File.ParentLabel;
            var outcome = await Busy.RunAsync("Привязка ссылок", (_, ct) => ws.BindReferencesAsync(root, group.Key, bindings, ct));
            if (!outcome.Ok) break;
            var bind = outcome.Value!;
            if (!bind.Success)
            {
                failures.Add($"Файлы поставлены на учёт, но ссылки {label} не привязаны: {bind.Message} Повторите «Сдать в хранилище» — загружать заново не придётся.");
                continue;
            }
            messages.Add(bind.Message);
            if (bind.ParentModifiedLocally) update.UnionWith(group.Select(i => i.Id));
            else update.Add(group.Key);
        }

        if (failures.Count > 0)
            Dialogs.ShowError(title, new ErrorInfo($"Сдано в хранилище {imported.Count} из {files.Count}.", failures));
        if (messages.Count > 0) Notices?.Invoke(this, messages);
        if (update.Count > 0) await UpdateToLatestAsync(update.ToList());
        else RaiseChanged();
        return failures.Count == 0;
    }

    // ------------------------------------------------------------------ правки в других рабочих папках

    /// <summary>
    /// Предупредить, если документы изменены и в другой моей рабочей папке (правки в двух папках не сливаются).
    /// Проверка хеширует файлы — выполняется не в потоке окна. true — продолжать.
    /// </summary>
    public async Task<bool> ConfirmOtherCopiesAsync(string action, string root, IReadOnlyCollection<Guid> documentIds, string proceedText)
    {
        if (documentIds.Count == 0) return true;
        var known = ProductFolders.KnownWorkspaces(_session.Profile);
        if (known.Count == 0) return true;
        var ids = documentIds.Distinct().ToList();
        var outcome = await Busy.RunAsync("Проверка других рабочих папок", (_, ct) =>
        {
            var list = new List<WorkspaceCopy>();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                list.AddRange(WorkspaceService.FindOtherWorkspaceCopies(id, root, known).Where(c => c.IsModified));
            }
            return Task.FromResult(list);
        }, reportErrors: false);
        if (!outcome.Ok || outcome.Value!.Count == 0) return true;
        var copies = outcome.Value;
        return Dialogs.Confirm(action,
            copies.Count == 1
                ? "Документ изменён и в другой вашей рабочей папке."
                : $"Документы изменены и в других ваших рабочих папках ({copies.Count}).",
            proceedText, "Отмена", copies.Select(c => c.Message));
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

    /// <summary>
    /// «Удалить…» (только администратор): документ пропадает из PDM — дерева, поиска, списков; история версий остаётся в журнале.
    /// Сервер отказывает, если на документ ссылаются опубликованные версии других документов (тогда — «не используется»).
    /// </summary>
    public async Task<bool> DeleteDocumentAsync(Guid documentId, string label)
    {
        const string title = "Удалить";
        if (!_session.IsAdmin)
        {
            Dialogs.ShowError(title, new ErrorInfo("Удалять документы может только администратор PDM.", new List<string>()));
            return false;
        }
        if (!Dialogs.Confirm(title,
                $"Удалить {label} из PDM? Документ пропадёт из дерева, поиска и списков, его путь в проекте освободится. " +
                "История версий останется в журнале. Отменить удаление нельзя.",
                "Удалить", "Отмена"))
            return false;
        var ws = Workspace;
        var root = WorkRoot;
        var outcome = await Busy.RunAsync("Удаление", (_, ct) => ws.DeleteDocumentAsync(root, documentId, ct));
        if (!outcome.Ok) return false;
        var r = outcome.Value!;
        if (!r.Success)
        {
            Dialogs.ShowError(title, ErrorText.FromProblems(r.Message, r.Problems.Where(p => p.Message != r.Message)));
            return false;
        }
        Busy.Done(r.Message);
        var infos = r.Problems.Select(p => p.Message).ToList();
        if (infos.Count > 0) Notices?.Invoke(this, infos);
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// «Удалить ссылку…» (только администратор): убрать из родителя ссылки на файлы, не учтённые в PDM, и сдать его новую версию
    /// (<see cref="WorkspaceService.RemoveReferencesAsync"/>). <paramref name="parentIsPart"/> — у детали разрываются внешние ссылки.
    /// </summary>
    public async Task<bool> RemoveReferencesAsync(Guid parentId, string parentLabel, bool parentIsPart, IReadOnlyList<string> refPaths)
    {
        const string title = "Удалить ссылку";
        if (refPaths.Count == 0) return false;
        if (!_session.IsAdmin)
        {
            Dialogs.ShowError(title, new ErrorInfo("Убирать ссылки может только администратор PDM.", new List<string>()));
            return false;
        }
        var root = RequireWorkspace(title);
        if (root == null) return false;
        var what = parentIsPart
            ? $"У детали {parentLabel} будут разорваны внешние ссылки (SOLIDWORKS разрывает их все сразу): элементы, построенные в контексте этих сборок, сохранят геометрию, но перестанут обновляться. PDM больше не будет показывать эти ссылки."
            : $"Из сборки {parentLabel} будут удалены компоненты с этими файлами.";
        if (!Dialogs.Confirm(title, what + " Документ будет взят в работу, изменён в SOLIDWORKS и сдан новой версией.", "Удалить ссылки", "Отмена",
                refPaths.Select(p => Path.GetFileName(p.Replace('/', '\\')) + " — " + p)))
            return false;
        var ws = Workspace;
        var outcome = await Busy.RunAsync("Удаление ссылок", (p, ct) => ws.RemoveReferencesAsync(root, parentId, refPaths, null, false, p, ct));
        if (outcome.Ok && outcome.Value is { Success: false, NeedsNewerCadConfirmation: true } first)
        {
            if (!Dialogs.Confirm("Версия CAD новее рабочей", first.Message + " Коллеги с рабочей версией CAD не смогут открыть файл.",
                    "Сохранить и сдать", "Отмена", first.Problems.Select(p => p.Message)))
            {
                RaiseChanged();
                return false;
            }
            outcome = await Busy.RunAsync("Удаление ссылок", (p, ct) => ws.RemoveReferencesAsync(root, parentId, refPaths, null, true, p, ct));
        }
        if (!outcome.Ok)
        {
            RaiseChanged();
            return false;
        }
        var r = outcome.Value!;
        if (!r.Success)
        {
            Dialogs.ShowError(title, ErrorText.FromProblems(r.Message, r.Problems.Where(p => !r.Message.Contains(p.Message, StringComparison.Ordinal))));
            RaiseChanged();
            return false;
        }
        Busy.Done(r.Message);
        Notices?.Invoke(this, new[] { r.Message });
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
