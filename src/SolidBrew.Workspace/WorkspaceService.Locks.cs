using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Итог «Взять в работу» по одному документу.</summary>
public sealed class CheckOutItemResult
{
    public Guid DocumentId { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>Если документ занят — чья блокировка.</summary>
    public LockDto? LockedBy { get; init; }
    /// <summary>Если в папке не последняя версия — какая последняя.</summary>
    public int? LatestVersion { get; init; }
}

public sealed class CheckOutResult : OperationResult
{
    public List<CheckOutItemResult> Items { get; } = new();
}

/// <summary>Документ в плане «Отменить взятие».</summary>
public sealed class UndoCheckOutItem
{
    public Guid DocumentId { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public string? Designation { get; init; }
    public int Version { get; init; }
    /// <summary>В файле есть несданные правки.</summary>
    public bool HasLocalChanges { get; init; }
    /// <summary>Вернуть файл к сданной версии из кэша (правки будут потеряны). По умолчанию — нет: правки остаются, файл только для чтения.</summary>
    public bool Revert { get; set; }
}

public sealed class UndoCheckOutPlan
{
    public string WorkRoot { get; init; } = string.Empty;
    public List<UndoCheckOutItem> Items { get; } = new();
    public List<PlanProblem> Problems { get; } = new();
    public bool HasLocalChanges => Items.Any(i => i.HasLocalChanges);

    /// <summary>Предупреждение для подтверждения (null — правок нет).</summary>
    public string? Warning => HasLocalChanges
        ? "Есть несданные правки: " + string.Join(", ", Items.Where(i => i.HasLocalChanges).Select(i => Texts.Doc(i.RelPath, i.Designation))) +
          ". По умолчанию правки останутся в файлах (только для чтения); отметьте «Вернуть», чтобы восстановить сданную версию."
        : null;
}

public sealed class UndoCheckOutResult : OperationResult
{
    public List<string> Reverted { get; } = new();
    public List<string> Unlocked { get; } = new();
}

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Взять в работу: блокировка на сервере, и только после её подтверждения — снятие атрибута «только для чтения».
    /// Если в рабочей папке не последняя версия — документ не берётся («сначала обновите до vN»).
    /// </summary>
    public async Task<CheckOutResult> CheckOutAsync(string workRoot, IEnumerable<Guid> documentIds, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var result = new CheckOutResult();

        foreach (var id in documentIds.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var entry = state.FindByDocument(id);
            if (entry == null)
            {
                result.Items.Add(new CheckOutItemResult { DocumentId = id, Message = "Документа нет в рабочей папке. Сначала откройте его." });
                continue;
            }
            var label = Texts.Doc(entry.RelPath, entry.Designation);
            var full = RelPath.Combine(root, entry.RelPath);
            if (!File.Exists(full))
            {
                result.Items.Add(new CheckOutItemResult { DocumentId = id, RelPath = entry.RelPath, Message = $"Файла {label} нет в рабочей папке. Откройте комплект заново." });
                continue;
            }

            CheckOutItemResult item;
            try
            {
                item = await CheckOutOneAsync(root, entry, full, me, label, ct).ConfigureAwait(false);
            }
            catch (PdmApiException ex)
            {
                item = new CheckOutItemResult { DocumentId = id, RelPath = entry.RelPath, Message = $"{label}: {ex.Message}" };
            }
            if (item.Success)
            {
                entry.LockedByMe = true;
                state.Save(root);
            }
            result.Items.Add(item);
        }

        result.Success = result.Items.Count > 0 && result.Items.All(i => i.Success);
        foreach (var f in result.Items.Where(i => !i.Success))
            result.Problems.Add(new PlanProblem(ProblemSeverity.Error, f.LockedBy != null ? LocalProblemCodes.LockedByOther : LocalProblemCodes.NotLatest, f.Message, f.RelPath, f.DocumentId));
        var ok = result.Items.Count(i => i.Success);
        result.Message = result.Success ? $"Взято в работу: {ok}." : $"Взято в работу: {ok} из {result.Items.Count}.";
        return result;
    }

    private async Task<CheckOutItemResult> CheckOutOneAsync(string root, WorkspaceEntry entry, string full, UserDto me, string label, CancellationToken ct)
    {
        var doc = (await Api.GetDocumentAsync(entry.DocumentId, ct).ConfigureAwait(false)).Document;
        if (doc.Lock != null && doc.Lock.UserId != me.Id)
            return LockedByOther(entry, doc.Lock, label);
        if (doc.LatestVersion != entry.Version)
            return NotLatest(entry, doc.LatestVersion, label);

        var alreadyMine = doc.Lock != null;
        try
        {
            await Api.LockAsync(entry.DocumentId, ct).ConfigureAwait(false);
        }
        catch (PdmApiException ex) when (ex.StatusCode == 409 && ex.Error?.Lock != null)
        {
            if (ex.Error.Lock.UserId != me.Id) return LockedByOther(entry, ex.Error.Lock, label);
        }

        // Повторная проверка после блокировки: пока брали, могла появиться новая версия.
        var after = (await Api.GetDocumentAsync(entry.DocumentId, ct).ConfigureAwait(false)).Document;
        if (after.LatestVersion != entry.Version)
        {
            if (!alreadyMine)
            {
                try
                {
                    await Api.UnlockAsync(entry.DocumentId, ct).ConfigureAwait(false);
                }
                catch (PdmApiException)
                {
                }
            }
            return NotLatest(entry, after.LatestVersion, label);
        }

        // Только теперь, после ответа сервера, снимаем «только для чтения».
        FileOps.SetReadOnly(full, false);
        return new CheckOutItemResult { DocumentId = entry.DocumentId, RelPath = entry.RelPath, Success = true, Message = $"{label} взят в работу." };
    }

