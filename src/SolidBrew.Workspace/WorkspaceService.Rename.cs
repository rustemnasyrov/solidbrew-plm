using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Документ, последняя версия которого ссылается на переименовываемый.</summary>
public sealed class RenameParent
{
    public Guid DocumentId { get; init; }
    /// <summary>Путь в рабочей папке (для документа чужого проекта — с подпапкой проекта).</summary>
    public string RelPath { get; init; } = string.Empty;
    /// <summary>Собственный путь документа в хранилище.</summary>
    public string DocumentRelPath { get; init; } = string.Empty;
    /// <summary>Подпапка проекта документа чужого проекта; null — документ проекта рабочей папки.</summary>
    public string? ProjectFolder { get; init; }
    public string? Designation { get; init; }
    public string? Name { get; init; }
    public int LatestVersion { get; init; }
    public LockDto? LockInfo { get; init; }
    public bool LockedByMe { get; init; }
    public bool LockedByOther => LockInfo != null && !LockedByMe;
    /// <summary>Версия в рабочей папке (null — документа нет в папке; будет разложена последняя версия).</summary>
    public int? LocalVersion { get; init; }
    /// <summary>Пути ссылок на переименовываемый документ, как записаны в файле.</summary>
    public List<string> RefPaths { get; init; } = new();
    internal VersionDto Latest { get; init; } = new();
}

/// <summary>План переименования/перемещения документа.</summary>
public sealed class RenamePlan
{
    public string WorkRoot { get; init; } = string.Empty;
    public Guid DocumentId { get; init; }
    /// <summary>Текущий путь в рабочей папке.</summary>
    public string OldRelPath { get; init; } = string.Empty;
    /// <summary>Новый путь в рабочей папке (для документа чужого проекта — в той же подпапке проекта).</summary>
    public string NewRelPath { get; init; } = string.Empty;
    /// <summary>Новый собственный путь документа в хранилище (<see cref="NewRelPath"/> без подпапки проекта).</summary>
    public string NewDocumentRelPath { get; init; } = string.Empty;
    public int LatestVersion { get; init; }
    public bool LockedByMe { get; init; }
    public string? Designation { get; init; }
    /// <summary>Последние версии документов, которые ссылаются на переименовываемый, и кто их держит.</summary>
    public List<RenameParent> Parents { get; } = new();
    public List<PlanProblem> Problems { get; } = new();
    public bool CanExecute => !Problems.Any(p => p.IsBlocking);
    internal VersionDto Latest { get; init; } = new();

    public string Summary =>
        $"«{OldRelPath}» → «{NewRelPath}». Будут взяты в работу, исправлены и сданы ссылающиеся документы: {Parents.Count}" +
        (Parents.Count > 0 ? " (" + string.Join(", ", Parents.Select(p => Texts.Doc(p.RelPath, p.Designation))) + ")." : ".");
}

public sealed class RenameResult : OperationResult
{
    public List<CheckinResultItem> Published { get; } = new();
}

