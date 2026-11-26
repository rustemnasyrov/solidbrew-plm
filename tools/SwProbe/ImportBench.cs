using System.Diagnostics;
using System.Reflection;
using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SwProbe;

/// <summary>
/// Замер подготовки постановки на учёт (<see cref="WorkspaceService.PrepareImportAsync"/>) на реальном SOLIDWORKS:
/// холодный прогон (пустой кэш свойств), тёплый (свойства из кэша), сверка результатов и оценка прежнего поведения
/// (полное чтение каждого документа SOLIDWORKS из папок поиска). Исходники только читаются; кэш и копии — в папке прототипа.
/// </summary>
internal static class ImportBench
{
    public static async Task<object> RunAsync(SolidWorksAdapter adapter, string head, List<string> extraFolders, CancellationToken ct)
    {
        var headFull = Path.GetFullPath(head);
        if (!File.Exists(headFull)) throw new ArgumentException($"Файл «{headFull}» не найден.");
        var benchRoot = Path.Combine(WriteGuard.ProbeRoot, "import-bench", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var counting = new CountingAdapter(adapter);
        var registry = new CadAdapterRegistry();
        registry.Register(counting);
        var cache = new LocalCache(Path.Combine(benchRoot, "cache"));
        var service = new WorkspaceService(OfflineApi.Create(), registry, cache);

        async Task<(object Stats, ImportReport Report)> PassAsync(string name)
        {
            counting.Reset();
            Commands.Progress?.Invoke($"import-bench: {name}");
            var sw = Stopwatch.StartNew();
            var report = await service.PrepareImportAsync(headFull, extraFolders, null, ct).ConfigureAwait(false);
            var seconds = sw.Elapsed.TotalSeconds;
            return (new
            {
                pass = name,
                seconds = Math.Round(seconds, 1),
                documents = report.Documents.Count,
                included = report.IncludedDocuments.Count(),
                fullReads = counting.FullReads,
                fullReadSeconds = Math.Round(counting.FullMs / 1000, 1),
                referenceReads = counting.RefReads,
                referenceReadSeconds = Math.Round(counting.RefMs / 1000, 1),
                readErrors = report.Documents.Where(d => d.ReadError != null).Select(d => $"{d.FileName}: {d.ReadError}").ToList(),
            }, report);
        }

        var (cold, coldReport) = await PassAsync("холодный: кэш свойств пуст").ConfigureAwait(false);
        var (warm, warmReport) = await PassAsync("тёплый: свойства из кэша").ConfigureAwait(false);

        // Сверка: тёплый прогон должен дать те же документы, свойства и ссылки.
        var mismatches = new List<string>();
        foreach (var c in coldReport.Documents)
        {
            var w = warmReport.Documents.FirstOrDefault(d => string.Equals(d.SourcePath, c.SourcePath, StringComparison.OrdinalIgnoreCase));
            if (w == null) { mismatches.Add($"{c.FileName}: нет во втором прогоне"); continue; }
            if (c.Sha256 != w.Sha256) mismatches.Add($"{c.FileName}: другой хеш (файл менялся?)");
            if (c.Designation != w.Designation || c.Name != w.Name) mismatches.Add($"{c.FileName}: обозначение/наименование");
            if (!c.Configurations.SequenceEqual(w.Configurations)) mismatches.Add($"{c.FileName}: конфигурации");
            if (Flatten(c.Properties) != Flatten(w.Properties)) mismatches.Add($"{c.FileName}: свойства");
            if (c.CadVersion != w.CadVersion) mismatches.Add($"{c.FileName}: версия CAD");
            var cr = string.Join("|", c.References.Select(r => r.SavedPath + "→" + r.Category + ":" + r.TargetPath));
            var wr = string.Join("|", w.References.Select(r => r.SavedPath + "→" + r.Category + ":" + r.TargetPath));
            if (cr != wr) mismatches.Add($"{c.FileName}: ссылки: было [{cr}] стало [{wr}]");
        }
        if (warmReport.Documents.Count != coldReport.Documents.Count) mismatches.Add("разное число документов");

        // Прежнее поведение: каждый документ SOLIDWORKS из папок поиска читался полностью (с открытием в CAD).
        var searchFolders = coldReport.SearchFolders;
        var inReport = new HashSet<string>(coldReport.Documents.Select(d => d.SourcePath), StringComparer.OrdinalIgnoreCase);
        var others = searchFolders
            .SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories))
            .Where(p => adapter.IsDocument(p) && !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !inReport.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var kitPaths = new HashSet<string>(coldReport.IncludedDocuments.Select(d => d.SourcePath), StringComparer.OrdinalIgnoreCase);
        var copyRoot = Path.Combine(benchRoot, "old-copies");
        double oldExtraMs = 0;
        var oldErrors = 0;
        var wouldPull = new List<string>();
        var n = 0;
        foreach (var p in others)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            Commands.Progress?.Invoke($"import-bench: прежнее чтение {n}/{others.Count}: {Path.GetFileName(p)}");
            var copy = Path.Combine(copyRoot, n.ToString(), Path.GetFileName(p));
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(p, copy);
            try
            {
                var info = await adapter.ReadAsync(copy, ct).ConfigureAwait(false);
                oldExtraMs += sw.Elapsed.TotalMilliseconds;
                // Прежняя логика брала любой документ, ссылающийся на комплект (в том числе соседние сборки).
                if (adapter.KindOf(p) != DocumentKind.Drawing && info.References.Any(r => kitPaths.Contains(SafeFull(r.SavedPath))))
                    wouldPull.Add(Path.GetRelativePath(searchFolders[0], p));
            }
            catch (CadException)
            {
                oldExtraMs += sw.Elapsed.TotalMilliseconds;
                oldErrors++;
            }
        }

