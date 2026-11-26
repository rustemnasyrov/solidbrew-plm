using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Свойства для записи в файл одного документа.</summary>
public sealed record PropertyWrite(Guid DocumentId, IReadOnlyList<CadPropertyChange> Changes);

/// <summary>Итог записи свойств CAD через PDM.</summary>
public sealed class WritePropertiesResult : OperationResult
{
    /// <summary>Документ (при записи одного документа).</summary>
    public Guid DocumentId { get; set; }
    /// <summary>Опубликованная версия документа (при записи одного); null — не сдавался.</summary>
    public int? NewVersion { get; set; }
    /// <summary>Опубликованные версии: документ → версия.</summary>
    public Dictionary<Guid, int> Published { get; } = new();
    /// <summary>
    /// Документы, в которые свойства только записаны, без сдачи: они взяты вами и в них есть другие несданные правки —
    /// сдаются вместе с ними.
    /// </summary>
    public List<Guid> WrittenOnly { get; } = new();
    /// <summary>CAD сохранил файлы в версии новее рабочей: повторите с подтверждением (<c>acceptNewerCad</c>).</summary>
    public bool NeedsNewerCadConfirmation { get; set; }
}

public sealed partial class WorkspaceService
{
    /// <summary>Записать свойства одного документа — см. пакетный <see cref="WritePropertiesAsync(string, IReadOnlyList{PropertyWrite}, string?, bool, IProgress{WorkProgress}?, CancellationToken)"/>.</summary>
    public async Task<WritePropertiesResult> WritePropertiesAsync(string workRoot, Guid documentId, IReadOnlyList<CadPropertyChange> changes,
        string? comment = null, bool acceptNewerCad = false, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (changes == null) throw new ArgumentNullException(nameof(changes));
        var result = await WritePropertiesAsync(workRoot, new[] { new PropertyWrite(documentId, changes) }, comment, acceptNewerCad, progress, ct).ConfigureAwait(false);
        result.DocumentId = documentId;
        result.NewVersion = result.Published.TryGetValue(documentId, out var v) ? v : null;
        return result;
    }

    /// <summary>
    /// Записать свойства CAD в файлы нескольких документов рабочей папки и опубликовать их новые версии — одной операцией:
    /// документы, ещё не взятые мной, берутся в работу; адаптер записывает свойства в каждый файл и сохраняет его;
    /// все документы сдаются одной сдачей; блокировки, взятые операцией, снимаются. Документ, взятый мной, в котором уже
    /// есть другие несданные правки, только получает свойства в файл (<see cref="WritePropertiesResult.WrittenOnly"/>).
    /// Всё или ничего: если хоть один документ нельзя записать или сдать, все файлы возвращаются к прежнему содержимому,
    /// а взятия, сделанные операцией, отменяются.
    /// </summary>
    public Task<WritePropertiesResult> WritePropertiesAsync(string workRoot, IReadOnlyList<PropertyWrite> writes,
        string? comment = null, bool acceptNewerCad = false, IProgress<WorkProgress>? progress = null, CancellationToken ct = default) =>
        ModifyDocumentsAsync(workRoot, writes, null, "Свойства не записаны", comment, acceptNewerCad, progress, ct);

