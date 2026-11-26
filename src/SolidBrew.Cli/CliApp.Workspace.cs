using SolidBrew.Core.Paths;
using SolidBrew.Workspace;

namespace SolidBrew.Cli;

public sealed partial class CliApp
{
    // ------------------------------------------------------------------ постановка на учёт

    private async Task<int> ImportAsync()
    {
        var head = FullPath(Need(0, "головной файл"));
        var project = await ResolveProjectAsync(_args.Value("project")
            ?? throw new CliException("Укажите проект: --project <имя или ID>. Список: sbpdm projects.")).ConfigureAwait(false);
        var extras = _args.Values("extra").Select(FullPath).ToList();
        // Отчёт — быстрым чтением ссылок; свойства загружаемых документов — следом (время каждого этапа — в отчёте).
        var options = new ImportOptions { HeadSavedPath = _args.Value("saved-as"), DeferProperties = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var report = await Service.PrepareImportAsync(head, extras, Progress, _ct, options).ConfigureAwait(false);
        var reportSeconds = watch.Elapsed.TotalSeconds;

        // Решения пользователя.
        foreach (var r in report.Documents.SelectMany(d => d.References))
        {
            if (r.Category == ImportReferenceCategory.NameOnly && _args.Flag("accept-name-matches")) r.Accept = true;
            if (r.Category == ImportReferenceCategory.OutsideFolders && _args.Flag("accept-outside")) r.Accept = true;
            if (r.Category == ImportReferenceCategory.Relocated && _args.Flag("accept-relocated")) r.Accept = true;
        }
        foreach (var choice in _args.Values("choose"))
        {
            var eq = choice.LastIndexOf('=');
            if (eq <= 0 || eq == choice.Length - 1)
                throw new CliException($"--choose: ожидается <ссылка>=<путь>, указано «{choice}».");
            var reference = choice.Substring(0, eq).Trim();
            var path = FullPath(choice.Substring(eq + 1).Trim());
            var matching = report.Documents.SelectMany(d => d.References).Where(r => r.Category == ImportReferenceCategory.Ambiguous &&
                (string.Equals(r.SavedPath, reference, StringComparison.OrdinalIgnoreCase) || string.Equals(r.FileName, reference, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matching.Count == 0)
                throw new CliException($"--choose: среди неоднозначных ссылок нет «{reference}».");
            foreach (var r in matching)
            {
                var candidate = r.Candidates.FirstOrDefault(c => string.Equals(c, path, StringComparison.OrdinalIgnoreCase))
                                ?? throw new CliException($"--choose: «{path}» не входит в варианты для «{reference}»: {string.Join("; ", r.Candidates)}.");
                r.Chosen = candidate;
            }
        }
        // --by-link <файл>: не загружать в PDM, оставить ссылкой на файл (имя файла или полный путь исходника).
        foreach (var name in _args.Values("by-link"))
        {
            var full = Path.IsPathFullyQualified(name) ? Path.GetFullPath(name) : null;
            var docs = report.Documents.Where(d => !d.IsHead && (full != null
                ? string.Equals(d.SourcePath, full, StringComparison.OrdinalIgnoreCase)
                : string.Equals(d.FileName, name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (docs.Count == 0) throw new CliException($"--by-link: среди документов постановки нет «{name}» (головной документ оставить ссылкой нельзя).");
            foreach (var d in docs) d.Include = false;
        }
        report.Recompute();
        watch.Restart();
        var propertiesRead = await Service.ReadPendingImportPropertiesAsync(report, Progress, _ct).ConfigureAwait(false);
        var timings = new { reportSeconds = Math.Round(reportSeconds, 1), propertiesSeconds = Math.Round(watch.Elapsed.TotalSeconds, 1), propertiesRead };
        Err.WriteLine($"Отчёт: {report.Documents.Count} документов за {reportSeconds:0.0} с; свойства: {propertiesRead} документов за {watch.Elapsed.TotalSeconds:0.0} с.");

        var allowMissing = _args.Flag("allow-missing");
        var reasons = report.BlockingReasons(allowMissing);
        if (!Json)
        {
            PrintImportReport(report, project);
            PrintBlockingReasons(reasons);
        }
        int Stop(int code, string message)
        {
            if (Json) Emit(ImportJson(report, project, reasons, timings), () => { });
            Err.WriteLine(message);
            return code;
        }

        if (!_args.Flag("yes"))
        {
            return Stop(NeedsAnswer, report.CanExecute(allowMissing)
                ? "Проверьте отчёт и повторите с --yes, чтобы поставить комплект на учёт."
                : "Разрешите проблемы (--choose, --accept-name-matches, --accept-outside, --accept-relocated, --allow-missing) и повторите с --yes.");
        }
        if (!report.CanExecute(allowMissing))
            return Stop(Failed, ImportReport.BlockingSummary(reasons, cli: true));
        if (report.HasNewerCadVersion && !_args.Flag("accept-newer-cad"))
            return Stop(NeedsAnswer, "Есть файлы, сохранённые в версии CAD новее рабочей. Подтвердите их постановку: --accept-newer-cad.");

        var json = ImportJson(report, project, reasons, timings); // до выполнения: после успеха временные копии отчёта удаляются
        var result = await Service.ExecuteImportAsync(report, project.Id, _args.Value("comment"), allowMissing, _args.Flag("accept-newer-cad"), Progress, _ct).ConfigureAwait(false);
        Emit(new { report = json, result }, () =>
        {
            Out.WriteLine(result.Message);
            PrintProblems(result.Problems);
            if (result.Success)
                Out.WriteLine($"Головной документ: ID {result.HeadDocumentId}, v{result.HeadVersion}. Открыть: sbpdm open {result.HeadDocumentId} --to <папка>");
        });
        return result.Success ? Ok : Failed;
    }

    private static object ImportJson(ImportReport report, Core.Contracts.ProjectDto project, List<ImportBlockingReason> reasons, object timings) => new
    {
        project,
        timings,
        canExecute = reasons.Count == 0,
        blockingReasons = reasons.Select(r => new
        {
            r.Code, r.Count, r.Text, action = r.CliAction.Length > 0 ? r.CliAction : r.Action, r.AllowMissingClears,
            documents = r.Documents.Select(d => d.SourcePath),
            references = r.References.Select(x => new { document = x.SourceDocument, x.SavedPath }),
        }),
        head = report.HeadFile,
        searchFolders = report.SearchFolders,
        commonRoot = report.CommonRoot,
        documents = report.Documents.Where(d => d.Disposition != ImportDisposition.NotUsed).Select(d => new
        {
            d.SourcePath, d.RelPath, d.Cad, d.Kind, d.Designation, d.Name, d.Sha256, d.Size, d.CadVersion, d.IsHead, d.IsExtra,
            d.Disposition,
            existing = d.Existing == null ? null : new { documentId = d.Existing.Document.Id, version = d.Existing.Version, project = d.Existing.Document.ProjectName, relPath = d.Existing.Document.RelPath },
            references = d.References.Select(r => new { r.SavedPath, r.Category, r.TargetPath, r.MovedFrom, r.MovedTo, r.Candidates, r.Accept, r.Chosen, target = r.EffectiveTarget }),
        }),
        problems = report.Problems,
    };

    /// <summary>Блок «Нельзя поставить на учёт» — те же причины и подсказки, что в окне постановки (ключи — командной строки).</summary>
    private void PrintBlockingReasons(List<ImportBlockingReason> reasons)
    {
        if (reasons.Count == 0)
        {
            Out.WriteLine("Можно ставить на учёт.");
            return;
        }
        Out.WriteLine("Нельзя поставить на учёт:");
        foreach (var r in reasons)
        {
            Out.WriteLine($"  • {r.Text} — {(r.CliAction.Length > 0 ? r.CliAction : r.Action)}");
            foreach (var d in r.Documents.Take(5))
                Out.WriteLine($"      {d.SourcePath}" + (d.SameNameOpenPath != null ? $"  [открыт: {d.SameNameOpenPath}]" : string.Empty));
            foreach (var x in r.References.Take(5))
                Out.WriteLine($"      {Path.GetFileName(x.SourceDocument)} → {x.SavedPath}");
            var more = r.Documents.Count + r.References.Count - Math.Min(5, r.Documents.Count) - Math.Min(5, r.References.Count);
            if (more > 0) Out.WriteLine($"      … и ещё {more}");
        }
    }

    private void PrintImportReport(ImportReport report, Core.Contracts.ProjectDto project)
    {
        Out.WriteLine($"Постановка на учёт в проект «{project.Name}».");
        Out.WriteLine($"Головной документ: {report.HeadFile}");
        Out.WriteLine("Папки поиска: " + string.Join("; ", report.SearchFolders));
        if (report.CommonRoot != null) Out.WriteLine($"Общая папка (от неё считаются пути): {report.CommonRoot}");
        var included = report.NewDocuments.ToList();
        Out.WriteLine($"Загружаются новыми документами ({included.Count}):");
        foreach (var d in included.OrderBy(d => d.RelPath, StringComparer.OrdinalIgnoreCase))
            Out.WriteLine($"  {d.RelPath}  [{KindText(d.Kind)}{(d.Designation != null ? ", " + d.Designation : "")}{(d.IsHead ? ", головной" : "")}{(d.IsExtra ? ", чертёж из папок поиска" : "")}]");
        var stored = report.FromStorageDocuments.ToList();
        if (stored.Count > 0)
        {
            Out.WriteLine($"Уже есть в PDM — берутся из хранилища ({stored.Count}):");
            foreach (var d in stored.OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase))
                Out.WriteLine($"  {d.SourcePath}  =  {ImportReport.Describe(d.Existing!)}");
        }
        var byLink = report.ByLinkDocuments.ToList();
        if (byLink.Count > 0)
        {
            Out.WriteLine($"Не загружаются, остаются ссылками на файлы ({byLink.Count}):");
            foreach (var d in byLink.OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase))
                Out.WriteLine($"  {d.SourcePath}");
        }

        string From(ImportReference r) => $"{Path.GetFileName(r.SourceDocument)} → {r.SavedPath}";
        var refs = report.References.ToList();
        void Group(ImportReferenceCategory category, string title, Func<ImportReference, string> line)
        {
            var list = refs.Where(r => r.Category == category).ToList();
            Out.WriteLine($"{title}: {list.Count}");
            foreach (var r in list) Out.WriteLine("  " + line(r));
        }
        Group(ImportReferenceCategory.Found, "Найдены", r => $"{From(r)}  =  {r.TargetPath}");
        Group(ImportReferenceCategory.OutsideFolders, "За пределами указанных папок", r =>
            $"{From(r)}  =  {r.TargetPath}  [{(r.Accept ? "подтверждено" : "нужно подтверждение: --accept-outside или добавьте папку --extra")}]");
        Group(ImportReferenceCategory.NameOnly, "Только по имени файла", r =>
            $"{From(r)}  ≈  {r.TargetPath}  [{(r.Accept ? "подтверждено" : "нужно подтверждение: --accept-name-matches")}]");
        Group(ImportReferenceCategory.Relocated, "Папка перенесена — на прежнем месте относительно документа", r =>
            $"{From(r)}  ≈  {r.TargetPath}  [{r.MovedFrom} → {r.MovedTo}; {(r.Accept ? "подтверждено" : "нужно подтверждение: --accept-relocated")}]");
        Group(ImportReferenceCategory.Ambiguous, "Неоднозначно", r =>
            $"{From(r)}: {(r.EffectiveTarget != null ? "выбран " + r.EffectiveTarget : "выберите: --choose \"" + r.FileName + "=<путь>\"")}; варианты: {string.Join("; ", r.Candidates)}");
        Group(ImportReferenceCategory.Missing, "Отсутствуют", r => $"{From(r)}  [не найден; --allow-missing — поставить без него]");
        var problems = report.Problems.Where(p => p.Code is not (LocalProblemCodes.MissingReference or LocalProblemCodes.AmbiguousReference or LocalProblemCodes.Unconfirmed)).ToList();
        if (problems.Count > 0)
        {
            Out.WriteLine("Проблемы:");
            PrintProblems(problems);
        }
    }

    // ------------------------------------------------------------------ открыть, обновить

    private async Task<int> OpenAsync()
    {
        var doc = await ResolveDocumentAsync(Need(0, "документ")).ConfigureAwait(false);
        var root = _args.Value("to") is { } to ? FullPath(to) : FindWorkspace(BaseDir) ?? BaseDir;
        var plan = await Service.PrepareOpenAsync(root, doc.Id, _args.Int("version"), Progress, _ct, includeDrawings: _args.Flag("with-drawings")).ConfigureAwait(false);
        var code = await ExecuteOpenPlanAsync(plan, _args.Flag("verify")).ConfigureAwait(false);
        if (code == Ok)
        {
            Profile.RememberProduct(plan.HeadDocumentId, plan.WorkRoot, doc.Designation, doc.Name);
            SaveProfile();
        }
        return code;
    }

    private async Task<int> UpdateAsync()
    {
        var root = WorkRoot();
        var docs = await WorkspaceDocsAsync(root, _args.Positionals).ConfigureAwait(false);
        var plan = await Service.PrepareUpdateToLatestAsync(root, docs.Select(d => d.Id), Progress, _ct).ConfigureAwait(false);
        return await ExecuteOpenPlanAsync(plan, verify: false).ConfigureAwait(false);
    }

    private async Task<int> ExecuteOpenPlanAsync(OpenPlan plan, bool verify)
    {
        if (_args.Flag("replace-all") && _args.Flag("keep-all")) throw new CliException("Укажите что-то одно: --replace-all или --keep-all.");
        foreach (var c in plan.Conflicts)
        {
            if (_args.Flag("replace-all")) c.Resolution = ConflictResolution.Replace;
            if (_args.Flag("keep-all")) c.Resolution = ConflictResolution.Keep;
        }

        void PrintPlan()
        {
            Out.WriteLine($"{(plan.IsUpdate ? "Обновление" : "Открытие")} {plan.HeadRelPath} v{plan.HeadVersion} в «{plan.WorkRoot}»{(plan.CreatesWorkspace ? " (новая рабочая папка)" : "")}.");
            Out.WriteLine(plan.Summary);
            foreach (var c in plan.Conflicts)
                Out.WriteLine($"  Конфликт: {c.Message} Решение: {(c.Resolution == ConflictResolution.Replace ? "заменить" : "оставить")}.");
            PrintProblems(plan.Problems);
        }

        if (!plan.CanExecute)
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Открыть нельзя: есть ошибки в плане.");
            return Failed;
        }
        if (plan.HasConflicts && !_args.Flag("yes"))
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("В рабочей папке есть конфликты. Решения по умолчанию показаны выше; повторите с --yes (можно с --replace-all или --keep-all).");
            return NeedsAnswer;
        }

        var result = await Service.ExecuteOpenAsync(plan, verify, Progress, _ct).ConfigureAwait(false);
        Emit(new { plan, result }, () =>
        {
            PrintPlan();
            Out.WriteLine(result.Message);
            if (result.Kept.Count > 0) Out.WriteLine("Оставлены как есть: " + string.Join(", ", result.Kept));
            if (result.ReferencesRewritten.Count > 0) Out.WriteLine("Ссылки переписаны на рабочую папку: " + string.Join(", ", result.ReferencesRewritten));
            PrintProblems(result.Problems);
            if (result.CadReport is { } cad)
            {
                Out.WriteLine($"Проверка в CAD: {(cad.Ok ? "все компоненты загружены из рабочей папки" : "есть проблемы")}.");
                foreach (var config in cad.Configurations)
                {
                    Out.WriteLine($"  Конфигурация «{config.Name}»:");
                    foreach (var comp in config.Components)
                        Out.WriteLine($"    {(comp.InsideWorkRoot ? "ок " : "НЕ ИЗ ПАПКИ ")} {comp.Name} ← {comp.LoadedPath}");
                }
            }
            Out.WriteLine("Головной файл: " + result.HeadFullPath);
        });
        return result.Success && (result.CadReport?.Ok ?? true) ? Ok : Failed;
    }

    // ------------------------------------------------------------------ состояние

    private async Task<int> StatusAsync()
    {
        var root = WorkRoot();
        var status = await Service.GetStatusAsync(root, !_args.Flag("offline"), Progress, _ct).ConfigureAwait(false);
        Emit(status, () =>
        {
            Out.WriteLine($"Рабочая папка: {status.WorkRoot}");
            if (status.ServerError != null) Out.WriteLine("Сервер недоступен — показано локальное состояние.");
            if (status.Files.Count == 0) Out.WriteLine("Учтённых файлов и черновиков нет.");
            else Table(new[] { "Файл", "Версия", "Состояние", "Обозначение" }, status.Files.Select(f => new[]
            {
                f.RelPath,
                f.LocalVersion is { } v ? "v" + v + (f.LatestVersion is { } l && l != v ? $"/v{l}" : "") : "—",
                f.StateText + (f.IsReadOnly ? "" : f.Local == LocalFileState.Draft ? "" : ", запись разрешена"),
                f.Designation,
            }));
            foreach (var m in status.Messages) Out.WriteLine(m);
        });
        return Ok;
    }

    // ------------------------------------------------------------------ взять, отменить

    private async Task<int> CheckOutAsync()
    {
        var root = WorkRoot();
        var docs = await WorkspaceDocsAsync(root, _args.Positionals).ConfigureAwait(false);
        var result = await Service.CheckOutAsync(root, docs.Select(d => d.Id), _ct).ConfigureAwait(false);
        Emit(result, () =>
        {
            foreach (var i in result.Items) Out.WriteLine(i.Message);
            Out.WriteLine(result.Message);
        });
        return result.Success ? Ok : Failed;
    }

    private async Task<int> UndoAsync()
    {
        var root = WorkRoot();
        var docs = await WorkspaceDocsAsync(root, _args.Positionals).ConfigureAwait(false);
        var plan = await Service.PrepareUndoCheckOutAsync(root, docs.Select(d => d.Id), _ct).ConfigureAwait(false);
        if (plan.Problems.Any(p => p.IsBlocking))
        {
            Emit(new { plan }, () => PrintProblems(plan.Problems));
            return Failed;
        }
        var revert = _args.Flag("revert");
        if (revert && plan.HasLocalChanges && !_args.Flag("yes"))
        {
            Emit(new { plan }, () =>
            {
                Out.WriteLine(plan.Warning);
                Out.WriteLine("С --revert несданные правки будут потеряны.");
            });
            Err.WriteLine("Подтвердите возврат к сданной версии: повторите с --yes (или без --revert, чтобы сохранить правки).");
            return NeedsAnswer;
        }
        var result = await Service.ExecuteUndoCheckOutAsync(plan, revert, Progress, _ct).ConfigureAwait(false);
        Emit(new { plan, result }, () =>
        {
            if (!revert && plan.Warning != null) Out.WriteLine(plan.Warning);
            Out.WriteLine(result.Message);
            PrintProblems(result.Problems);
        });
        return result.Success ? Ok : Failed;
    }

    // ------------------------------------------------------------------ сдать

    private async Task<int> CheckinAsync()
    {
        var root = WorkRoot();
        var plan = await Service.PrepareCheckinAsync(root, Progress, _ct).ConfigureAwait(false);
        CheckinCandidate Find(string file)
        {
            var rel = RelOf(root, file);
            return plan.Items.FirstOrDefault(i => RelPath.Key(i.RelPath) == RelPath.Key(rel))
                   ?? throw new CliException($"«{rel}» нет среди документов для сдачи (не взят в работу и не черновик).");
        }
        foreach (var f in _args.Values("include")) Find(f).Include = true;
        foreach (var f in _args.Values("exclude")) Find(f).Include = false;
        plan.Revalidate();

        void PrintPlan()
        {
            if (plan.Items.Count == 0)
            {
                Out.WriteLine("Сдавать нечего: нет взятых в работу документов и черновиков.");
                return;
            }
            Out.WriteLine("Документы:");
            foreach (var c in plan.Items.OrderBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase))
            {
                var ver = c.IsDraft ? "новый" : $"v{c.BaseVersion} → v{c.BaseVersion + 1}";
                Out.WriteLine($"  [{(c.Include ? "x" : " ")}] {c.RelPath}  ({ver}; {c.Reason})");
            }
            Out.WriteLine(plan.Summary);
            PrintProblems(plan.Problems);
        }

        if (!plan.Included.Any())
        {
            Emit(new { plan }, PrintPlan);
            if (plan.Items.Count > 0) Err.WriteLine("Не отмечено ни одного документа: отметьте нужные (--include).");
            return Ok;
        }
        if (plan.HasBlockingProblems)
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Сдача невозможна: есть блокирующие проблемы.");
            return Failed;
        }
        if (!_args.Flag("yes"))
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Проверьте список и повторите с --yes, чтобы сдать.");
            return NeedsAnswer;
        }
        if (plan.HasNewerCadVersion && !_args.Flag("accept-newer-cad"))
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Есть файлы, сохранённые в версии CAD новее рабочей. Подтвердите сдачу: --accept-newer-cad.");
            return NeedsAnswer;
        }

        var result = await Service.ExecuteCheckinAsync(plan, _args.Value("comment"), _args.Flag("keep"), _args.Flag("accept-newer-cad"), Progress, _ct).ConfigureAwait(false);
        Emit(new { plan, result }, () =>
        {
            PrintPlan();
            Out.WriteLine(result.Message);
            foreach (var p in result.Published) Out.WriteLine($"  {p.RelPath} v{p.Version}");
            PrintProblems(result.Problems);
        });
        return result.Success ? Ok : Failed;
    }

    // ------------------------------------------------------------------ переименовать

    private async Task<int> RenameAsync()
    {
        var root = WorkRoot();
        var rel = RelOf(root, Need(0, "файл"));
        var target = Need(1, "новый путь");
        string newRel;
        try
        {
            newRel = RelPath.GetRelative(root, FullPath(target));
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }
        var plan = await Service.PrepareRenameAsync(root, rel, newRel, _ct).ConfigureAwait(false);

        void PrintPlan()
        {
            Out.WriteLine(plan.Summary);
            foreach (var p in plan.Parents)
                Out.WriteLine($"  {Label(p.RelPath, p.Designation)} v{p.LatestVersion}: {LockText(p.LockInfo)}");
            PrintProblems(plan.Problems);
        }

        if (!plan.CanExecute)
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Переименование невозможно.");
            return Failed;
        }
        if (!_args.Flag("yes"))
        {
            Emit(new { plan }, PrintPlan);
            Err.WriteLine("Проверьте план и повторите с --yes.");
            return NeedsAnswer;
        }
        var result = await Service.ExecuteRenameAsync(plan, _args.Value("comment"), Progress, _ct).ConfigureAwait(false);
        Emit(new { plan, result }, () =>
        {
            Out.WriteLine(result.Message);
            foreach (var p in result.Published) Out.WriteLine($"  {p.RelPath} v{p.Version}");
            PrintProblems(result.Problems);
        });
        return result.Success ? Ok : Failed;
    }
}