        coldReport.Dispose();
        warmReport.Dispose();
        TryDeleteDirectory(copyRoot);

        var coldSeconds = (double)cold.GetType().GetProperty("seconds")!.GetValue(cold)!;
        return new
        {
            head = headFull,
            searchFolders,
            cold,
            warm,
            mismatches,
            included = coldReport.IncludedDocuments.Select(d => d.RelPath).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            // Прочитаны, но не включены: на них ведут ссылки, требующие решения (вне папок, только по имени, неоднозначные).
            notIncluded = coldReport.Documents.Where(d => !d.IsIncluded).Select(d => new
            {
                d.SourcePath,
                kind = d.Kind.ToString(),
                via = coldReport.Documents.SelectMany(x => x.References)
                    .Where(r => string.Equals(r.TargetPath, d.SourcePath, StringComparison.OrdinalIgnoreCase) || r.Candidates.Contains(d.SourcePath, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.Category.ToString()).Distinct().ToList(),
            }).ToList(),
            oldBehaviourEstimate = new
            {
                note = "Прежний код дополнительно копировал и полностью читал каждый документ SOLIDWORKS папок поиска, не вошедший в комплект.",
                extraDocuments = others.Count,
                extraSeconds = Math.Round(oldExtraMs / 1000, 1),
                totalSecondsEstimate = Math.Round(coldSeconds + oldExtraMs / 1000, 1),
                readErrors = oldErrors,
                partsAndAssembliesOldCodeWouldPullIn = wouldPull,
            },
            cacheFolder = service.ReadCache.Root,
        };
    }

    private static string SafeFull(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private static string Flatten(Dictionary<string, Dictionary<string, string>> props) =>
        string.Join(";", props.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "{" + string.Join(",", p.Value.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => v.Key + "=" + v.Value)) + "}"));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Обёртка адаптера: считает полные чтения и чтения только ссылок.</summary>
    private sealed class CountingAdapter(ICadAdapter inner) : ICadAdapter
    {
        public int FullReads { get; private set; }
        public int RefReads { get; private set; }
        public double FullMs { get; private set; }
        public double RefMs { get; private set; }

        public void Reset() { FullReads = RefReads = 0; FullMs = RefMs = 0; }

        public string CadId => inner.CadId;
        public string DisplayName => inner.DisplayName;
        public IReadOnlyCollection<string> Extensions => inner.Extensions;
        public bool IsDocument(string path) => inner.IsDocument(path);
        public DocumentKind KindOf(string path) => inner.KindOf(path);
        public Task WritePropertiesAsync(string documentPath, IReadOnlyList<CadPropertyChange> changes, CancellationToken ct = default) =>
            inner.WritePropertiesAsync(documentPath, changes, ct);
        public Task RemoveReferencesAsync(string documentPath, IReadOnlyList<string> savedPaths, CancellationToken ct = default) =>
            inner.RemoveReferencesAsync(documentPath, savedPaths, ct);

        public async Task<CadDocumentInfo> ReadAsync(string path, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try { return await inner.ReadAsync(path, ct).ConfigureAwait(false); }
            finally { FullReads++; FullMs += sw.Elapsed.TotalMilliseconds; }
        }

        public async Task<List<CadReference>> ReadReferencesAsync(string path, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            try { return await inner.ReadReferencesAsync(path, ct).ConfigureAwait(false); }
            finally { RefReads++; RefMs += sw.Elapsed.TotalMilliseconds; }
        }

        public Task ReplaceReferencesAsync(string documentPath, IReadOnlyDictionary<string, string> oldToNew, CancellationToken ct = default) =>
            throw new NotSupportedException("Замер только читает.");

        public Task<OpenReport> OpenAndVerifyAsync(string headPath, string workRoot, CancellationToken ct = default) =>
            throw new NotSupportedException("Замер только читает.");
    }
}

/// <summary>API без сервера: настройки недоступны (рабочие версии CAD не заданы), остальное — ошибка.</summary>
public class OfflineApi : DispatchProxy
{
    public static IPdmApi Create() => DispatchProxy.Create<IPdmApi, OfflineApi>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IPdmApi.GetSettingsAsync))
            return Task.FromException<SettingsDto>(new PdmApiException(403, "Замер без сервера."));
        throw new NotSupportedException($"Замер без сервера: {targetMethod?.Name} недоступен.");
    }
}