    /// <summary>
    /// «Удалить ссылку» (ссылка на файл, не учтённый в PDM): документ берётся в работу, CAD убирает ссылки
    /// (<see cref="ICadAdapter.RemoveReferencesAsync"/>: у сборки — компоненты, у детали — разрыв внешних ссылок и отметка
    /// для PDM), PDM проверяет, что ссылок больше нет, и сдаёт новую версию; взятие, сделанное операцией, снимается.
    /// У детали SOLIDWORKS разрывает все внешние ссылки сразу, поэтому, если у неё есть и другие ссылки, операция не выполняется.
    /// При ошибке файл возвращается к прежнему содержимому.
    /// </summary>
    public async Task<WritePropertiesResult> RemoveReferencesAsync(string workRoot, Guid documentId, IReadOnlyList<string> refPaths,
        string? comment = null, bool acceptNewerCad = false, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (refPaths == null) throw new ArgumentNullException(nameof(refPaths));
        var result = await ModifyDocumentsAsync(workRoot, new[] { new PropertyWrite(documentId, Array.Empty<CadPropertyChange>()) },
            new Dictionary<Guid, IReadOnlyList<string>> { [documentId] = refPaths }, "Ссылки не убраны", comment, acceptNewerCad, progress, ct).ConfigureAwait(false);
        result.DocumentId = documentId;
        result.NewVersion = result.Published.TryGetValue(documentId, out var v) ? v : null;
        return result;
    }

    private async Task<WritePropertiesResult> ModifyDocumentsAsync(string workRoot, IReadOnlyList<PropertyWrite> writes,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>>? removeRefs, string fail,
        string? comment, bool acceptNewerCad, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        if (writes == null) throw new ArgumentNullException(nameof(writes));
        var removal = removeRefs != null;
        var result = new WritePropertiesResult();
        WritePropertiesResult Fail(string message)
        {
            result.Success = false;
            result.Message = message;
            return result;
        }

        // Документ → изменения (повторы одного свойства — последнее значение).
        var byDoc = writes.GroupBy(w => w.DocumentId).Select(g => (Id: g.Key, Changes: g
                .SelectMany(w => w.Changes)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new CadPropertyChange(c.Configuration ?? string.Empty, c.Name.Trim(), c.Value ?? string.Empty))
                .GroupBy(c => (c.Configuration, c.Name.ToUpperInvariant())).Select(x => x.Last()).ToList()))
            .Where(x => x.Changes.Count > 0 || removeRefs?.ContainsKey(x.Id) == true).ToList();
        if (byDoc.Count == 0)
        {
            result.Success = true;
            result.Message = "Нечего записывать.";
            return result;
        }

        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var me = await GetMeAsync(ct).ConfigureAwait(false);