/// <summary>«Где применяется» в удобном виде.</summary>
public sealed class WhereUsedView
{
    public Guid DocumentId { get; init; }
    public int Version { get; init; }
    /// <summary>Применения запрошенной версии.</summary>
    public List<WhereUsedItemDto> ThisVersion { get; } = new();
    /// <summary>Применения других версий документа.</summary>
    public List<WhereUsedItemDto> OtherVersions { get; } = new();
    /// <summary>Все применения, где используется не последняя версия документа.</summary>
    public IEnumerable<WhereUsedItemDto> NotLatest => ThisVersion.Concat(OtherVersions).Where(i => !i.UsesLatestChild);
    /// <summary>Последние версии родителей (они и будут меняться при переименовании).</summary>
    public IEnumerable<WhereUsedItemDto> LatestParents => ThisVersion.Concat(OtherVersions).Where(i => i.IsParentLatest);
}

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Подготовить переименование/перемещение документа рабочей папки: какие последние версии документов ссылаются на него
    /// и кто их держит. Если хоть один занят другим — план невыполним.
    /// <paramref name="relPath"/> и <paramref name="newRelPath"/> — пути в рабочей папке; у документа чужого проекта новый путь
    /// без подпапки проекта считается путём внутри этой подпапки (проект документа не меняется).
    /// </summary>
    public async Task<RenamePlan> PrepareRenameAsync(string workRoot, string relPath, string newRelPath, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var entry = state.FindByPath(relPath) ?? throw new WorkspaceException(
            $"«{relPath}» не учтён в рабочей папке. Черновик (ещё не сданный файл) переименуйте в CAD или проводнике.");
        if (!RelPath.TryNormalize(newRelPath, out var newRel, out var err)) throw new WorkspaceException(err);
        if (entry.ProjectFolder != null && StripProjectFolder(entry.ProjectFolder, newRel) == newRel)
            newRel = LocalRelPath(entry.ProjectFolder, newRel);
        var newDocRel = StripProjectFolder(entry.ProjectFolder, newRel);

        var detail = await Api.GetDocumentAsync(entry.DocumentId, ct).ConfigureAwait(false);
        var latest = detail.Versions.FirstOrDefault(v => v.Number == detail.Document.LatestVersion) ?? detail.Versions.First();
        var label = Texts.Doc(entry.RelPath, entry.Designation);
        var plan = new RenamePlan
        {
            WorkRoot = root,
            DocumentId = entry.DocumentId,
            OldRelPath = entry.RelPath,
            NewRelPath = newRel,
            NewDocumentRelPath = newDocRel,
            LatestVersion = detail.Document.LatestVersion,
            LockedByMe = detail.Document.Lock?.UserId == me.Id,
            Designation = entry.Designation,
            Latest = latest,
        };

        if (RelPath.Key(newRel) == RelPath.Key(entry.RelPath))
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.PathTaken, "Новое имя совпадает с текущим."));
        if (File.Exists(RelPath.Combine(root, newRel)) || state.FindByPath(newRel) != null)
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.PathTaken, $"Файл «{newRel}» уже есть в рабочей папке. Выберите другое имя.", newRel));
        if (detail.Document.Lock != null && detail.Document.Lock.UserId != me.Id)
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LockedByOther,
                $"{label} взят в работу: {detail.Document.Lock.UserDisplayName}, с {Texts.Time(detail.Document.Lock.At)}. Переименование невозможно.", entry.RelPath, entry.DocumentId));
        if (entry.Version != detail.Document.LatestVersion)
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                $"В рабочей папке {label} v{entry.Version}, последняя — v{detail.Document.LatestVersion}. Сначала обновите документ.", entry.RelPath, entry.DocumentId));
        var full = RelPath.Combine(root, entry.RelPath);
        if (File.Exists(full) && await FileOps.HashAsync(full, ct).ConfigureAwait(false) != entry.LocalSha)
            plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LocalChanges,
                $"В {label} есть несданные правки. Сначала сдайте их или отмените, затем переименуйте.", entry.RelPath, entry.DocumentId));

        Func<Guid, string?>? folders = null;
        var whereUsed = await Api.GetWhereUsedAsync(entry.DocumentId, detail.Document.LatestVersion, ct).ConfigureAwait(false);
        foreach (var parentId in whereUsed.Items.Where(i => i.IsParentLatest && i.ParentDocumentId != entry.DocumentId).Select(i => i.ParentDocumentId).Distinct())
        {
            var pd = await Api.GetDocumentAsync(parentId, ct).ConfigureAwait(false);
            var pLatest = pd.Versions.FirstOrDefault(v => v.Number == pd.Document.LatestVersion) ?? pd.Versions.First();
            var pEntry = state.FindByDocument(parentId);
            var pProject = pd.Document.ProjectId;
            if (folders == null && pProject != state.ProjectId && pProject != Guid.Empty)
                folders = await ProjectFoldersAsync(state.ProjectId, new[] { pProject }, ct).ConfigureAwait(false);
            var pFolder = folders?.Invoke(pProject);
            var parent = new RenameParent
            {
                DocumentId = parentId,
                RelPath = LocalRelPath(pFolder, pd.Document.RelPath),
                DocumentRelPath = pd.Document.RelPath,
                ProjectFolder = pFolder,
                Designation = pd.Document.Designation,
                Name = pd.Document.Name,
                LatestVersion = pd.Document.LatestVersion,
                LockInfo = pd.Document.Lock,
                LockedByMe = pd.Document.Lock?.UserId == me.Id,
                LocalVersion = pEntry?.Version,
                RefPaths = pLatest.References.Where(r => r.ChildDocumentId == entry.DocumentId).Select(r => r.RefPath).ToList(),
                Latest = pLatest,
            };
            plan.Parents.Add(parent);
            var pLabel = Texts.Doc(parent.RelPath, parent.Designation);

            if (parent.LockedByOther)
                plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LockedByOther,
                    $"{pLabel} ссылается на {label} и взят в работу: {parent.LockInfo!.UserDisplayName}, с {Texts.Time(parent.LockInfo.At)}. Переименование невозможно, пока документ не сдан.", parent.RelPath, parentId));
            if (pEntry != null)
            {
                var pFull = RelPath.Combine(root, pEntry.RelPath);
                if (File.Exists(pFull) && await FileOps.HashAsync(pFull, ct).ConfigureAwait(false) != pEntry.LocalSha)
                    plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LocalChanges,
                        $"В {pLabel} есть несданные правки. Сначала сдайте или отмените их — переименование сдаёт этот документ.", pEntry.RelPath, parentId));
                if (RelPath.Key(pEntry.RelPath) != RelPath.Key(parent.RelPath))
                    plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                        $"{pLabel} лежит в папке под старым именем «{pEntry.RelPath}». Сначала обновите его до последней версии.", pEntry.RelPath, parentId));
            }
            else
            {
                var at = state.FindByPath(parent.RelPath);
                if (at != null || File.Exists(RelPath.Combine(root, parent.RelPath)))
                    plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.PathTaken,
                        $"Путь «{parent.RelPath}» для {pLabel} занят другим файлом рабочей папки.", parent.RelPath, parentId));
            }
        }
        return plan;
    }

    /// <summary>
    /// Выполнить переименование: взять ссылающиеся (при неудаче — откатить взятые), переписать в них ссылки адаптером,
    /// одной сдачей опубликовать переименованный (новая версия с новым путём) и ссылающиеся. Затем обновить файлы в папке.
    /// При ошибке файлы в рабочей папке не меняются.
    /// </summary>
    public async Task<RenameResult> ExecuteRenameAsync(RenamePlan plan, string? comment = null, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var result = new RenameResult();
        if (!plan.CanExecute)
        {
            result.Problems.AddRange(plan.Problems.Where(p => p.IsBlocking));
            result.Message = "Переименование невозможно.";
            return result;
        }

        var root = plan.WorkRoot;
        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var lockedNow = new List<Guid>();
        var preLocked = new HashSet<Guid>();
        // Копии ссылающихся документов правятся рядом с их местом в рабочей папке (скрытые временные файлы):
        // так адаптер правильно строит относительные ссылки. При ошибке они удаляются, сами файлы не трогаются.
        var tempCopies = new List<string>();
        var success = false;
        try
        {
            // 1. Взять в работу.
            var all = new List<(Guid Id, int Latest, string RelPath)> { (plan.DocumentId, plan.LatestVersion, plan.OldRelPath) };
            all.AddRange(plan.Parents.Select(p => (p.DocumentId, p.LatestVersion, p.RelPath)));
            var n = 0;
            foreach (var (id, latest, rel) in all)
            {
                n++;
                progress?.Report(new WorkProgress(WorkStage.Locking, n, all.Count, $"Взятие в работу «{Texts.FileNameOf(rel)}»"));
                var doc = (await Api.GetDocumentAsync(id, ct).ConfigureAwait(false)).Document;
                if (doc.Lock != null && doc.Lock.UserId == me.Id)
                {
                    preLocked.Add(id);
                }
                else
                {
                    try
                    {
                        await Api.LockAsync(id, ct).ConfigureAwait(false);
                        lockedNow.Add(id);
                    }
                    catch (PdmApiException ex)
                    {
                        var who = ex.Error?.Lock is { } l ? $" Документ взят: {l.UserDisplayName}, с {Texts.Time(l.At)}." : string.Empty;
                        result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LockedByOther,
                            $"Не удалось взять в работу «{rel}»: {ex.Message}{who} Переименование отменено.", rel, id));
                        return Fail(result, "Переименование не выполнено: ссылающийся документ занят.");
                    }
                }
                var after = (await Api.GetDocumentAsync(id, ct).ConfigureAwait(false)).Document;
                if (after.LatestVersion != latest)
                {
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                        $"«{rel}» изменился на сервере (v{after.LatestVersion}). Подготовьте переименование заново.", rel, id));
                    return Fail(result, "Переименование не выполнено: документы изменились.");
                }
            }

            // 2. Содержимое: из кэша, ссылки переписываются в копиях.
            var objects = new List<CacheObject> { new(plan.Latest.Sha256, plan.Latest.Size) };
            objects.AddRange(plan.Parents.Select(p => new CacheObject(p.Latest.Sha256, p.Latest.Size)));
            await Cache.EnsureAsync(Api, objects, progress, ct).ConfigureAwait(false);

            var newFull = RelPath.Combine(root, plan.NewRelPath);
            var parentIds = new HashSet<Guid>(plan.Parents.Select(p => p.DocumentId)) { plan.DocumentId };
            var request = new CheckinRequest
            {
                ProjectId = WorkspaceState.Load(root).ProjectId,
                Comment = string.IsNullOrWhiteSpace(comment) ? $"Переименование «{plan.OldRelPath}» → «{plan.NewRelPath}»" : comment.Trim(),
                KeepLocks = preLocked.Count > 0,
                AcceptNewerCadVersion = true,
            };

            CheckinReference MapPublished(VersionRefDto r) =>
                r.ChildDocumentId == null ? new CheckinReference { RefPath = r.RefPath }
                : parentIds.Contains(r.ChildDocumentId.Value) && r.ChildVersion == r.ChildLatestVersion
                    ? new CheckinReference { RefPath = r.RefPath, DocumentId = r.ChildDocumentId }
                    : new CheckinReference { RefPath = r.RefPath, DocumentId = r.ChildDocumentId, Version = r.ChildVersion };

            var renamedItem = ItemFromVersion(plan.Latest, plan.DocumentId, string.IsNullOrEmpty(plan.NewDocumentRelPath) ? plan.NewRelPath : plan.NewDocumentRelPath);
            renamedItem.References = plan.Latest.References.Where(r => r.ChildDocumentId != plan.DocumentId).Select(MapPublished).ToList();
            if (renamedItem.References.Any(r => r.DocumentId == null && r.TempKey == null)) request.AllowUnresolvedReferences = true;
            request.Items.Add(renamedItem);

            var stagedParents = new Dictionary<Guid, (string File, string Sha, long Size)>();
            foreach (var p in plan.Parents)
            {
                var pFinal = RelPath.Combine(root, p.RelPath);
                var copy = Path.Combine(Path.GetDirectoryName(pFinal)!, ".~sb" + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + Path.GetFileName(pFinal));
                tempCopies.Add(copy);
                FileOps.PlaceCopy(Cache.PathOf(p.Latest.Sha256), copy, readOnly: false);
                var adapter = AdapterFor(p.Latest.Cad, p.RelPath);
                var map = p.RefPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(r => r, _ => newFull, StringComparer.OrdinalIgnoreCase);
                await adapter.ReplaceReferencesAsync(copy, map, ct).ConfigureAwait(false);
                var info = await adapter.ReadAsync(copy, ct).ConfigureAwait(false);
                var sha = await FileOps.HashAsync(copy, ct).ConfigureAwait(false);
                var size = new FileInfo(copy).Length;
                stagedParents[p.DocumentId] = (copy, sha, size);

                var item = ItemFromVersion(p.Latest, p.DocumentId, string.IsNullOrEmpty(p.DocumentRelPath) ? p.RelPath : p.DocumentRelPath);
                item.Sha256 = sha;
                item.Size = size;
                item.CadVersion = info.CadVersion ?? item.CadVersion;
                item.CadVersionMajor = info.CadVersionMajor ?? item.CadVersionMajor;
                item.Configurations = info.Configurations;
                item.Properties = info.Properties;
                item.References = new List<CheckinReference>();
                // Ссылки, оставшиеся прежними, сопоставляются с опубликованными; переписанные адаптером — это переименованный документ.
                var untouched = p.Latest.References.Where(x => x.ChildDocumentId != plan.DocumentId).ToList();
                foreach (var r in info.References)
                {
                    var published = untouched.FirstOrDefault(x => string.Equals(x.RefPath, r.SavedPath, StringComparison.OrdinalIgnoreCase));
                    if (published == null && p.RefPaths.Contains(r.SavedPath, StringComparer.OrdinalIgnoreCase))
                        throw new WorkspaceException($"Не удалось исправить ссылку «{r.SavedPath}» в «{p.RelPath}». Переименование отменено.");
                    if (published == null)
                    {
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath, DocumentId = plan.DocumentId });
                        continue;
                    }
                    if (published == null || published.ChildDocumentId == null)
                    {
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath });
                        request.AllowUnresolvedReferences = true;
                    }
                    else item.References.Add(MapPublished(published));
                }
                request.Items.Add(item);
            }

            // 3. Сдача.
            try
            {
                await UploadMissingAsync(stagedParents.Values.Select(s => (s.Sha, s.Size, s.File)), progress, ct).ConfigureAwait(false);
                progress?.Report(new WorkProgress(WorkStage.Publishing, 0, 1, "Публикация"));
                var response = await Api.CheckinAsync(request, ct).ConfigureAwait(false);
                result.Published.AddRange(response.Items);
            }
            catch (PdmApiException ex)
            {
                result.Problems.AddRange(PlanProblem.FromServer(ex));
                return Fail(result, "Переименование не выполнено, ничего не опубликовано: " + ex.Message);
            }
            success = true;

            // 4. Рабочая папка.
            var state = WorkspaceState.Load(root);
            var keep = request.KeepLocks;
            int VersionOf(Guid id) => result.Published.First(x => x.DocumentId == id).Version;
            List<WorkspaceDependency> DepsOf(CheckinItem item) => item.References.Where(r => r.DocumentId != null)
                .Select(r => new WorkspaceDependency { DocumentId = r.DocumentId!.Value, Version = r.Version ?? VersionOf(r.DocumentId.Value), RefPath = r.RefPath }).ToList();

            var oldFull = RelPath.Combine(root, plan.OldRelPath);
            var renamedLocked = keep && preLocked.Contains(plan.DocumentId);
            if (File.Exists(oldFull))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                FileOps.SetReadOnly(oldFull, false);
                File.Move(oldFull, newFull);
                FileOps.SetReadOnly(newFull, !renamedLocked);
            }
            else
            {
                FileOps.PlaceCopy(Cache.PathOf(plan.Latest.Sha256), newFull, !renamedLocked);
            }
            var renamedEntry = state.FindByDocument(plan.DocumentId)!;
            renamedEntry.RelPath = plan.NewRelPath;
            renamedEntry.Version = VersionOf(plan.DocumentId);
            // Байты переименованного не менялись: пути ссылок — как сейчас в файле (возможно, уже переписаны на рабочую папку).
            var oldDeps = renamedEntry.Deps;
            renamedEntry.Deps = DepsOf(renamedItem);
            foreach (var d in renamedEntry.Deps)
            {
                var local = oldDeps.FirstOrDefault(o => o.DocumentId == d.DocumentId);
                if (local != null) d.RefPath = local.RefPath;
            }
            renamedEntry.LockedByMe = renamedLocked;
            renamedEntry.Stamp = FileOps.StampOf(newFull);

            foreach (var p in plan.Parents)
            {
                var (file, sha, size) = stagedParents[p.DocumentId];
                var pLocked = keep && preLocked.Contains(p.DocumentId);
                var pFull = RelPath.Combine(root, p.RelPath);
                await Cache.AddFileAsync(file, sha, ct).ConfigureAwait(false);
                if (File.Exists(pFull)) FileOps.SetReadOnly(pFull, false);
                File.Move(file, pFull, overwrite: true);
                FileOps.SetReadOnly(pFull, !pLocked);
                state.Entries.RemoveAll(e => e.DocumentId == p.DocumentId || RelPath.Key(e.RelPath) == RelPath.Key(p.RelPath));
                state.Entries.Add(new WorkspaceEntry
                {
                    RelPath = p.RelPath,
                    ProjectFolder = p.ProjectFolder,
                    DocumentId = p.DocumentId,
                    Version = VersionOf(p.DocumentId),
                    Sha256 = sha,
                    Size = size,
                    Cad = p.Latest.Cad,
                    Kind = p.Latest.Kind,
                    Designation = p.Designation,
                    Name = p.Name,
                    Deps = DepsOf(request.Items.First(i => i.DocumentId == p.DocumentId)),
                    LockedByMe = pLocked,
                    Stamp = FileOps.StampOf(pFull),
                });
            }
            state.Save(root);

            // Ссылающиеся документы положены из опубликованных байтов: остальные их ссылки — на пути рабочей папки.
            var relinkIds = new HashSet<Guid>(plan.Parents.Select(p => p.DocumentId)) { plan.DocumentId };
            var rewritten = await RewriteReferencesAsync(root, state, state.Entries.Where(e => relinkIds.Contains(e.DocumentId)).ToList(), null, result.Problems, progress, ct).ConfigureAwait(false);
            if (rewritten.Count > 0 || result.Problems.Count > 0) state.Save(root);

            // Блокировки, взятые только ради переименования, снимаются (при KeepLocks сервер их оставил).
            if (keep)
            {
                foreach (var id in lockedNow) await TryUnlockAsync(id, ct).ConfigureAwait(false);
            }
            result.Success = true;
            result.Message = $"Переименовано: «{plan.OldRelPath}» → «{plan.NewRelPath}». Сдано документов: {result.Published.Count}.";
            return result;
        }
        finally
        {
            if (!success)
            {
                foreach (var id in lockedNow) await TryUnlockAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            foreach (var t in tempCopies) FileOps.TryDelete(t);
        }
    }

    private static RenameResult Fail(RenameResult r, string message)
    {
        r.Success = false;
        r.Message = message;
        return r;
    }

    private async Task TryUnlockAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await Api.UnlockAsync(id, ct).ConfigureAwait(false);
        }
        catch (PdmApiException)
        {
        }
    }

    private static CheckinItem ItemFromVersion(VersionDto v, Guid documentId, string relPath) => new()
    {
        DocumentId = documentId,
        BaseVersion = v.Number,
        RelPath = relPath,
        Sha256 = v.Sha256,
        Size = v.Size,
        Cad = v.Cad,
        Kind = v.Kind,
        CadVersion = v.CadVersion,
        CadVersionMajor = v.CadVersionMajor,
        Configurations = v.Configurations,
        Properties = v.Properties,
    };

    // ------------------------------------------------------------------ прочее

    /// <summary>Пометить документ «не используется» (или вернуть).</summary>
    public Task<DocumentSummaryDto> MarkUnusedAsync(Guid documentId, bool unused, CancellationToken ct = default) =>
        Api.MarkUnusedAsync(documentId, unused, ct);

    /// <summary>Выгрузить комплект версии в ZIP-файл (запись через временный файл).</summary>
    public async Task ExportAsync(Guid documentId, int version, bool includeDerived, string destinationFile,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.GetFullPath(destinationFile);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var temp = dest + ".part";
        progress?.Report(new WorkProgress(WorkStage.Exporting, 0, 0, "Выгрузка комплекта"));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await Api.ExportAsync(documentId, version, includeDerived, stream, ct).ConfigureAwait(false);
            }
            File.Move(temp, dest, overwrite: true);
        }
        catch
        {
            FileOps.TryDelete(temp);
            throw;
        }
        progress?.Report(new WorkProgress(WorkStage.Done, 1, 1, $"Комплект выгружен: {dest}"));
    }

    /// <summary>«Где применяется» версии документа: отдельно применения этой версии и других, отметка «не последняя».</summary>
    public async Task<WhereUsedView> GetWhereUsedAsync(Guid documentId, int version, CancellationToken ct = default)
    {
        var dto = await Api.GetWhereUsedAsync(documentId, version, ct).ConfigureAwait(false);
        var view = new WhereUsedView { DocumentId = documentId, Version = version };
        foreach (var i in dto.Items.OrderBy(i => i.ParentRelPath, StringComparer.OrdinalIgnoreCase).ThenByDescending(i => i.ParentVersion))
            (i.ChildVersion == version ? view.ThisVersion : view.OtherVersions).Add(i);
        return view;
    }

    /// <summary>
    /// Дерево комплекта для экрана «Комплект» с состояниями из рабочей папки (если задана).
    /// <paramref name="includeDrawings"/> — с чертежами документов комплекта (под моделями, см. <see cref="KitTreeBuilder"/>).
    /// </summary>
    public async Task<KitTreeNode> GetKitTreeAsync(Guid documentId, int version, string? workRoot = null, CancellationToken ct = default,
        bool includeDrawings = false)
    {
        var kit = await Api.GetKitAsync(documentId, version, includeDrawings, ct).ConfigureAwait(false);
        var me = await GetMeAsync(ct).ConfigureAwait(false);
        WorkspaceStatus? status = null;
        if (workRoot != null && IsWorkspace(workRoot))
            status = await GetStatusAsync(workRoot, queryServer: false, ct: ct).ConfigureAwait(false);
        return KitTreeBuilder.Build(kit, status, me.Id);
    }
}