    private static CheckOutItemResult LockedByOther(WorkspaceEntry entry, LockDto l, string label) => new()
    {
        DocumentId = entry.DocumentId,
        RelPath = entry.RelPath,
        LockedBy = l,
        Message = $"{label} уже взят в работу: {l.UserDisplayName} ({l.UserLogin}), с {Texts.Time(l.At)}. Файл остаётся только для чтения. Дождитесь сдачи или договоритесь с коллегой.",
    };

    private static CheckOutItemResult NotLatest(WorkspaceEntry entry, int latest, string label) => new()
    {
        DocumentId = entry.DocumentId,
        RelPath = entry.RelPath,
        LatestVersion = latest,
        Message = $"В рабочей папке {label} v{entry.Version}, а последняя версия — v{latest}. Сначала обновите документ до v{latest} («Обновить до последней версии»), затем возьмите в работу.",
    };

    /// <summary>Подготовить «Отменить взятие»: какие документы, есть ли в них несданные правки.</summary>
    public async Task<UndoCheckOutPlan> PrepareUndoCheckOutAsync(string workRoot, IEnumerable<Guid> documentIds, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var plan = new UndoCheckOutPlan { WorkRoot = root };
        foreach (var id in documentIds.Distinct())
        {
            var entry = state.FindByDocument(id);
            if (entry == null)
            {
                plan.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest, "Документа нет в рабочей папке.", null, id));
                continue;
            }
            var full = RelPath.Combine(root, entry.RelPath);
            var changed = File.Exists(full) && await FileOps.HashAsync(full, ct).ConfigureAwait(false) != entry.Sha256;
            plan.Items.Add(new UndoCheckOutItem
            {
                DocumentId = id,
                RelPath = entry.RelPath,
                Designation = entry.Designation,
                Version = entry.Version,
                HasLocalChanges = changed,
            });
        }
        return plan;
    }

    /// <summary>
    /// Отменить взятие: снять блокировку на сервере, файл — «только для чтения».
    /// Правки по умолчанию остаются; <see cref="UndoCheckOutItem.Revert"/> (или <paramref name="revertAll"/>) — вернуть сданную версию из кэша.
    /// </summary>
    public async Task<UndoCheckOutResult> ExecuteUndoCheckOutAsync(UndoCheckOutPlan plan, bool? revertAll = null,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var root = plan.WorkRoot;
        var state = WorkspaceState.Load(root);
        var result = new UndoCheckOutResult();
        var me = await GetMeAsync(ct).ConfigureAwait(false);

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var entry = state.FindByDocument(item.DocumentId);
            if (entry == null) continue;
            var label = Texts.Doc(entry.RelPath, entry.Designation);
            try
            {
                await Api.UnlockAsync(item.DocumentId, ct).ConfigureAwait(false);
            }
            catch (PdmApiException ex) when (ex.StatusCode is 404 or 409)
            {
                // Блокировки уже нет (сняли принудительно) или она чужая — проверим.
                var doc = (await Api.GetDocumentAsync(item.DocumentId, ct).ConfigureAwait(false)).Document;
                if (doc.Lock != null && doc.Lock.UserId == me.Id)
                {
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Server, $"{label}: {ex.Message}", entry.RelPath, entry.DocumentId));
                    continue;
                }
            }
            catch (PdmApiException ex)
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Server, $"{label}: {ex.Message}", entry.RelPath, entry.DocumentId));
                continue;
            }

            entry.LockedByMe = false;
            result.Unlocked.Add(entry.RelPath);
            var full = RelPath.Combine(root, entry.RelPath);
            var revert = revertAll ?? item.Revert;
            if (revert && item.HasLocalChanges || revert && !File.Exists(full))
            {
                await Cache.EnsureAsync(Api, new[] { new CacheObject(entry.Sha256, entry.Size) }, progress, ct).ConfigureAwait(false);
                FileOps.PlaceCopy(Cache.PathOf(entry.Sha256), full, readOnly: true);
                entry.Stamp = FileOps.StampOf(full);
                result.Reverted.Add(entry.RelPath);
            }
            else if (File.Exists(full))
            {
                FileOps.SetReadOnly(full, true);
                if (item.HasLocalChanges)
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Info, LocalProblemCodes.LocalChanges,
                        $"Правки в {label} сохранены в файле (только для чтения). Они не сданы.", entry.RelPath, entry.DocumentId));
            }
            state.Save(root);
        }

        result.Success = !result.Problems.Any(p => p.IsBlocking);
        result.Message = $"Взятие отменено: {result.Unlocked.Count}" + (result.Reverted.Count > 0 ? $", возвращено к сданной версии: {result.Reverted.Count}." : ".");
        return result;
    }
}