        // 1. Проверить все документы до любых изменений.
        var items = new List<WriteItem>();
        foreach (var (id, changes) in byDoc)
        {
            var entry = state.FindByDocument(id);
            if (entry == null)
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                    "Документа нет в рабочей папке. Откройте изделие, в которое он входит, и повторите.", null, id));
                continue;
            }
            var label = Texts.Doc(entry.RelPath, entry.Designation);
            var full = RelPath.Combine(root, entry.RelPath);
            if (!File.Exists(full))
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                    $"Файла {label} нет в рабочей папке. Откройте комплект заново.", entry.RelPath, id));
                continue;
            }
            var doc = (await Api.GetDocumentAsync(id, ct).ConfigureAwait(false)).Document;
            if (doc.Lock != null && doc.Lock.UserId != me.Id)
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LockedByOther,
                    $"{label} взят в работу: {doc.Lock.UserDisplayName}, с {Texts.Time(doc.Lock.At)}. Записать свойства можно, когда его сдадут.", entry.RelPath, id));
                continue;
            }
            if (doc.LatestVersion != entry.Version)
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NotLatest,
                    $"В рабочей папке {label} v{entry.Version}, последняя — v{doc.LatestVersion}. Обновите документ до последней версии и повторите.", entry.RelPath, id));
                continue;
            }
            var lockedByMe = doc.Lock != null;
            var localChanged = await FileOps.HashAsync(full, ct).ConfigureAwait(false) != entry.LocalSha;
            if (localChanged && !lockedByMe)
            {
                result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.LocalChanges,
                    $"В {label} есть правки, а документ не взят в работу. Отмените их (откройте документ заново) или возьмите его в работу и повторите.", entry.RelPath, id));
                continue;
            }
            var adapter = AdapterFor(entry.Cad, entry.RelPath);
            var remove = removeRefs != null && removeRefs.TryGetValue(id, out var rr) ? rr : null;
            if (remove != null)
            {
                var kind = adapter.KindOf(full);
                if (kind == DocumentKind.Drawing)
                {
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.UnsupportedFile,
                        $"У чертежа {label} ссылки — это виды модели. Удалите виды в CAD.", entry.RelPath, id));
                    continue;
                }
                if (kind == DocumentKind.Part)
                {
                    // CAD разрывает внешние ссылки детали все сразу: другие ссылки разорвались бы молча.
                    var info = await ReadCadAsync(adapter, full, await FileOps.HashAsync(full, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    var others = info.References.Where(r => !remove.Any(x => SameSavedPath(x, r.SavedPath))).ToList();
                    if (others.Count > 0)
                    {
                        result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.UnsupportedFile,
                            $"У детали {label} есть и другие внешние ссылки: {string.Join(", ", others.Select(r => "«" + r.FileName + "»"))}. " +
                            "SOLIDWORKS разрывает внешние ссылки детали все сразу — уберите их вместе или разорвите в SOLIDWORKS вручную.", entry.RelPath, id));
                        continue;
                    }
                }
            }
            items.Add(new WriteItem(id, label, full, adapter, changes, lockedByMe, localChanged) { RemoveRefs = remove });
        }
        if (result.Problems.Count > 0)
        {
            return Fail(items.Count + result.Problems.Count == 1
                ? $"{fail}: " + result.Problems[0].Message
                : $"{fail} ни в один файл: нельзя изменить {result.Problems.Count} из {byDoc.Count}. {result.Problems[0].Message}");
        }

        // 2. Копии файлов до записи: при ошибке все возвращаются как были.
        foreach (var it in items)
        {
            it.Backup = Path.Combine(Path.GetDirectoryName(it.Full)!, ".~sb" + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + Path.GetFileName(it.Full));
            File.Copy(it.Full, it.Backup);
            File.SetAttributes(it.Backup, FileAttributes.Hidden);
        }
        var tookLock = new List<Guid>();
        var keepFiles = false;
        try
        {
            // 3. Взять в работу.
            var toLock = items.Where(i => !i.LockedByMe).Select(i => i.Id).ToList();
            if (toLock.Count > 0)
            {
                progress?.Report(new WorkProgress(WorkStage.Locking, 0, toLock.Count, "Взятие в работу"));
                var co = await CheckOutAsync(root, toLock, ct).ConfigureAwait(false);
                tookLock.AddRange(co.Items.Where(i => i.Success).Select(i => i.DocumentId));
                if (!co.Success)
                {
                    result.Problems.AddRange(co.Problems);
                    return Fail($"{fail}: " + (co.Items.FirstOrDefault(i => !i.Success)?.Message ?? co.Message));
                }
            }

            // 4. Записать в файлы.
            var n = 0;
            foreach (var it in items)
            {
                n++;
                progress?.Report(new WorkProgress(WorkStage.Placing, n, items.Count, (it.RemoveRefs != null ? "Удаление ссылок: " : "Запись свойств: ") + it.Label));
                try
                {
                    if (it.RemoveRefs != null) await it.Adapter.RemoveReferencesAsync(it.Full, it.RemoveRefs, ct).ConfigureAwait(false);
                    if (it.Changes.Count > 0) await it.Adapter.WritePropertiesAsync(it.Full, it.Changes, ct).ConfigureAwait(false);
                }
                catch (CadException ex)
                {
                    result.Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.ReadError, $"{it.Label}: {ex.Message}", null, it.Id));
                    return Fail(items.Count == 1
                        ? $"{fail}: " + ex.Message
                        : $"{fail} ни в один файл: {it.Label}: {ex.Message}");
                }
                if (it.RemoveRefs != null)
                {
                    // Проверка: для PDM ссылок больше нет (у детали CAD оставляет их разорванными — их скрывает отметка).
                    var after = await ReadCadAsync(it.Adapter, it.Full, await FileOps.HashAsync(it.Full, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    var still = after.References.Where(r => it.RemoveRefs.Any(x => SameSavedPath(x, r.SavedPath))).ToList();
                    if (still.Count > 0)
                        return Fail($"{fail}: CAD не убрал ссылки {string.Join(", ", still.Select(r => "«" + r.FileName + "»"))} из {it.Label}.");
                }
            }

            // 5. Сдать одной сдачей (кроме документов с другими несданными правками).
            var checkinIds = items.Where(i => !i.LocalChanged).Select(i => i.Id).ToHashSet();
            if (checkinIds.Count > 0)
            {
                var plan = await PrepareCheckinAsync(root, progress, ct).ConfigureAwait(false);
                foreach (var c in plan.Items) c.Include = c.DocumentId is { } d && checkinIds.Contains(d);
                plan.Revalidate();
                var unchanged = items.Where(i => checkinIds.Contains(i.Id) && plan.Included.All(c => c.DocumentId != i.Id)).ToList();
                if (unchanged.Count > 0)
                    return Fail($"{fail}: CAD не изменил файл {unchanged[0].Label}.");
                if (plan.HasBlockingProblems)
                {
                    result.Problems.AddRange(plan.Problems.Where(p => p.IsBlocking));
                    return Fail($"{fail}: документы нельзя сдать. " + plan.Problems.First(p => p.IsBlocking).Message);
                }
                if (plan.HasNewerCadVersion && !acceptNewerCad)
                {
                    result.NeedsNewerCadConfirmation = true;
                    result.Problems.AddRange(plan.Problems.Where(p => p.Code == LocalProblemCodes.NewerCadVersion));
                    return Fail($"{fail}: при сохранении CAD преобразовал файлы в версию новее рабочей. Подтвердите, если это допустимо.");
                }

                var text = !string.IsNullOrWhiteSpace(comment) ? comment.Trim()
                    : removal ? "Убраны ссылки на файлы, не учтённые в PDM: " + string.Join(", ", items.Where(i => i.RemoveRefs != null).SelectMany(i => i.RemoveRefs!).Select(Texts.FileNameOf).Distinct(StringComparer.OrdinalIgnoreCase))
                    : "Свойства: " + string.Join("; ", items.Where(i => checkinIds.Contains(i.Id)).SelectMany(i => i.Changes).Select(c => c.ToString()).Distinct());
                if (text.Length > 500) text = text.Substring(0, 497) + "…";
                // Взятые мной до операции остаются взятыми; взятые операцией — снимаются ниже.
                var keepLocks = items.Any(i => i.LockedByMe && checkinIds.Contains(i.Id));
                var checkin = await ExecuteCheckinAsync(plan, text, keepLocks, acceptNewerCad: true, progress, ct).ConfigureAwait(false);
                if (!checkin.Success)
                {
                    result.Problems.AddRange(checkin.Problems);
                    return Fail($"{fail}, ничего не опубликовано: " + checkin.Message);
                }
                keepFiles = true;
                foreach (var p in checkin.Published.Where(p => checkinIds.Contains(p.DocumentId))) result.Published[p.DocumentId] = p.Version;
                if (keepLocks && tookLock.Count > 0)
                {
                    foreach (var id in tookLock) await TryUnlockAsync(id, ct).ConfigureAwait(false);
                    var after = WorkspaceState.Load(root);
                    foreach (var id in tookLock)
                    {
                        if (after.FindByDocument(id) is not { } e) continue;
                        e.LockedByMe = false;
                        var f = RelPath.Combine(root, e.RelPath);
                        if (File.Exists(f)) FileOps.SetReadOnly(f, true);
                    }
                    after.Save(root);
                }
            }
            keepFiles = true;
            result.WrittenOnly.AddRange(items.Where(i => i.LocalChanged).Select(i => i.Id));
            result.Success = true;
            var parts = new List<string>();
            if (removal)
            {
                var it = items[0];
                parts.Add(result.Published.TryGetValue(it.Id, out var ver)
                    ? $"Ссылки убраны ({it.RemoveRefs!.Count}): {it.Label} v{ver}."
                    : $"Ссылки убраны из файла {it.Label} без сдачи: там есть и другие ваши несданные правки — сдайте их, когда закончите.");
                result.Message = string.Join(" ", parts);
                return result;
            }
            if (result.Published.Count > 0)
            {
                parts.Add(items.Count == 1
                    ? $"Свойства записаны ({items[0].Changes.Count}): {items[0].Label} v{result.Published.Values.First()}."
                    : $"Свойства записаны и сданы: {result.Published.Count} {Documents(result.Published.Count)} " +
                      "(" + string.Join(", ", items.Where(i => result.Published.ContainsKey(i.Id)).Select(i => $"{i.Label} v{result.Published[i.Id]}")) + ").");
            }
            if (result.WrittenOnly.Count > 0)
            {
                var only = items.Where(i => i.LocalChanged).Select(i => i.Label).ToList();
                parts.Add($"Свойства записаны в файл{(only.Count > 1 ? "ы" : string.Empty)} {string.Join(", ", only)} без сдачи: там есть и другие ваши несданные правки — сдайте их, когда закончите.");
            }
            result.Message = string.Join(" ", parts);
            return result;
        }
        finally
        {
            if (!keepFiles) await RestoreAfterFailedWriteAsync(root, items, tookLock).ConfigureAwait(false);
            foreach (var it in items)
                if (it.Backup != null) FileOps.TryDelete(it.Backup);
        }
    }

    private static string Documents(int n) => (n % 10, n % 100) switch
    {
        (1, not 11) => "документ",
        (2 or 3 or 4, not (12 or 13 or 14)) => "документа",
        _ => "документов",
    };

    private sealed class WriteItem(Guid id, string label, string full, ICadAdapter adapter, List<CadPropertyChange> changes, bool lockedByMe, bool localChanged)
    {
        public Guid Id { get; } = id;
        public string Label { get; } = label;
        public string Full { get; } = full;
        public ICadAdapter Adapter { get; } = adapter;
        public List<CadPropertyChange> Changes { get; } = changes;
        public bool LockedByMe { get; } = lockedByMe;
        public bool LocalChanged { get; } = localChanged;
        /// <summary>Ссылки, которые нужно убрать из файла (иначе — только свойства).</summary>
        public IReadOnlyList<string>? RemoveRefs { get; init; }
        public string? Backup { get; set; }
    }

    /// <summary>Вернуть файлы к содержимому до записи и отменить взятия, сделанные операцией.</summary>
    private async Task RestoreAfterFailedWriteAsync(string root, List<WriteItem> items, List<Guid> tookLock)
    {
        foreach (var it in items)
        {
            if (it.Backup == null || !File.Exists(it.Backup)) continue;
            try
            {
                if (File.Exists(it.Full)) FileOps.SetReadOnly(it.Full, false);
                File.Copy(it.Backup, it.Full, overwrite: true);
                File.SetAttributes(it.Full, File.GetAttributes(it.Full) & ~FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл занят (например, открыт в CAD) — остаётся как есть; пользователь увидит его изменённым.
            }
        }
        if (tookLock.Count == 0) return;
        foreach (var id in tookLock) await TryUnlockAsync(id, CancellationToken.None).ConfigureAwait(false);
        var state = WorkspaceState.Load(root);
        foreach (var id in tookLock)
        {
            if (state.FindByDocument(id) is not { } entry) continue;
            entry.LockedByMe = false;
            try
            {
                var f = RelPath.Combine(root, entry.RelPath);
                if (File.Exists(f)) FileOps.SetReadOnly(f, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        state.Save(root);
    }
}
