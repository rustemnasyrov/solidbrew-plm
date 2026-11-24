using SolidBrew.Core.Cad;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Переписать ссылки разложенных документов на полные пути их зависимостей в рабочей папке.
    /// <para>
    /// SOLIDWORKS хранит ссылки абсолютными путями компьютера, где документ сохраняли, и ищет компонент в памяти по имени,
    /// затем в папке ссылающегося документа, затем по сохранённому пути; относительный путь он не использует. Поэтому без
    /// переписывания чертёж из «Чертежи/» или сборка с деталями в подпапках загружает компоненты из исходной папки (тихая
    /// подмена) или не находит их.
    /// </para>
    /// Ожидаемый путь ссылки — файл документа-зависимости в рабочей папке (по <see cref="WorkspaceDependency.DocumentId"/>:
    /// из состояния, иначе из <paramref name="fallbackPaths"/>). Переписываются только файлы без локальных правок; файл
    /// временно лишается атрибута «только для чтения». После успеха обновляются <see cref="WorkspaceDependency.RefPath"/>,
    /// <see cref="WorkspaceEntry.PlacedSha256"/> и отметка. Ошибки адаптера не прерывают операцию — по документу добавляется
    /// предупреждение в <paramref name="problems"/>. Состояние не сохраняется — это делает вызывающий.
    /// </summary>
    /// <returns>Относительные пути документов, в которых ссылки переписаны.</returns>
    internal async Task<List<string>> RewriteReferencesAsync(string root, WorkspaceState state, IEnumerable<WorkspaceEntry> entries,
        IReadOnlyDictionary<Guid, string>? fallbackPaths, List<PlanProblem> problems, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var rewritten = new List<string>();
        var list = entries.Distinct().Where(e => e.Deps.Count > 0).ToList();
        var n = 0;
        foreach (var entry in list)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            var full = RelPath.Combine(root, entry.RelPath);
            if (!File.Exists(full)) continue;

            // Старый путь ссылки → новый полный путь в рабочей папке.
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<(WorkspaceDependency Dep, string NewPath)>();
            foreach (var dep in entry.Deps)
            {
                if (dep.DocumentId == entry.DocumentId || string.IsNullOrEmpty(dep.RefPath)) continue;
                var childRel = state.FindByDocument(dep.DocumentId)?.RelPath;
                if (childRel == null && fallbackPaths != null) fallbackPaths.TryGetValue(dep.DocumentId, out childRel);
                if (childRel == null) continue;
                var expected = RelPath.Combine(root, childRel);
                if (SameSavedPath(dep.RefPath, expected)) continue;
                if (map.TryGetValue(dep.RefPath, out var already) && !SameSavedPath(already, expected)) continue; // одна ссылка — два документа: не трогаем
                map[dep.RefPath] = expected;
                changes.Add((dep, expected));
            }
            if (map.Count == 0) continue;

            var label = Texts.Doc(entry.RelPath, entry.Designation);
            progress?.Report(new WorkProgress(WorkStage.Placing, n, list.Count, $"Ссылки на рабочую папку в «{Texts.FileNameOf(entry.RelPath)}»"));

            // Файл с локальными правками не трогаем: это работа пользователя.
            var stamp = FileOps.StampOf(full);
            var before = entry.Stamp != null && entry.Stamp.SameAs(stamp) ? entry.LocalSha : await FileOps.HashAsync(full, ct).ConfigureAwait(false);
            if (!string.Equals(before, entry.LocalSha, StringComparison.Ordinal)) continue;

            var adapter = Adapters.Get(entry.Cad) ?? Adapters.FindFor(full);
            string? error = null;
            if (adapter == null)
            {
                error = $"нет адаптера CAD «{entry.Cad}»";
            }
            else
            {
                var wasReadOnly = FileOps.IsReadOnly(full);
                try
                {
                    if (wasReadOnly) FileOps.SetReadOnly(full, false);
                    await adapter.ReplaceReferencesAsync(full, map, ct).ConfigureAwait(false);
                }
                catch (CadException ex)
                {
                    error = ex.Message;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ex.Message;
                }
                finally
                {
                    try
                    {
                        if (wasReadOnly && File.Exists(full)) FileOps.SetReadOnly(full, true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }

            var after = await FileOps.HashAsync(full, ct).ConfigureAwait(false);
            if (error == null)
            {
                foreach (var (dep, newPath) in changes) dep.RefPath = newPath;
                rewritten.Add(entry.RelPath);
            }
            else
            {
                problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.ReferencesNotRewritten,
                    $"{label}: ссылки не переписаны: {error.TrimEnd('.')}; CAD может загрузить компоненты по старым путям. " +
                    "Закройте документ в CAD / запустите CAD и повторите «Открыть».", entry.RelPath, entry.DocumentId));
                if (!string.Equals(after, before, StringComparison.Ordinal) && adapter != null)
                {
                    // Адаптер успел переписать часть ссылок: привести зависимости к тому, что реально записано в файле.
                    await SyncRefPathsAsync(adapter, full, changes, ct).ConfigureAwait(false);
                }
            }

            if (!string.Equals(after, before, StringComparison.Ordinal))
            {
                entry.PlacedSha256 = string.Equals(after, entry.Sha256, StringComparison.Ordinal) ? null : after;
                entry.Stamp = FileOps.StampOf(full);
            }
        }
        return rewritten;
    }

    private static async Task SyncRefPathsAsync(ICadAdapter adapter, string full, List<(WorkspaceDependency Dep, string NewPath)> changes, CancellationToken ct)
    {
        CadDocumentInfo info;
        try
        {
            info = await adapter.ReadAsync(full, ct).ConfigureAwait(false);
        }
        catch (CadException)
        {
            return;
        }
        foreach (var (dep, newPath) in changes)
        {
            var hasOld = info.References.Any(r => SameSavedPath(r.SavedPath, dep.RefPath));
            var hasNew = info.References.Any(r => SameSavedPath(r.SavedPath, newPath));
            if (hasNew && !hasOld) dep.RefPath = newPath;
        }
    }

    /// <summary>Пути ссылок совпадают (без учёта регистра и вида разделителя).</summary>
    internal static bool SameSavedPath(string a, string b) =>
        string.Equals(a.Replace('/', '\\').Trim(), b.Replace('/', '\\').Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<WorkspaceDependency> CloneDeps(IEnumerable<WorkspaceDependency> deps) =>
        deps.Select(d => new WorkspaceDependency { DocumentId = d.DocumentId, Version = d.Version, RefPath = d.RefPath }).ToList();
}
