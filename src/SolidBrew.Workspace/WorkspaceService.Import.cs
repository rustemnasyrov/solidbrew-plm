using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Подготовить постановку на учёт готового проекта: обход ссылок от головного файла, поиск чертежей
    /// в папке головного файла (со всеми подпапками, например <c>Чертежи/</c>) и в <paramref name="extraFolders"/> по их ссылкам
    /// на комплект: попадают только чертежи, которые ссылаются на документы комплекта (у остальных читаются только ссылки).
    /// Документы комплекта копируются во временную папку кэша (несколько файлов сразу, пока CAD читает предыдущие), хешируются
    /// и читаются адаптером из копии. Отчёт строится быстрым чтением — ссылки, конфигурации и версия CAD без открытия документа;
    /// свойства (обозначение, наименование) дочитываются после отчёта и только у загружаемых документов — здесь же или, при
    /// <see cref="ImportOptions.DeferProperties"/>, вызывающим (<see cref="ReadPendingImportPropertiesAsync"/>). Свойства берутся
    /// из кэша по хешу, если файл уже читался; атрибуты исходников не меняются. Папки поиска — папка головного файла и <paramref name="extraFolders"/>.
    /// </summary>
    public async Task<ImportReport> PrepareImportAsync(string headFile, IEnumerable<string>? extraFolders = null,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default, ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        var head = Path.GetFullPath(headFile);
        if (!File.Exists(head)) throw new WorkspaceException($"Головной документ не найден: {head}.");
        if (Adapters.FindFor(head) == null) throw new WorkspaceException($"«{Path.GetFileName(head)}» не является документом поддерживаемого CAD.");

        var extras = (extraFolders ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => Path.GetFullPath(f).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in extras)
        {
            if (!Directory.Exists(f)) throw new WorkspaceException($"Папка поиска не найдена: {f}.");
        }
        var searchFolders = new List<string> { Path.GetDirectoryName(head)!.TrimEnd('\\') };
        searchFolders.AddRange(extras.Where(e => !searchFolders.Contains(e, StringComparer.OrdinalIgnoreCase)));

        var working = await GetWorkingCadVersionsAsync(ct).ConfigureAwait(false);
        var report = new ImportReport
        {
            HeadFile = head,
            RelPathPrefix = options.RelPathPrefix?.Trim().Trim('/') ?? string.Empty,
            AllowExistingHead = options.AllowExistingHead,
            SearchFolders = searchFolders,
            ExtraFolders = extras,
            StagingRoot = Cache.CreateStagingFolder(),
            WorkingCadVersions = working,
        };

        try
        {
            progress?.Report(new WorkProgress(WorkStage.Scanning, 0, 0, "Составление списка файлов в папках поиска"));
            var nameIndex = BuildNameIndex(searchFolders, ct);
            report.Scan = new ImportScan(searchFolders, nameIndex, working, options.CopyParallelism);

            var headSaved = string.IsNullOrWhiteSpace(options.HeadSavedPath) ? null : SavedFullPath(options.HeadSavedPath, Path.GetDirectoryName(head)!);
            var headDoc = await ScanAsync(report, head, true, false, headSaved, progress, ct).ConfigureAwait(false);
            await ClosureAsync(report, headDoc, progress, ct).ConfigureAwait(false);

            // Чертежи в папке головного файла (с подпапками) и в доп. папках: сборка не знает своих чертежей, поэтому их ищут
            // по ссылкам «чертёж → модель». Кандидаты — только чертежи: деталь или сборка из папки поиска, ссылающаяся на
            // комплект, — это вышестоящее или соседнее изделие, ему в комплекте не место. Сначала читаются только ссылки
            // (без копирования и без открытия в CAD); копируются и читаются дальше лишь чертежи, вошедшие в комплект.
            var drawingFiles = searchFolders.SelectMany(f => SafeEnumerate(f))
                .Where(p => !IsTempName(p) && Adapters.FindFor(p) is { } a && a.KindOf(p) == DocumentKind.Drawing)
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var candidates = new List<(string Path, List<ImportReference> Refs)>();
            var checkedDrawings = 0;
            foreach (var path in drawingFiles)
            {
                ct.ThrowIfCancellationRequested();
                checkedDrawings++;
                if (report.Find(path) != null) continue;
                progress?.Report(new WorkProgress(WorkStage.Reading, checkedDrawings, drawingFiles.Count,
                    $"Поиск чертежей: ссылки {checkedDrawings} из {drawingFiles.Count}: «{Path.GetFileName(path)}»"));
                List<CadReference> refs;
                try
                {
                    refs = await Adapters.FindFor(path)!.ReadReferencesAsync(path, ct).ConfigureAwait(false);
                }
                catch (CadException)
                {
                    continue; // нечитаемый чертёж не может быть привязан к комплекту
                }
                var dir = Path.GetDirectoryName(path)!;
                candidates.Add((path, refs.Select(r => ClassifyImportReference(path, dir, r.SavedPath, null, searchFolders, nameIndex)).ToList()));
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var c in candidates.ToList())
                {
                    if (report.Find(c.Path) != null)
                    {
                        candidates.Remove(c);
                        continue;
                    }
                    var linked = c.Refs.Any(r =>
                        (r.TargetPath != null && report.Find(r.TargetPath) != null) ||
                        r.Candidates.Any(x => report.Find(x) != null));
                    if (!linked) continue;
                    candidates.Remove(c);
                    var d = await ScanAsync(report, c.Path, false, true, null, progress, ct).ConfigureAwait(false);
                    await ClosureAsync(report, d, progress, ct).ConfigureAwait(false);
                    changed = true;
                }
            } while (changed);

            // Уже учтённые в PDM (то же содержимое) — берутся из хранилища, а не заводятся заново.
            progress?.Report(new WorkProgress(WorkStage.Scanning, 0, 0, "Поиск уже учтённых файлов в хранилище"));
            await FindExistingAsync(report, ct).ConfigureAwait(false);

            // Решения по умолчанию: неоднозначные — без выбора, «только по имени» и «вне папок» — без подтверждения.
            report.Recompute();
            if (!options.DeferProperties) await ReadPendingImportPropertiesAsync(report, progress, ct).ConfigureAwait(false);
            progress?.Report(new WorkProgress(WorkStage.Done, 1, 1, $"Прочитано документов: {report.Documents.Count}"));
            return report;
        }
        catch
        {
            if (report.Scan != null) await report.Scan.DrainCopiesAsync().ConfigureAwait(false);
            report.Dispose();
            throw;
        }
    }

    /// <summary>Прочитать документ быстрым чтением, добавить в отчёт и начать копировать файлы его ссылок.</summary>
    private async Task<ImportDocument> ScanAsync(ImportReport report, string path, bool isHead, bool isExtra, string? savedLocation,
        IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var scan = report.Scan!;
        var n = ++scan.Scanned;
        var known = Math.Max(n, scan.CopyCount);
        progress?.Report(new WorkProgress(WorkStage.Reading, n, known, $"Чтение ссылок {n} из {known}: «{Path.GetFileName(path)}»"));
        var doc = await ScanSourceAsync(report, path, isHead, isExtra, MovedFrom(savedLocation, path), ct).ConfigureAwait(false);
        report.Documents.Add(doc);
        Prefetch(report, doc, ct);
        return doc;
    }

    /// <summary>Обход ссылок от <paramref name="start"/>: читаются все найденные файлы, ещё не попавшие в отчёт.</summary>
    private async Task ClosureAsync(ImportReport report, ImportDocument start, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var queue = new Queue<ImportDocument>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var d = queue.Dequeue();
            foreach (var r in d.References.ToList())
            {
                foreach (var t in TargetsOf(r))
                {
                    if (report.Find(t) != null) continue;
                    // Путь из ссылки — где файл лежал при сохранении: если его перенесли, по нему ищутся и его ссылки.
                    queue.Enqueue(await ScanAsync(report, t, false, false, SavedFullPath(r.SavedPath, Path.GetDirectoryName(d.SourcePath)!), progress, ct).ConfigureAwait(false));
                }
            }
        }
    }

    private static IEnumerable<string> TargetsOf(ImportReference r) =>
        r.Category == ImportReferenceCategory.Ambiguous ? r.Candidates : r.TargetPath != null ? new[] { r.TargetPath } : Array.Empty<string>();

    /// <summary>Наперёд скопировать файлы ссылок документа: копирование идёт, пока CAD читает другие документы.</summary>
    private void Prefetch(ImportReport report, ImportDocument doc, CancellationToken ct)
    {
        foreach (var t in doc.References.SelectMany(TargetsOf))
        {
            if (report.Find(t) == null) _ = StageCopy(report, t, ct);
        }
    }

    /// <summary>
    /// Копия исходника во временной папке (одна на исходник): копирование только чтением, параллельно с другими
    /// (не больше <see cref="ImportOptions.CopyParallelism"/>). Ошибка ввода-вывода — в результате, а не исключением.
    /// </summary>
    private Task<StagedCopy> StageCopy(ImportReport report, string source, CancellationToken ct)
    {
        var scan = report.Scan!;
        lock (scan.Copies)
        {
            if (scan.Copies.TryGetValue(source, out var existing)) return existing;
            var n = ++scan.CopyCounter;
            var copy = Path.Combine(report.StagingRoot, "files", n.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(source));
            var task = Task.Run(async () =>
            {
                await scan.CopyGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var stamp = FileOps.StampOf(source);
                    var (sha, size) = await FileOps.CopyForReadingAsync(source, copy, ct).ConfigureAwait(false);
                    // Хеш исходника известен — ключ кэша свойств чертежа не будет перечитывать модель с сетевого диска.
                    if (stamp != null && stamp.SameAs(FileOps.StampOf(source))) RememberHash(source, stamp, sha);
                    return new StagedCopy(copy, sha, size, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new StagedCopy(copy, string.Empty, 0, ex);
                }
                finally
                {
                    scan.CopyGate.Release();
                }
            }, ct);
            scan.Copies[source] = task;
            return task;
        }
    }

    /// <summary>
    /// Отметить документы, содержимое которых уже есть в PDM. Из нескольких версий с тем же содержимым — используемый документ,
    /// его последняя версия, самая свежая. Старый сервер без этой операции (404) — ничего не отмечается.
    /// </summary>
    private async Task FindExistingAsync(ImportReport report, CancellationToken ct)
    {
        var hashes = report.Documents.Where(d => d.Sha256.Length > 0).Select(d => d.Sha256).Distinct(StringComparer.Ordinal).ToList();
        if (hashes.Count == 0) return;
        List<VersionByHashDto> found;
        try
        {
            found = await Api.FindVersionsByHashAsync(hashes, ct).ConfigureAwait(false);
        }
        catch (PdmApiException ex) when (!ex.IsNetworkError)
        {
            return;
        }
        var best = found.GroupBy(v => v.Sha256, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g
            .OrderByDescending(v => v.Document.Status == DocumentStatus.Active)
            .ThenByDescending(v => v.Version == v.Document.LatestVersion)
            .ThenByDescending(v => v.Document.LatestAt)
            .First(), StringComparer.Ordinal);
        foreach (var d in report.Documents)
            d.Existing = d.Sha256.Length > 0 && best.TryGetValue(d.Sha256, out var v) ? v : null;
    }

    /// <summary>
    /// Заново отметить документы, уже учтённые в PDM, и пересчитать отчёт. Нужно, если между подготовкой и постановкой
    /// часть его файлов поставили на учёт другой постановкой (пакетная сдача файлов по ссылке с общими компонентами):
    /// тогда они берутся из хранилища, а не загружаются второй раз.
    /// </summary>
    public async Task RefreshExistingAsync(ImportReport report, CancellationToken ct = default)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        await FindExistingAsync(report, ct).ConfigureAwait(false);
        report.Recompute();
    }

    /// <summary>Полный путь ссылки (относительная — от папки ссылающегося документа); null — путь недопустим.</summary>
    private static string? SavedFullPath(string saved, string referencingDir)
    {
        try
        {
            var normalized = saved.Replace('/', '\\');
            return Path.GetFullPath(Path.IsPathFullyQualified(normalized) ? normalized : Path.Combine(referencingDir, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Прежнее расположение документа, если он перенесён (путь из ссылки не совпадает с найденным файлом).</summary>
    private static string? MovedFrom(string? savedLocation, string actual) =>
        savedLocation != null && !string.Equals(savedLocation, Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase) ? savedLocation : null;

    private static bool IsTempName(string path)
    {
        var n = Path.GetFileName(path);
        return n.StartsWith("~$", StringComparison.Ordinal) || n.StartsWith(".", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SafeEnumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, List<string>> BuildNameIndex(IEnumerable<string> folders, CancellationToken ct)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            foreach (var file in SafeEnumerate(folder))
            {
                ct.ThrowIfCancellationRequested();
                if (IsTempName(file) || !seen.Add(file)) continue;
                var name = Path.GetFileName(file);
                if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
                list.Add(Path.GetFullPath(file));
            }
        }
        return index;
    }

    private static bool IsInside(string folder, string path) =>
        Path.GetFullPath(path).StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private async Task<ImportDocument> ScanSourceAsync(ImportReport report, string source, bool isHead, bool isExtra, string? savedLocation, CancellationToken ct)
    {
        var scan = report.Scan!;
        var staged = await StageCopy(report, source, ct).ConfigureAwait(false);
        var copy = staged.Path;
        if (staged.Error != null)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, IsHead = isHead, IsExtra = isExtra, SavedLocation = savedLocation,
                ReadError = $"Не удалось прочитать «{source}»: {staged.Error.Message}",
            };
        }
        var sha = staged.Sha256;
        var size = staged.Size;

        var adapter = Adapters.FindFor(source);
        if (adapter == null)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, Sha256 = sha, Size = size, IsHead = isHead, IsExtra = isExtra, Kind = DocumentKind.Other,
                SavedLocation = savedLocation,
                ReadError = $"Файл «{Path.GetFileName(source)}» ({source}) не является документом поддерживаемого CAD — поставить его на учёт нельзя.",
            };
        }

        var kind = adapter.KindOf(source);
        CadDocumentInfo info;
        var hasProperties = !adapter.HasFastRead();
        try
        {
            if (hasProperties)
            {
                info = await ReadCadAsync(adapter, copy, sha, ct).ConfigureAwait(false); // адаптер без быстрого чтения — целиком
            }
            else
            {
                // Быстрое чтение: только ссылки, конфигурации и версия CAD. Свойства уже читавшегося файла — из кэша по хешу.
                info = await adapter.ReadStructureAsync(copy, ct).ConfigureAwait(false);
                if (CadReadCache.IsCacheable(kind) && ReadCache.TryGet(adapter.CadId, sha) is { } cached)
                {
                    info.Properties = cached.Properties;
                    hasProperties = true;
                    DocumentProperties.ApplyRemovedReferences(info);
                }
            }
        }
        catch (CadException ex)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, Sha256 = sha, Size = size, IsHead = isHead, IsExtra = isExtra,
                Cad = adapter.CadId, Kind = kind, SavedLocation = savedLocation,
                ReadError = $"Не удалось прочитать «{source}»: {ex.Message}",
            };
        }

        var doc = new ImportDocument
        {
            SourcePath = source,
            StagingPath = copy,
            Sha256 = sha,
            Size = size,
            Cad = adapter.CadId,
            Kind = kind,
            CadVersion = info.CadVersion,
            CadVersionMajor = info.CadVersionMajor,
            IsNewerCadVersion = info.CadVersionMajor != null && scan.Working.TryGetValue(adapter.CadId, out var w) && info.CadVersionMajor > w,
            Configurations = info.Configurations,
            IsHead = isHead,
            IsExtra = isExtra,
            SavedLocation = savedLocation,
            HasStructure = true,
        };
        if (hasProperties) doc.SetProperties(info.Properties);

        var dir = Path.GetDirectoryName(source)!;
        foreach (var r in info.References)
        {
            doc.References.Add(ClassifyImportReference(source, dir, r.SavedPath, savedLocation, scan.SearchFolders, scan.NameIndex));
        }
        return doc;
    }

    // ------------------------------------------------------------------ свойства после отчёта

    /// <summary>
    /// Дочитать свойства всех документов, которым они нужны (<see cref="ImportDocument.NeedsProperties"/>; головной — первым),
    /// и применить к отчёту. Ход: «Свойства N из M, осталось ≈ мм:сс». Ошибка чтения документа — его <see cref="ImportDocument.ReadError"/>.
    /// Возвращает число прочитанных документов.
    /// </summary>
    public async Task<int> ReadPendingImportPropertiesAsync(ImportReport report, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        var done = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (report.NextForProperties() is { } d)
        {
            ct.ThrowIfCancellationRequested();
            var total = done + report.PendingProperties.Count();
            progress?.Report(new WorkProgress(WorkStage.Reading, done, total, ImportReport.PropertiesProgressText(done, total, watch.Elapsed, d.FileName)));
            var read = await ReadImportPropertiesAsync(report, d, ct).ConfigureAwait(false);
            report.ApplyProperties(read);
            done++;
        }
        return done;
    }

    /// <summary>
    /// Прочитать свойства одного документа отчёта (из кэша по хешу или адаптером, с открытием копии в CAD). Отчёт не меняется —
    /// применить результат: <see cref="ImportReport.ApplyProperties"/> (окно делает это в своём потоке, пока читает в фоне).
    /// </summary>
    public async Task<ImportPropertiesRead> ReadImportPropertiesAsync(ImportReport report, ImportDocument document, CancellationToken ct = default)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (document == null) throw new ArgumentNullException(nameof(document));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var adapter = Adapters.Get(document.Cad) ?? Adapters.FindFor(document.SourcePath);
        if (adapter == null)
            return new ImportPropertiesRead(document, null, $"Нет адаптера CAD для «{document.FileName}».", null, null, watch.Elapsed);
        try
        {
            var props = await ReadPropertiesCachedAsync(adapter, document, ct).ConfigureAwait(false);
            return new ImportPropertiesRead(document, props, null, null, null, watch.Elapsed);
        }
        catch (CadSameNameOpenException ex)
        {
            return new ImportPropertiesRead(document, null, $"Не удалось прочитать свойства «{document.SourcePath}»: {ex.Message}", ex.OpenPath, ex.CadName, watch.Elapsed);
        }
        catch (CadException ex)
        {
            return new ImportPropertiesRead(document, null, $"Не удалось прочитать свойства «{document.SourcePath}»: {ex.Message}", null, null, watch.Elapsed);
        }
    }

    private async Task<Dictionary<string, Dictionary<string, string>>> ReadPropertiesCachedAsync(ICadAdapter adapter, ImportDocument doc, CancellationToken ct)
    {
        string? key = null;
        if (SolidBrew.Core.Hashing.Sha256Hex.IsValid(doc.Sha256))
        {
            if (CadReadCache.IsCacheable(doc.Kind)) key = doc.Sha256;
            else if (doc.Kind == DocumentKind.Drawing)
                key = await DrawingCacheKeyAsync(doc.StagingPath, doc.Sha256,
                    doc.References.Select(r => new CadReference { SavedPath = r.SavedPath, FileName = CadReference.FileNameOf(r.SavedPath) }).ToList(), ct).ConfigureAwait(false);
        }
        if (key != null && ReadCache.TryGet(adapter.CadId, key) is { } cached) return cached.Properties;

        var before = FileOps.StampOf(doc.StagingPath);
        var props = await adapter.ReadPropertiesAsync(doc.StagingPath, doc.Configurations, ct).ConfigureAwait(false);
        if (key != null && before != null && before.SameAs(FileOps.StampOf(doc.StagingPath)))
        {
            ReadCache.Put(adapter.CadId, key, new CadDocumentInfo
            {
                CadVersion = doc.CadVersion, CadVersionMajor = doc.CadVersionMajor, Configurations = doc.Configurations, Properties = props,
            });
        }
        return props;
    }

    /// <summary>
    /// «Прочитать заново» документы с ошибкой чтения (по умолчанию — все такие): у прочитанных ссылками сбрасывается ошибка
    /// свойств — их перечитает <see cref="ReadPendingImportPropertiesAsync"/> (или фоновое чтение окна); не скопированные или
    /// не прочитанные документы копируются и читаются заново, с обходом их ссылок. Остальной отчёт и решения сохраняются.
    /// Возвращает документы, которые будут перечитаны (новые объекты вместо перечитанных целиком).
    /// </summary>
    public async Task<List<ImportDocument>> RereadImportDocumentsAsync(ImportReport report, IEnumerable<ImportDocument>? documents = null,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        var scan = report.Scan ?? throw new WorkspaceException("Этот отчёт нельзя дочитать — прочитайте ссылки заново.");
        var list = (documents ?? report.Documents.ToList()).Where(d => d.ReadError != null && report.Documents.Contains(d)).Distinct().ToList();
        var result = new List<ImportDocument>();
        var rescanned = false;
        foreach (var d in list)
        {
            ct.ThrowIfCancellationRequested();
            if (d.HasStructure)
            {
                d.ClearPropertiesError();
                result.Add(d);
                continue;
            }
            progress?.Report(new WorkProgress(WorkStage.Reading, 0, 0, $"Чтение «{d.FileName}» заново"));
            lock (scan.Copies) scan.Copies.Remove(d.SourcePath);
            var fresh = await ScanSourceAsync(report, d.SourcePath, d.IsHead, d.IsExtra, d.SavedLocation, ct).ConfigureAwait(false);
            fresh.Include = d.Include;
            report.Documents[report.Documents.IndexOf(d)] = fresh;
            Prefetch(report, fresh, ct);
            await ClosureAsync(report, fresh, progress, ct).ConfigureAwait(false);
            result.Add(fresh);
            rescanned = true;
        }
        if (rescanned) await FindExistingAsync(report, ct).ConfigureAwait(false);
        report.Recompute();
        return result;
    }

    /// <summary>
    /// Ссылка документа, перенесённого из <paramref name="savedLocation"/> (переименовали или переместили папку): файл на том же
    /// месте относительно документа, что и до переноса. null — документ не переносился, путь на другом диске или файла там нет.
    /// </summary>
    private static string? FindRelocated(string source, string dir, string saved, string? savedLocation)
    {
        if (savedLocation == null) return null;
        var oldDir = Path.GetDirectoryName(savedLocation);
        var savedFull = SavedFullPath(saved, oldDir ?? dir);
        if (oldDir == null || savedFull == null || string.Equals(oldDir.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var rel = Path.GetRelativePath(oldDir, savedFull);
            if (Path.IsPathRooted(rel)) return null; // другой диск — относительного положения нет
            var candidate = Path.GetFullPath(Path.Combine(dir, rel));
            return File.Exists(candidate) && !string.Equals(candidate, source, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private static ImportReference ClassifyImportReference(string source, string dir, string saved, string? savedLocation,
        List<string> searchFolders, Dictionary<string, List<string>> nameIndex)
    {
        // Порядок поиска SOLIDWORKS (без документов в памяти): файл с тем же именем в папке ссылающегося документа,
        // затем записанный путь. Так CAD и откроет исходник; файл, перенесённый вместе с документом, — найден, а не «по имени».
        string? resolved = null;
        try
        {
            var sameFolder = Path.GetFullPath(Path.Combine(dir, CadReference.FileNameOf(saved)));
            if (File.Exists(sameFolder) && !string.Equals(sameFolder, source, StringComparison.OrdinalIgnoreCase))
            {
                resolved = sameFolder;
            }
            else
            {
                var normalized = saved.Replace('/', '\\');
                var candidate = Path.IsPathFullyQualified(normalized) ? normalized : Path.Combine(dir, normalized);
                candidate = Path.GetFullPath(candidate);
                if (File.Exists(candidate)) resolved = candidate;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
        }

        if (resolved != null)
        {
            var inside = searchFolders.Any(f => IsInside(f, resolved));
            return new ImportReference
            {
                SourceDocument = source,
                SavedPath = saved,
                Category = inside ? ImportReferenceCategory.Found : ImportReferenceCategory.OutsideFolders,
                TargetPath = resolved,
                Accept = inside,
            };
        }

        // Документ перенесли вместе с окружением (папку переименовали): файл на прежнем месте относительно документа.
        // Точнее совпадения по имени, но CAD сам так не ищет — нужно подтверждение.
        if (FindRelocated(source, dir, saved, savedLocation) is { } relocated)
        {
            return new ImportReference
            {
                SourceDocument = source, SavedPath = saved, Category = ImportReferenceCategory.Relocated, TargetPath = relocated,
                MovedFrom = Path.GetDirectoryName(savedLocation!), MovedTo = dir,
            };
        }

        var name = CadReference.FileNameOf(saved);
        var matches = nameIndex.TryGetValue(name, out var list)
            ? list.Where(p => !string.Equals(p, source, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        if (matches.Count == 1)
            return new ImportReference { SourceDocument = source, SavedPath = saved, Category = ImportReferenceCategory.NameOnly, TargetPath = matches[0] };
        if (matches.Count > 1)
            return new ImportReference { SourceDocument = source, SavedPath = saved, Category = ImportReferenceCategory.Ambiguous, Candidates = matches.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList() };
        return new ImportReference { SourceDocument = source, SavedPath = saved, Category = ImportReferenceCategory.Missing };
    }

    /// <summary>
    /// Поставить на учёт: одна сдача всех включённых документов как новых (TempKey) в проект <paramref name="projectId"/>.
    /// Отсутствующие и неподтверждённые ссылки — только при <paramref name="allowMissing"/>.
    /// После успеха временные копии удаляются; содержимое кладётся в кэш (последующее «Открыть» ничего не скачивает).
    /// </summary>
    public async Task<ImportResult> ExecuteImportAsync(ImportReport report, Guid projectId, string? comment, bool allowMissing = false, bool acceptNewerCad = false,
        IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        var result = new ImportResult();
        report.Recompute();
        // Свойства, которые ещё не дочитаны (окно не дождалось, CLI, пакетная сдача), — до проверки: ошибка чтения блокирует.
        if (report.PendingProperties.Any()) await ReadPendingImportPropertiesAsync(report, progress, ct).ConfigureAwait(false);
        if (!report.CanExecute(allowMissing))
        {
            result.Problems.AddRange(report.Problems.Where(p => p.IsBlocking));
            result.Message = report.HasMissing && report.CanExecute(true)
                ? "Есть отсутствующие или неподтверждённые ссылки. Подтвердите их или разрешите постановку с отсутствующими ссылками."
                : "Поставить на учёт нельзя: есть блокирующие ошибки.";
            return result;
        }
        if (report.HasNewerCadVersion && !acceptNewerCad)
        {
            result.Problems.AddRange(report.Problems.Where(p => p.Code == LocalProblemCodes.NewerCadVersion));
            result.Message = "Есть файлы, сохранённые в версии CAD новее рабочей. Подтвердите постановку таких файлов.";
            return result;
        }

        var docs = report.NewDocuments.ToList();
        if (docs.Count == 0 && report.Head.Disposition == ImportDisposition.FromStorage && report.Head.Existing is { } existingHead)
        {
            // Загружать нечего: файл уже есть в PDM — им и пользуемся.
            result.Success = true;
            result.HeadDocumentId = existingHead.Document.Id;
            result.HeadVersion = existingHead.Version;
            result.Message = $"«{report.Head.FileName}» уже учтён в PDM: {ImportReport.Describe(existingHead)}. Загружать нечего.";
            report.Dispose();
            return result;
        }
        var request = new CheckinRequest
        {
            ProjectId = projectId,
            Comment = string.IsNullOrWhiteSpace(comment) ? "Постановка на учёт" : comment.Trim(),
            AcceptNewerCadVersion = acceptNewerCad,
        };
        var anyMissing = false;
        var anyByLink = false;
        foreach (var d in docs)
        {
            var item = new CheckinItem
            {
                TempKey = d.RelPath,
                RelPath = d.RelPath,
                Sha256 = d.Sha256,
                Size = d.Size,
                Cad = d.Cad,
                Kind = d.Kind,
                CadVersion = d.CadVersion,
                CadVersionMajor = d.CadVersionMajor,
                Configurations = d.Configurations,
                Properties = d.Properties,
            };
            foreach (var r in d.References)
            {
                var t = r.EffectiveTarget != null ? report.Find(r.EffectiveTarget) : null;
                if (t == d) continue;
                switch (t?.Disposition)
                {
                    case ImportDisposition.New:
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath, TempKey = t.RelPath });
                        break;
                    case ImportDisposition.FromStorage:
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath, DocumentId = t.Existing!.Document.Id, Version = t.Existing.Version });
                        break;
                    case ImportDisposition.ByLink:
                        // Галочка снята: ссылка остаётся путём к файлу, CAD откроет его по ссылке.
                        anyByLink = true;
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath });
                        break;
                    default:
                        anyMissing = true;
                        item.References.Add(new CheckinReference { RefPath = r.SavedPath });
                        break;
                }
            }
            request.Items.Add(item);
        }
        request.AllowUnresolvedReferences = anyByLink || (anyMissing && allowMissing);

        try
        {
            await UploadMissingAsync(docs.Select(d => (d.Sha256, d.Size, d.StagingPath)), progress, ct).ConfigureAwait(false);
            progress?.Report(new WorkProgress(WorkStage.Publishing, 0, 1, "Постановка на учёт"));
            result.Response = await Api.CheckinAsync(request, ct).ConfigureAwait(false);
        }
        catch (PdmApiException ex)
        {
            result.Problems.AddRange(PlanProblem.FromServer(ex));
            result.Message = "Постановка не выполнена, ничего не опубликовано: " + ex.Message;
            return result;
        }

        foreach (var d in docs)
        {
            try
            {
                await Cache.AddFileAsync(d.StagingPath, d.Sha256, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or WorkspaceException)
            {
            }
        }

        var headItem = result.Response.Items.FirstOrDefault(i => i.TempKey == report.Head.RelPath);
        if (headItem != null)
        {
            result.HeadDocumentId = headItem.DocumentId;
            result.HeadVersion = headItem.Version;
        }
        result.Success = true;
        var fromStorage = report.FromStorageDocuments.Count();
        var byLink = report.ByLinkDocuments.Count();
        result.Message = $"Поставлено на учёт документов: {result.Response.Items.Count}." +
                         (fromStorage > 0 ? $" Взято из хранилища: {fromStorage}." : string.Empty) +
                         (byLink > 0 ? $" Оставлено ссылками на файлы: {byLink}." : string.Empty) +
                         (anyMissing ? " Есть отсутствующие ссылки." : string.Empty);
        report.Dispose();
        return result;
    }

    /// <summary>Отказаться от постановки: удалить временные копии.</summary>
    public void DiscardImport(ImportReport report) => report?.Dispose();
}

/// <summary>Копия исходника во временной папке постановки (или ошибка копирования).</summary>
internal sealed record StagedCopy(string Path, string Sha256, long Size, Exception? Error);

/// <summary>Состояние обхода ссылок постановки: папки поиска, индекс имён файлов, копии во временную папку.</summary>
internal sealed class ImportScan
{
    public ImportScan(List<string> searchFolders, Dictionary<string, List<string>> nameIndex, Dictionary<string, int> working, int copyParallelism)
    {
        SearchFolders = searchFolders;
        NameIndex = nameIndex;
        Working = working;
        CopyGate = new SemaphoreSlim(Math.Max(1, copyParallelism));
    }

    public List<string> SearchFolders { get; }
    public Dictionary<string, List<string>> NameIndex { get; }
    public Dictionary<string, int> Working { get; }
    public SemaphoreSlim CopyGate { get; }
    /// <summary>Копии по полному пути исходника (запущенные наперёд и готовые).</summary>
    public Dictionary<string, Task<StagedCopy>> Copies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int CopyCounter { get; set; }
    public int Scanned { get; set; }

    public int CopyCount
    {
        get
        {
            lock (Copies) return Copies.Count;
        }
    }

    /// <summary>Дождаться начатых копирований (перед удалением временной папки при отмене или ошибке).</summary>
    public async Task DrainCopiesAsync()
    {
        Task[] all;
        lock (Copies) all = Copies.Values.Cast<Task>().ToArray();
        try
        {
            await Task.WhenAll(all).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // отмена или ошибка копирования — копии всё равно удаляются вместе с папкой
        }
    }
}
