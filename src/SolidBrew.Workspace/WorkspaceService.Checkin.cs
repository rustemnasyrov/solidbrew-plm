using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Разрешить ссылку файла рабочей папки: (1) абсолютный путь внутри корня; (2) относительный от папки документа;
    /// (3) по имени файла среди файлов рабочей папки — один файл или «неоднозначно» / «отсутствует».
    /// </summary>
    internal static ResolvedReference ResolveInWorkspace(LocalScan scan, string documentFullPath, string savedPath)
    {
        var normalized = savedPath.Replace('/', '\\');
        string? candidatePath = null;
        var how = ReferenceResolution.ByAbsolutePath;
        try
        {
            if (Path.IsPathFullyQualified(normalized))
            {
                candidatePath = Path.GetFullPath(normalized);
            }
            else
            {
                candidatePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentFullPath)!, normalized));
                how = ReferenceResolution.ByRelativePath;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            candidatePath = null;
        }

        if (candidatePath != null)
        {
            string? rel = null;
            try
            {
                rel = RelPath.GetRelative(scan.Root, candidatePath);
            }
            catch (ArgumentException)
            {
            }
            if (rel != null)
            {
                var f = scan.Get(rel);
                if (f != null && (f.Exists || f.Entry != null))
                    return new ResolvedReference { SavedPath = savedPath, Resolution = how, TargetRelPath = f.RelPath };
            }
        }

        var name = CadReference.FileNameOf(savedPath);
        var byName = scan.ByFileName(name).ToList();
        if (byName.Count == 1)
            return new ResolvedReference { SavedPath = savedPath, Resolution = ReferenceResolution.ByFileName, TargetRelPath = byName[0].RelPath };
        if (byName.Count > 1)
            return new ResolvedReference { SavedPath = savedPath, Resolution = ReferenceResolution.Ambiguous, Candidates = byName.Select(b => b.RelPath).OrderBy(p => p).ToList() };
        return new ResolvedReference { SavedPath = savedPath, Resolution = ReferenceResolution.Missing };
    }

    /// <summary>
    /// Подготовить сдачу: кандидаты — взятые мной и изменённые (по байтам или по привязкам зависимостей), взятые без изменений
    /// (не отмечены) и черновики. Адаптер читает ссылки и свойства; ссылки разрешаются в рабочей папке.
    /// </summary>
    public async Task<CheckinPlan> PrepareCheckinAsync(string workRoot, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var scan = await ScanAsync(root, state, hashDrafts: true, progress, ct).ConfigureAwait(false);
        if (scan.StateDirty) state.Save(root);

        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var working = await GetWorkingCadVersionsAsync(ct).ConfigureAwait(false);
        var plan = new CheckinPlan { WorkRoot = root, ProjectId = state.ProjectId, WorkingCadVersions = working };

        foreach (var f in scan.Files.Values)
        {
            if (f.Entry == null && !f.Exists) continue;
            plan.Targets[RelPath.Key(f.RelPath)] = new CheckinPlan.TargetInfo
            {
                RelPath = f.RelPath,
                Entry = f.Entry,
                IsDraft = f.IsDraft,
                ContentModified = f.IsModified,
                HasAdapter = f.Adapter != null,
            };
        }

        var lockedEntries = scan.Files.Values.Where(f => f.Entry != null && f.Entry.LockedByMe && f.Exists).ToList();
        var drafts = scan.Files.Values.Where(f => f.IsDraft).ToList();

        // Блокировки на сервере: не сняли ли принудительно.
        var server = await FetchSummariesAsync(state.ProjectId, lockedEntries.Select(e => e.Entry!.DocumentId), ct).ConfigureAwait(false);

        var toRead = lockedEntries.Concat(drafts).ToList();
        var index = 0;
        foreach (var f in toRead.OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new WorkProgress(WorkStage.Reading, index, toRead.Count, $"Чтение ссылок «{Texts.FileNameOf(f.RelPath)}»"));
            var adapter = f.Adapter;
            var own = new List<PlanProblem>();
            CadDocumentInfo? info = null;
            if (adapter == null)
            {
                own.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.UnsupportedFile,
                    $"Нет адаптера CAD для «{f.RelPath}» — прочитать ссылки нельзя.", f.RelPath, f.Entry?.DocumentId));
            }
            else
            {
                try
                {
                    info = await adapter.ReadAsync(f.FullPath, ct).ConfigureAwait(false);
                }
                catch (CadException ex)
                {
                    own.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.ReadError,
                        $"Не удалось прочитать «{f.RelPath}»: {ex.Message}", f.RelPath, f.Entry?.DocumentId));
                }
            }

            var refs = info?.References.Select(r => ResolveInWorkspace(scan, f.FullPath, r.SavedPath)).ToList() ?? new List<ResolvedReference>();
            var e = f.Entry;
            bool refsChanged = false;
            if (e != null && info != null)
            {
                var now = new HashSet<(Guid, int)>();
                foreach (var r in refs.Where(r => r.TargetRelPath != null))
                {
                    var t = scan.Get(r.TargetRelPath!);
                    if (t?.Entry != null)
                    {
                        if (t.Entry.DocumentId != e.DocumentId) now.Add((t.Entry.DocumentId, t.Entry.Version));
                    }
                    else refsChanged = true;
                }
                var before = new HashSet<(Guid, int)>(e.Deps.Select(d => (d.DocumentId, d.Version)));
                refsChanged |= !now.SetEquals(before);
            }

            var lockLost = false;
            if (e != null && server.TryGetValue(e.DocumentId, out var doc))
            {
                if (doc.Lock == null || doc.Lock.UserId != me.Id)
                {
                    lockLost = true;
                    var who = doc.Lock != null ? $" Сейчас документ взят: {doc.Lock.UserDisplayName}, с {Texts.Time(doc.Lock.At)}." : string.Empty;
                    own.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LockLost,
                        $"Блокировку {Texts.Doc(f.RelPath, e.Designation)} сняли принудительно — сдать его нельзя.{who} Правки сохранены в файле.", f.RelPath, e.DocumentId));
                }
                else if (doc.LatestVersion != e.Version)
                {
                    own.Add(new PlanProblem(ProblemSeverity.Error, ProblemCodes.StaleBaseVersion,
                        $"{Texts.Doc(f.RelPath, e.Designation)}: на сервере уже v{doc.LatestVersion}, а правка шла от v{e.Version}. Сохраните правки отдельно и обновите документ.", f.RelPath, e.DocumentId));
                }
            }

            var relOk = RelPath.TryNormalize(f.RelPath, out _, out var relError);
            if (!relOk) own.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.InvalidPath, relError, f.RelPath));

            var contentChanged = e != null && f.IsModified;
            var designation = info != null ? DocumentProperties.Designation(info.Properties) : e?.Designation;
            var isNewer = info?.CadVersionMajor != null && adapter != null && working.TryGetValue(adapter.CadId, out var wv) && info.CadVersionMajor > wv;
            var reason = e == null ? "новый документ"
                : contentChanged && refsChanged ? "изменён, изменены привязки зависимостей"
                : contentChanged ? "изменён"
                : refsChanged ? "новая привязка зависимостей (байты файла не изменились)"
                : "взят в работу, не изменён";

            var cand = new CheckinCandidate
            {
                RelPath = f.RelPath,
                DocumentRelPath = e?.DocumentRelPath ?? f.RelPath,
                ProjectFolder = e?.ProjectFolder,
                FullPath = f.FullPath,
                IsDraft = e == null,
                DocumentId = e?.DocumentId,
                BaseVersion = e?.Version,
                TempKey = e == null ? f.RelPath : null,
                ContentChanged = contentChanged,
                ReferencesChanged = refsChanged,
                Reason = reason,
                Cad = adapter?.CadId ?? e?.Cad ?? string.Empty,
                Kind = adapter?.KindOf(f.FullPath) ?? e?.Kind ?? DocumentKind.Other,
                CadVersion = info?.CadVersion,
                CadVersionMajor = info?.CadVersionMajor,
                IsNewerCadVersion = isNewer,
                Designation = designation,
                Name = info != null ? DocumentProperties.Name(info.Properties) : e?.Name,
                Sha256 = f.Sha ?? string.Empty,
                Size = f.Size,
                Configurations = info?.Configurations ?? new List<string>(),
                Properties = info?.Properties ?? new Dictionary<string, Dictionary<string, string>>(),
                References = refs,
            };
            cand.OwnProblems.AddRange(own);
            cand.Include = (e == null || contentChanged || refsChanged) && !lockLost;
            plan.Items.Add(cand);
        }

        plan.Revalidate();
        return plan;
    }

    /// <summary>
    /// Сдать отмеченные документы одной атомарной операцией. При ошибке локально ничего не меняется
    /// (правки и блокировки целы), ошибки сервера — в <see cref="OperationResult.Problems"/> и <see cref="CheckinPlan.ServerProblems"/>.
    /// </summary>
    public async Task<CheckinResult> ExecuteCheckinAsync(CheckinPlan plan, string? comment, bool keepLocks = false, bool acceptNewerCad = false,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        var result = new CheckinResult();
        plan.Revalidate();
        plan.ServerProblems.Clear();
        if (!plan.CanExecute)
        {
            result.Problems.AddRange(plan.Problems.Where(p => p.IsBlocking));
            result.Message = plan.Included.Any() ? "Сдача невозможна: есть блокирующие ошибки." : "Нечего сдавать: не отмечено ни одного документа.";
            return result;
        }
        if (plan.HasNewerCadVersion && !acceptNewerCad)
        {
            result.Problems.AddRange(plan.Problems.Where(p => p.Code == LocalProblemCodes.NewerCadVersion));
            result.Message = "Есть файлы, сохранённые в версии CAD новее рабочей. Подтвердите сдачу таких файлов.";
            return result;
        }

        var root = plan.WorkRoot;
        var included = plan.Included.ToList();
        var staging = Cache.CreateStagingFolder();
        try
        {
            // 1. Снимок файлов: сдаётся ровно то, что было прочитано при подготовке.
            var staged = new Dictionary<CheckinCandidate, string>();
            var i = 0;
            foreach (var c in included)
            {
                i++;
                progress?.Report(new WorkProgress(WorkStage.Hashing, i, included.Count, $"Проверка «{c.FileName}»"));
                var copy = Path.Combine(staging, c.RelPath.Replace('/', '\\'));
                var (sha, _) = await FileOps.CopyForReadingAsync(c.FullPath, copy, ct).ConfigureAwait(false);
                if (sha != c.Sha256)
                {
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.FileChanged,
                        $"Файл «{c.RelPath}» изменился после подготовки сдачи. Сохраните работу в CAD и подготовьте сдачу заново.", c.RelPath, c.DocumentId));
                }
                staged[c] = copy;
            }
            if (result.Problems.Count > 0)
            {
                result.Message = "Сдача не выполнена: файлы изменились после подготовки.";
                return result;
            }

            // 2. Недостающие объекты.
            var request = BuildCheckinRequest(plan, included, comment, keepLocks, acceptNewerCad);
            try
            {
                await UploadMissingAsync(included.Select(c => (c.Sha256, c.Size, staged[c])), progress, ct).ConfigureAwait(false);
                progress?.Report(new WorkProgress(WorkStage.Publishing, 0, 1, "Публикация версий"));
                result.Response = await Api.CheckinAsync(request, ct).ConfigureAwait(false);
            }
            catch (PdmApiException ex)
            {
                var problems = PlanProblem.FromServer(ex);
                plan.ServerProblems.AddRange(problems);
                result.Problems.AddRange(problems);
                result.Message = "Сдача не выполнена, ничего не опубликовано: " + ex.Message;
                return result;
            }

            // 3. Успех: состояние, атрибуты, кэш.
            await ApplyCheckinAsync(plan, included, staged, result.Response, keepLocks, result, ct).ConfigureAwait(false);
            result.Success = true;
            result.Message = $"Сдано документов: {result.Published.Count}." + (keepLocks ? " Документы остаются в работе." : string.Empty);
            progress?.Report(new WorkProgress(WorkStage.Done, 1, 1, result.Message));
            return result;
        }
        finally
        {
            FileOps.TryDeleteDirectory(staging);
        }
    }

    private static CheckinRequest BuildCheckinRequest(CheckinPlan plan, List<CheckinCandidate> included, string? comment, bool keepLocks, bool acceptNewerCad)
    {
        var request = new CheckinRequest
        {
            ProjectId = plan.ProjectId,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            KeepLocks = keepLocks,
            AcceptNewerCadVersion = acceptNewerCad,
        };
        foreach (var c in included)
        {
            request.Items.Add(new CheckinItem
            {
                DocumentId = c.IsDraft ? null : c.DocumentId,
                TempKey = c.IsDraft ? c.TempKey : null,
                BaseVersion = c.IsDraft ? null : c.BaseVersion,
                RelPath = c.DocumentRelPath,
                Sha256 = c.Sha256,
                Size = c.Size,
                Cad = c.Cad,
                Kind = c.Kind,
                CadVersion = c.CadVersion,
                CadVersionMajor = c.CadVersionMajor,
                Configurations = c.Configurations,
                Properties = c.Properties,
                References = c.References.Where(r => r.TargetRelPath == null || RelPath.Key(r.TargetRelPath) != RelPath.Key(c.RelPath))
                    .Select(plan.ToCheckinReference).ToList(),
            });
        }
        return request;
    }

    /// <summary>Загрузить на сервер объекты, которых там нет.</summary>
    internal async Task UploadMissingAsync(IEnumerable<(string Sha, long Size, string File)> objects, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var all = objects.GroupBy(o => o.Sha).Select(g => g.First()).ToList();
        progress?.Report(new WorkProgress(WorkStage.Uploading, 0, 0, "Проверка, каких файлов нет на сервере"));
        var missing = new HashSet<string>(await Api.GetMissingObjectsAsync(all.Select(o => o.Sha), ct).ConfigureAwait(false));
        var toUpload = all.Where(o => missing.Contains(o.Sha)).ToList();
        long total = toUpload.Sum(o => o.Size), done = 0;
        var n = 0;
        foreach (var o in toUpload)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var baseDone = done;
            var msg = $"Загрузка {n} из {toUpload.Count}: «{Path.GetFileName(o.File)}» ({Texts.Bytes(o.Size)})";
            progress?.Report(new WorkProgress(WorkStage.Uploading, done, total, msg));
            await using var stream = new FileStream(o.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, useAsync: true);
            var inner = progress == null ? null : new SyncProgress<long>(b => progress.Report(new WorkProgress(WorkStage.Uploading, baseDone + b, total, msg)));
            await Api.UploadObjectAsync(o.Sha, stream, o.Size, inner, ct).ConfigureAwait(false);
            done += o.Size;
        }
    }

    private async Task ApplyCheckinAsync(CheckinPlan plan, List<CheckinCandidate> included, Dictionary<CheckinCandidate, string> staged,
        CheckinResponse response, bool keepLocks, CheckinResult result, CancellationToken ct)
    {
        var root = plan.WorkRoot;
        var state = WorkspaceState.Load(root);

        var newVersions = new Dictionary<string, CheckinResultItem>(StringComparer.Ordinal); // ключ — RelPath.Key кандидата
        foreach (var c in included)
        {
            var r = c.IsDraft
                ? response.Items.FirstOrDefault(x => x.TempKey == c.TempKey)
                : response.Items.FirstOrDefault(x => x.DocumentId == c.DocumentId);
            if (r == null)
                throw new WorkspaceException($"Сервер не вернул версию для «{c.RelPath}». Сдача выполнена, но состояние папки не обновлено — откройте комплект заново.");
            newVersions[RelPath.Key(c.RelPath)] = r;
            result.Published.Add(r);
        }

        foreach (var c in included)
        {
            var r = newVersions[RelPath.Key(c.RelPath)];
            var deps = new List<WorkspaceDependency>();
            foreach (var reference in c.References)
            {
                if (reference.TargetRelPath == null || RelPath.Key(reference.TargetRelPath) == RelPath.Key(c.RelPath)) continue;
                if (newVersions.TryGetValue(RelPath.Key(reference.TargetRelPath), out var nv))
                {
                    deps.Add(new WorkspaceDependency { DocumentId = nv.DocumentId, Version = nv.Version, RefPath = reference.SavedPath });
                }
                else
                {
                    var t = state.FindByPath(reference.TargetRelPath);
                    if (t != null) deps.Add(new WorkspaceDependency { DocumentId = t.DocumentId, Version = t.Version, RefPath = reference.SavedPath });
                }
            }

            var lockedNow = keepLocks;
            if (keepLocks && c.IsDraft)
            {
                try
                {
                    await Api.LockAsync(r.DocumentId, ct).ConfigureAwait(false);
                }
                catch (PdmApiException ex)
                {
                    if (!(ex.StatusCode == 409 && ex.Error?.Lock?.UserId == (await GetMeAsync(ct).ConfigureAwait(false)).Id))
                    {
                        lockedNow = false;
                        result.Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.Server,
                            $"Новый документ «{c.RelPath}» сдан, но не оставлен в работе: {ex.Message}", c.RelPath, r.DocumentId));
                    }
                }
            }

            state.Entries.RemoveAll(e => RelPath.Key(e.RelPath) == RelPath.Key(c.RelPath) || e.DocumentId == r.DocumentId);
            state.Entries.Add(new WorkspaceEntry
            {
                RelPath = c.RelPath,
                ProjectFolder = c.ProjectFolder,
                DocumentId = r.DocumentId,
                Version = r.Version,
                Sha256 = c.Sha256,
                Size = c.Size,
                Cad = c.Cad,
                Kind = c.Kind,
                Designation = c.Designation,
                Name = c.Name,
                Deps = deps,
                LockedByMe = lockedNow,
                Stamp = null,
            });
            if (File.Exists(c.FullPath)) FileOps.SetReadOnly(c.FullPath, !lockedNow);

            try
            {
                await Cache.AddFileAsync(staged[c], c.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or WorkspaceException)
            {
                // Кэш — только ускорение; при следующем открытии объект будет скачан.
            }
        }
        state.Save(root);
    }
}
