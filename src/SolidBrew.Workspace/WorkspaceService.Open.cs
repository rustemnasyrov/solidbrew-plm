using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Подготовить открытие версии документа (по умолчанию последней) в рабочую папку.
    /// Папка создаётся при выполнении, если её ещё нет. Ничего не меняет на диске.
    /// </summary>
    public async Task<OpenPlan> PrepareOpenAsync(string workRoot, Guid documentId, int? version = null,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        progress?.Report(new WorkProgress(WorkStage.Preparing, 0, 0, "Получение состава комплекта"));
        var ver = version ?? (await Api.GetDocumentAsync(documentId, ct).ConfigureAwait(false)).Document.LatestVersion;
        var kit = await Api.GetKitAsync(documentId, ver, ct).ConfigureAwait(false);
        return await BuildOpenPlanAsync(root, new List<KitDto> { kit }, isUpdate: false, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Подготовить «Обновить до последней версии» для выбранных документов рабочей папки
    /// (каждый — с комплектом своей последней версии). Сценарий 7.6: взять сборку, обновить деталь, сдать сборку.
    /// </summary>
    public async Task<OpenPlan> PrepareUpdateToLatestAsync(string workRoot, IEnumerable<Guid> documentIds,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        WorkspaceState.Load(root);
        var kits = new List<KitDto>();
        foreach (var id in documentIds.Distinct())
        {
            progress?.Report(new WorkProgress(WorkStage.Preparing, kits.Count, 0, "Получение последних версий"));
            var latest = (await Api.GetDocumentAsync(id, ct).ConfigureAwait(false)).Document.LatestVersion;
            kits.Add(await Api.GetKitAsync(id, latest, ct).ConfigureAwait(false));
        }
        if (kits.Count == 0) throw new WorkspaceException("Не выбраны документы для обновления.");
        return await BuildOpenPlanAsync(root, kits, isUpdate: true, progress, ct).ConfigureAwait(false);
    }

    private async Task<OpenPlan> BuildOpenPlanAsync(string root, List<KitDto> kits, bool isUpdate, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var state = WorkspaceState.TryLoad(root);
        var headKit = kits[0];
        var headItem = headKit.Items.FirstOrDefault(i => i.DocumentId == headKit.HeadDocumentId && i.Version == headKit.HeadVersion)
                       ?? throw new WorkspaceException("Сервер вернул комплект без головного документа. Сообщите администратору PDM.");

        var plan = new OpenPlan
        {
            WorkRoot = root,
            ProjectId = state?.ProjectId ?? headItem.ProjectId,
            HeadDocumentId = headItem.DocumentId,
            HeadVersion = headItem.Version,
            HeadRelPath = headItem.RelPath,
            HeadCad = headItem.Cad,
            IsUpdate = isUpdate,
            CreatesWorkspace = state == null,
        };
        plan.Kits.AddRange(kits);

        if (state != null && !isUpdate && state.ProjectId != headItem.ProjectId)
        {
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.PathTaken,
                $"Папка «{root}» уже привязана к другому проекту. Откройте {Texts.Doc(headItem.RelPath, headItem.Designation)} в отдельную рабочую папку."));
        }

        // Сведение комплектов: один документ — одна версия.
        var items = new Dictionary<Guid, KitItemDto>();
        var edges = new List<KitEdgeDto>();
        foreach (var kit in kits)
        {
            edges.AddRange(kit.Edges);
            foreach (var it in kit.Items)
            {
                if (items.TryGetValue(it.DocumentId, out var other))
                {
                    if (other.Version != it.Version)
                    {
                        plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.VersionConflict,
                            $"Документ {Texts.Doc(it.RelPath, it.Designation)} входит в комплект в двух версиях: v{Math.Min(other.Version, it.Version)} и v{Math.Max(other.Version, it.Version)}. " +
                            "В рабочей папке может лежать только одна версия файла — такой комплект открыть нельзя. Обновите зависимость в сборках, чтобы они ссылались на одну версию.",
                            it.RelPath, it.DocumentId));
                    }
                    continue;
                }
                items[it.DocumentId] = it;
            }
            foreach (var u in kit.Unresolved)
            {
                var parent = kit.Items.FirstOrDefault(i => i.DocumentId == u.ParentDocumentId && i.Version == u.ParentVersion);
                plan.Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.UnresolvedInKit,
                    $"В {Texts.Doc(parent?.RelPath ?? "?", parent?.Designation)} v{u.ParentVersion} есть ссылка на отсутствующий файл «{u.RefPath}» — он не учтён в PDM. CAD не найдёт этот компонент.",
                    parent?.RelPath, u.ParentDocumentId));
            }
        }

        var byPath = items.Values.GroupBy(i => RelPath.Key(i.RelPath)).Where(g => g.Count() > 1);
        foreach (var g in byPath)
        {
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.VersionConflict,
                $"Разные документы комплекта лежат по одному пути «{g.First().RelPath}». Открыть такой комплект нельзя — сообщите администратору PDM."));
        }

        var scan = state != null ? await ScanAsync(root, state, hashDrafts: false, progress, ct).ConfigureAwait(false) : null;

        foreach (var it in items.Values.OrderBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var full = RelPath.Combine(root, it.RelPath);
            var item = new OpenPlanItem
            {
                DocumentId = it.DocumentId,
                Version = it.Version,
                LatestVersion = it.LatestVersion,
                RelPath = it.RelPath,
                FullPath = full,
                Sha256 = it.Sha256,
                Size = it.Size,
                Cad = it.Cad,
                Kind = it.Kind,
                CadVersion = it.CadVersion,
                CadVersionMajor = it.CadVersionMajor,
                Designation = it.Designation,
                Name = it.Name,
                LockInfo = it.Lock,
                LockedByMe = it.Lock != null && it.Lock.UserId == me.Id,
                InCache = Cache.Contains(it.Sha256),
                Action = OpenAction.Place,
            };
            foreach (var e in edges.Where(e => e.ParentDocumentId == it.DocumentId && e.ParentVersion == it.Version))
            {
                if (!item.Deps.Any(d => d.DocumentId == e.ChildDocumentId && d.RefPath == e.RefPath))
                    item.Deps.Add(new WorkspaceDependency { DocumentId = e.ChildDocumentId, Version = e.ChildVersion, RefPath = e.RefPath });
            }
            var label = Texts.Doc(it.RelPath, it.Designation);

            // Документ уже лежит под другим путём.
            var docEntry = state?.FindByDocument(it.DocumentId);
            if (docEntry != null && RelPath.Key(docEntry.RelPath) != RelPath.Key(it.RelPath))
            {
                var old = scan!.Get(docEntry.RelPath);
                var modified = old != null && old.IsModified;
                item.MovedFromSha = old != null && old.Exists ? old.Sha : null;
                item.Conflicts.Add(new OpenConflict
                {
                    Kind = OpenConflictKind.MovedDocument,
                    RelPath = it.RelPath,
                    OldRelPath = docEntry.RelPath,
                    DocumentId = it.DocumentId,
                    LocalVersion = docEntry.Version,
                    NewVersion = it.Version,
                    DefaultResolution = modified ? ConflictResolution.Keep : ConflictResolution.Replace,
                    Resolution = modified ? ConflictResolution.Keep : ConflictResolution.Replace,
                    Message = modified
                        ? $"Документ {label} лежит в папке как «{docEntry.RelPath}» (v{docEntry.Version}) с несданными правками, а в комплекте v{it.Version} называется «{it.RelPath}». Заменить? Правки будут потеряны. По умолчанию — оставить."
                        : $"Документ {label} лежит в папке как «{docEntry.RelPath}» (v{docEntry.Version}), а в комплекте v{it.Version} называется «{it.RelPath}». Старый файл будет убран, новый — разложен.",
                });
                item.Action = OpenAction.Replace;
            }

            var lf = scan?.Get(it.RelPath);
            if (lf != null && lf.Exists)
            {
                if (lf.Entry != null)
                {
                    item.LocalVersion = lf.Entry.DocumentId == it.DocumentId ? lf.Entry.Version : null;
                    item.ExpectedLocalSha = lf.Sha;
                    if (lf.Entry.DocumentId == it.DocumentId && lf.Entry.Version == it.Version && !lf.IsModified)
                    {
                        if (item.Conflicts.Count == 0) item.Action = OpenAction.AlreadyInPlace;
                    }
                    else if (lf.IsModified)
                    {
                        var what = lf.Entry.DocumentId == it.DocumentId
                            ? (lf.Entry.Version == it.Version ? $"v{it.Version}" : $"v{lf.Entry.Version}, а комплекту нужна v{it.Version}")
                            : "другой документ";
                        item.Conflicts.Add(new OpenConflict
                        {
                            Kind = OpenConflictKind.LocalChanges,
                            RelPath = it.RelPath,
                            DocumentId = it.DocumentId,
                            LocalVersion = item.LocalVersion,
                            NewVersion = it.Version,
                            DefaultResolution = ConflictResolution.Keep,
                            Resolution = ConflictResolution.Keep,
                            Message = $"В файле «{it.RelPath}» есть несданные правки ({what}). По умолчанию файл остаётся как есть. «Заменить» — правки будут потеряны.",
                        });
                        item.Action = OpenAction.Replace;
                    }
                    else if (lf.Entry.DocumentId == it.DocumentId)
                    {
                        item.Conflicts.Add(new OpenConflict
                        {
                            Kind = OpenConflictKind.OtherVersion,
                            RelPath = it.RelPath,
                            DocumentId = it.DocumentId,
                            LocalVersion = lf.Entry.Version,
                            NewVersion = it.Version,
                            DefaultResolution = ConflictResolution.Replace,
                            Resolution = ConflictResolution.Replace,
                            Message = $"В папке лежит {label} v{lf.Entry.Version}. Заменить на v{it.Version}?",
                        });
                        item.Action = OpenAction.Replace;
                    }
                    else
                    {
                        item.Conflicts.Add(new OpenConflict
                        {
                            Kind = OpenConflictKind.OtherDocument,
                            RelPath = it.RelPath,
                            DocumentId = it.DocumentId,
                            NewVersion = it.Version,
                            DefaultResolution = ConflictResolution.Replace,
                            Resolution = ConflictResolution.Replace,
                            Message = $"По пути «{it.RelPath}» лежит другой документ (без правок). Заменить его документом {label} v{it.Version}?",
                        });
                        item.Action = OpenAction.Replace;
                    }
                }
                else
                {
                    var sha = await FileOps.HashAsync(lf.FullPath, ct).ConfigureAwait(false);
                    item.ExpectedLocalSha = sha;
                    if (sha == it.Sha256)
                    {
                        if (item.Conflicts.Count == 0) item.Action = OpenAction.Adopt;
                    }
                    else
                    {
                        item.Conflicts.Add(new OpenConflict
                        {
                            Kind = OpenConflictKind.UnknownFile,
                            RelPath = it.RelPath,
                            DocumentId = it.DocumentId,
                            NewVersion = it.Version,
                            DefaultResolution = ConflictResolution.Keep,
                            Resolution = ConflictResolution.Keep,
                            Message = $"В папке уже есть неучтённый файл «{it.RelPath}» с другим содержимым. По умолчанию он остаётся, и {label} v{it.Version} не раскладывается. «Заменить» — файл будет перезаписан.",
                        });
                        item.Action = OpenAction.Replace;
                    }
                }
            }
            else if (lf == null && state == null && File.Exists(full))
            {
                var sha = await FileOps.HashAsync(full, ct).ConfigureAwait(false);
                item.ExpectedLocalSha = sha;
                if (sha == it.Sha256) item.Action = OpenAction.Adopt;
                else
                {
                    item.Conflicts.Add(new OpenConflict
                    {
                        Kind = OpenConflictKind.UnknownFile,
                        RelPath = it.RelPath,
                        DocumentId = it.DocumentId,
                        NewVersion = it.Version,
                        DefaultResolution = ConflictResolution.Keep,
                        Resolution = ConflictResolution.Keep,
                        Message = $"В папке уже есть файл «{it.RelPath}» с другим содержимым. По умолчанию он остаётся. «Заменить» — файл будет перезаписан.",
                    });
                    item.Action = OpenAction.Replace;
                }
            }

            plan.Items.Add(item);
        }

        var working = await GetWorkingCadVersionsAsync(ct).ConfigureAwait(false);
        foreach (var it in plan.Items)
        {
            if (it.CadVersionMajor != null && working.TryGetValue(it.Cad, out var w) && it.CadVersionMajor > w)
            {
                plan.Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.NewerCadVersion,
                    $"{Texts.Doc(it.RelPath, it.Designation)} сохранён в {it.CadVersion}, новее рабочей версии ({w}): в рабочей версии CAD он не откроется.",
                    it.RelPath, it.DocumentId));
            }
        }
        return plan;
    }

    /// <summary>
    /// Выполнить открытие: докачать недостающее в кэш, разложить файлы (молча ничего не перезаписывается —
    /// только по решениям в <see cref="OpenConflict.Resolution"/>), выставить атрибуты, записать состояние.
    /// <paramref name="verifyInCad"/> — затем открыть головной документ в CAD и проверить источники компонентов.
    /// </summary>
    public async Task<OpenResult> ExecuteOpenAsync(OpenPlan plan, bool verifyInCad = false,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var result = new OpenResult { HeadFullPath = RelPath.Combine(plan.WorkRoot, plan.HeadRelPath) };
        if (!plan.CanExecute)
        {
            result.Problems.AddRange(plan.Problems.Where(p => p.IsBlocking));
            result.Message = "Открыть нельзя: есть ошибки в плане.";
            return result;
        }

        var root = plan.WorkRoot;
        Directory.CreateDirectory(root);
        var state = WorkspaceState.TryLoad(root);
        if (state == null)
        {
            state = new WorkspaceState { ProjectId = plan.ProjectId, ServerUrl = _options.ServerUrl };
        }
        else if (!plan.IsUpdate && state.ProjectId != plan.ProjectId)
        {
            throw new WorkspaceException($"Папка «{root}» уже привязана к другому проекту.");
        }

        // Повторная проверка: файл не должен был измениться после подготовки плана.
        var toWrite = new List<OpenPlanItem>();
        var toWriteSet = new HashSet<OpenPlanItem>();
        foreach (var item in plan.Items)
        {
            if (!item.WillWrite)
            {
                if (item.Action != OpenAction.AlreadyInPlace && item.Action != OpenAction.Adopt)
                    result.Kept.Add(item.RelPath);
                continue;
            }
            string? now = File.Exists(item.FullPath) ? await FileOps.HashAsync(item.FullPath, ct).ConfigureAwait(false) : null;
            if (now != null && now != item.ExpectedLocalSha)
            {
                result.Kept.Add(item.RelPath);
                result.Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.FileChanged,
                    $"Файл «{item.RelPath}» изменился после подготовки — оставлен как есть. Повторите открытие, чтобы решить, что с ним делать.",
                    item.RelPath, item.DocumentId));
                continue;
            }
            toWrite.Add(item);
            toWriteSet.Add(item);
        }

        result.Downloaded = await Cache.EnsureAsync(Api, toWrite.Select(i => new CacheObject(i.Sha256, i.Size)), progress, ct).ConfigureAwait(false);

        var n = 0;
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var write = toWriteSet.Contains(item);
            if (!write && item.Action != OpenAction.AlreadyInPlace && item.Action != OpenAction.Adopt) continue;

            if (write)
            {
                n++;
                progress?.Report(new WorkProgress(WorkStage.Placing, n, toWrite.Count, $"Раскладка «{item.FileName}»"));
                var moved = item.Conflicts.FirstOrDefault(c => c.Kind == OpenConflictKind.MovedDocument);
                if (moved?.OldRelPath != null)
                {
                    var oldFull = RelPath.Combine(root, moved.OldRelPath);
                    FileOps.Delete(oldFull);
                    state.Entries.RemoveAll(e => RelPath.Key(e.RelPath) == RelPath.Key(moved.OldRelPath));
                }
                FileOps.PlaceCopy(Cache.PathOf(item.Sha256), item.FullPath, readOnly: !item.LockedByMe);
                result.Placed.Add(item.RelPath);
            }
            else if (File.Exists(item.FullPath))
            {
                FileOps.SetReadOnly(item.FullPath, !item.LockedByMe);
            }

            state.Entries.RemoveAll(e => RelPath.Key(e.RelPath) == RelPath.Key(item.RelPath) || e.DocumentId == item.DocumentId);
            state.Entries.Add(new WorkspaceEntry
            {
                RelPath = item.RelPath,
                DocumentId = item.DocumentId,
                Version = item.Version,
                Sha256 = item.Sha256,
                Size = item.Size,
                Cad = item.Cad,
                Kind = item.Kind,
                Designation = item.Designation,
                Name = item.Name,
                Deps = item.Deps.ToList(),
                LockedByMe = item.LockedByMe,
                Stamp = FileOps.StampOf(item.FullPath),
            });
        }

        if (!plan.IsUpdate)
        {
            state.HeadDocumentId = plan.HeadDocumentId;
            state.HeadVersion = plan.HeadVersion;
        }
        state.Save(root);

        result.Success = true;
        result.Message = $"Разложено файлов: {result.Placed.Count}, скачано: {result.Downloaded}" +
                         (result.Kept.Count > 0 ? $", оставлено как есть: {result.Kept.Count}." : ".");

        if (verifyInCad)
        {
            progress?.Report(new WorkProgress(WorkStage.Verifying, 0, 0, "Открытие в CAD и проверка компонентов"));
            result.CadReport = await VerifyInCadAsync(plan, state, ct).ConfigureAwait(false);
            if (!result.CadReport.Ok)
            {
                foreach (var p in result.CadReport.Problems)
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.FileChanged, p));
                result.Message += " Проверка в CAD выявила проблемы.";
            }
        }
        progress?.Report(new WorkProgress(WorkStage.Done, 1, 1, result.Message));
        return result;
    }

    /// <summary>Открыть головной документ в CAD и проверить, что компоненты загружены из рабочей папки и нужных версий.</summary>
    private async Task<OpenReport> VerifyInCadAsync(OpenPlan plan, WorkspaceState state, CancellationToken ct)
    {
        var head = RelPath.Combine(plan.WorkRoot, plan.HeadRelPath);
        var adapter = AdapterFor(plan.HeadCad, head);
        OpenReport report;
        try
        {
            report = await adapter.OpenAndVerifyAsync(head, plan.WorkRoot, ct).ConfigureAwait(false);
        }
        catch (CadException ex)
        {
            return new OpenReport { HeadPath = head, Ok = false, Problems = { ex.Message } };
        }

        var expected = plan.Items.ToDictionary(i => i.DocumentId, i => i.Version);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in report.Configurations.SelectMany(c => c.Components))
        {
            if (!c.InsideWorkRoot || !seen.Add(c.LoadedPath)) continue;
            string rel;
            try
            {
                rel = RelPath.GetRelative(plan.WorkRoot, c.LoadedPath);
            }
            catch (ArgumentException)
            {
                continue;
            }
            var entry = state.FindByPath(rel);
            if (entry == null)
            {
                report.Problems.Add($"Компонент «{c.Name}» загружен из «{rel}» — этот файл не входит в комплект.");
            }
            else if (expected.TryGetValue(entry.DocumentId, out var v) && v != entry.Version)
            {
                report.Problems.Add($"Компонент «{c.Name}» загружен в версии v{entry.Version}, а комплект требует v{v} (локальный файл оставлен по вашему решению).");
            }
            else if (!expected.ContainsKey(entry.DocumentId))
            {
                report.Problems.Add($"Компонент «{c.Name}» загружен из «{rel}» — этот документ не входит в открываемый комплект.");
            }
        }
        report.Ok = report.Problems.Count == 0;
        return report;
    }
}
