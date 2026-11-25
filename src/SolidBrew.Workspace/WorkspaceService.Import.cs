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
    /// Документы комплекта копируются во временную папку кэша, хешируются и читаются адаптером уже из копии
    /// (свойства — из кэша по хешу, если файл уже читался); атрибуты исходников не меняются.
    /// Папки поиска — папка головного файла и <paramref name="extraFolders"/>.
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
            var counter = 0;

            async Task<ImportDocument> ScanAsync(string path, bool isHead, bool isExtra)
            {
                counter++;
                progress?.Report(new WorkProgress(WorkStage.Reading, counter, 0, $"Чтение «{Path.GetFileName(path)}»"));
                var doc = await ScanSourceAsync(report, path, counter, isHead, isExtra, working, searchFolders, nameIndex, ct).ConfigureAwait(false);
                report.Documents.Add(doc);
                return doc;
            }

            async Task ClosureAsync(ImportDocument start)
            {
                var queue = new Queue<ImportDocument>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var d = queue.Dequeue();
                    foreach (var r in d.References)
                    {
                        var targets = r.Category == ImportReferenceCategory.Ambiguous ? r.Candidates : r.TargetPath != null ? new List<string> { r.TargetPath } : new List<string>();
                        foreach (var t in targets)
                        {
                            if (report.Find(t) != null) continue;
                            queue.Enqueue(await ScanAsync(t, false, false).ConfigureAwait(false));
                        }
                    }
                }
            }

            var headDoc = await ScanAsync(head, true, false).ConfigureAwait(false);
            await ClosureAsync(headDoc).ConfigureAwait(false);

            // Чертежи в папке головного файла (с подпапками) и в доп. папках: сборка не знает своих чертежей, поэтому их ищут
            // по ссылкам «чертёж → модель». Кандидаты — только чертежи: деталь или сборка из папки поиска, ссылающаяся на
            // комплект, — это вышестоящее или соседнее изделие, ему в комплекте не место. Сначала читаются только ссылки
            // (без копирования и без открытия в CAD); полностью читаются лишь чертежи, вошедшие в комплект.
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
                progress?.Report(new WorkProgress(WorkStage.Reading, checkedDrawings, drawingFiles.Count, $"Поиск чертежей: ссылки «{Path.GetFileName(path)}»"));
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
                candidates.Add((path, refs.Select(r => ClassifyImportReference(path, dir, r.SavedPath, searchFolders, nameIndex)).ToList()));
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
                    var d = await ScanAsync(c.Path, false, true).ConfigureAwait(false);
                    await ClosureAsync(d).ConfigureAwait(false);
                    changed = true;
                }
            } while (changed);

            // Уже учтённые в PDM (то же содержимое) — берутся из хранилища, а не заводятся заново.
            progress?.Report(new WorkProgress(WorkStage.Scanning, counter, counter, "Поиск уже учтённых файлов в хранилище"));
            await FindExistingAsync(report, ct).ConfigureAwait(false);

            // Решения по умолчанию: неоднозначные — без выбора, «только по имени» и «вне папок» — без подтверждения.
            report.Recompute();
            progress?.Report(new WorkProgress(WorkStage.Done, counter, counter, $"Прочитано документов: {report.Documents.Count}"));
            return report;
        }
        catch
        {
            report.Dispose();
            throw;
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

    private async Task<ImportDocument> ScanSourceAsync(ImportReport report, string source, int n, bool isHead, bool isExtra,
        Dictionary<string, int> working, List<string> searchFolders, Dictionary<string, List<string>> nameIndex, CancellationToken ct)
    {
        var copy = Path.Combine(report.StagingRoot, "files", n.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(source));
        string sha;
        long size;
        try
        {
            (sha, size) = await FileOps.CopyForReadingAsync(source, copy, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, IsHead = isHead, IsExtra = isExtra,
                ReadError = $"Не удалось прочитать «{source}»: {ex.Message}",
            };
        }

        var adapter = Adapters.FindFor(source);
        if (adapter == null)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, Sha256 = sha, Size = size, IsHead = isHead, IsExtra = isExtra, Kind = DocumentKind.Other,
                ReadError = $"Файл «{Path.GetFileName(source)}» ({source}) не является документом поддерживаемого CAD — поставить его на учёт нельзя.",
            };
        }

        CadDocumentInfo info;
        try
        {
            info = await ReadCadAsync(adapter, copy, sha, ct).ConfigureAwait(false);
        }
        catch (CadException ex)
        {
            return new ImportDocument
            {
                SourcePath = source, StagingPath = copy, Sha256 = sha, Size = size, IsHead = isHead, IsExtra = isExtra,
                Cad = adapter.CadId, Kind = adapter.KindOf(source),
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
            Kind = adapter.KindOf(source),
            CadVersion = info.CadVersion,
            CadVersionMajor = info.CadVersionMajor,
            IsNewerCadVersion = info.CadVersionMajor != null && working.TryGetValue(adapter.CadId, out var w) && info.CadVersionMajor > w,
            Designation = DocumentProperties.Designation(info.Properties),
            Name = DocumentProperties.Name(info.Properties),
            Configurations = info.Configurations,
            Properties = info.Properties,
            IsHead = isHead,
            IsExtra = isExtra,
        };

        var dir = Path.GetDirectoryName(source)!;
        foreach (var r in info.References)
        {
            doc.References.Add(ClassifyImportReference(source, dir, r.SavedPath, searchFolders, nameIndex));
        }
        return doc;
    }

    private static ImportReference ClassifyImportReference(string source, string dir, string saved, List<string> searchFolders, Dictionary<string, List<string>> nameIndex)
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
