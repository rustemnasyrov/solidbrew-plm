using System.Globalization;
using System.Text.RegularExpressions;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Настройки службы рабочих папок.</summary>
public sealed class WorkspaceServiceOptions
{
    /// <summary>Адрес сервера — записывается в состояние новой рабочей папки.</summary>
    public string? ServerUrl { get; set; }
}

/// <summary>
/// Клиентское ядро PDM: кэш, рабочие папки и операции раздела 7 ТЗ.
/// Операции, меняющие файлы, двухфазные: Prepare… возвращает план для показа пользователю, Execute… выполняет его.
/// Методы не привязаны к потоку UI; рабочая папка передаётся путём, состояние читается с диска при каждой операции.
/// </summary>
public sealed partial class WorkspaceService
{
    private readonly WorkspaceServiceOptions _options;
    private UserDto? _me;
    private string? _meToken;

    public WorkspaceService(IPdmApi api, CadAdapterRegistry adapters, LocalCache cache, WorkspaceServiceOptions? options = null)
    {
        Api = api ?? throw new ArgumentNullException(nameof(api));
        Adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options ?? new WorkspaceServiceOptions();
        ReadCache = new CadReadCache(Path.Combine(cache.Root, "cadinfo"));
    }

    public IPdmApi Api { get; }
    public CadAdapterRegistry Adapters { get; }
    public LocalCache Cache { get; }

    /// <summary>Кэш свойств CAD-файлов по SHA-256 (рядом с кэшем объектов).</summary>
    public CadReadCache ReadCache { get; }

    // ------------------------------------------------------------------ рабочая папка

    /// <summary>Папка является рабочей папкой SolidBrew.</summary>
    public static bool IsWorkspace(string workRoot) => WorkspaceState.Exists(workRoot);

    /// <summary>Прочитать состояние рабочей папки (для показа и диагностики).</summary>
    public WorkspaceState LoadState(string workRoot) => WorkspaceState.Load(workRoot);

    /// <summary>Сделать папку рабочей папкой проекта (пустое состояние). Если уже рабочая — проверяется проект.</summary>
    public WorkspaceState InitWorkspace(string workRoot, Guid projectId)
    {
        var existing = WorkspaceState.TryLoad(workRoot);
        if (existing != null)
        {
            if (existing.ProjectId != projectId)
                throw new WorkspaceException($"Папка «{workRoot}» уже привязана к другому проекту. Для каждого проекта нужна своя рабочая папка.");
            return existing;
        }
        Directory.CreateDirectory(workRoot);
        var state = new WorkspaceState { ProjectId = projectId, ServerUrl = _options.ServerUrl };
        state.Save(workRoot);
        return state;
    }

    // ------------------------------------------------------------------ общее

    /// <summary>Текущий пользователь (кэшируется до смены токена).</summary>
    public async Task<UserDto> GetMeAsync(CancellationToken ct = default)
    {
        if (_me != null && _meToken == Api.Token) return _me;
        var me = await Api.GetMeAsync(ct).ConfigureAwait(false);
        _me = me;
        _meToken = Api.Token;
        return me;
    }

    /// <summary>Рабочие версии CAD из настроек сервера: cadId → основной номер. Нет доступа — пусто.</summary>
    public async Task<Dictionary<string, int>> GetWorkingCadVersionsAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        SettingsDto settings;
        try
        {
            settings = await Api.GetSettingsAsync(ct).ConfigureAwait(false);
        }
        catch (PdmApiException ex) when (!ex.IsNetworkError)
        {
            return result;
        }
        foreach (var adapter in Adapters.All)
        {
            if (settings.Values.TryGetValue(SettingKeys.WorkingVersion(adapter.CadId), out var text) && text != null)
            {
                var m = Regex.Match(text, @"\d{2,4}");
                if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
                    result[adapter.CadId] = major;
            }
        }
        return result;
    }

    internal static string Root(string workRoot)
    {
        if (string.IsNullOrWhiteSpace(workRoot)) throw new WorkspaceException("Не задана рабочая папка.");
        return Path.GetFullPath(workRoot).TrimEnd('\\', '/');
    }

    internal ICadAdapter AdapterFor(string cad, string path) =>
        Adapters.Get(cad) ?? Adapters.FindFor(path) ??
        throw new WorkspaceException($"Нет адаптера CAD для документа «{Path.GetFileName(path)}» ({cad}). Установите поддержку этого CAD.");

    /// <summary>
    /// Прочитать документ CAD с кэшем свойств: если свойства файла с хешем <paramref name="sha256"/> уже читались,
    /// адаптер читает только ссылки (в SOLIDWORKS — без открытия документа). Иначе полное чтение, результат запоминается,
    /// если файл не менялся во время чтения.
    /// </summary>
    internal async Task<CadDocumentInfo> ReadCadAsync(ICadAdapter adapter, string fullPath, string? sha256, CancellationToken ct)
    {
        var info = await ReadCadRawAsync(adapter, fullPath, sha256, ct).ConfigureAwait(false);
        // Ссылки, убранные через PDM («Удалить ссылку»), не учитываются, даже если CAD оставил их в файле разорванными.
        DocumentProperties.ApplyRemovedReferences(info);
        return info;
    }

    private async Task<CadDocumentInfo> ReadCadRawAsync(ICadAdapter adapter, string fullPath, string? sha256, CancellationToken ct)
    {
        var kind = adapter.KindOf(fullPath);
        if (string.IsNullOrEmpty(sha256) || !(CadReadCache.IsCacheable(kind) || kind == DocumentKind.Drawing))
            return await adapter.ReadAsync(fullPath, ct).ConfigureAwait(false);

        // Чертёж: свойства бывают связаны со свойствами модели, поэтому ключ — хеш чертежа и хеши файлов, на которые он ссылается.
        List<CadReference>? refs = null;
        var key = sha256!;
        if (kind == DocumentKind.Drawing)
        {
            refs = await adapter.ReadReferencesAsync(fullPath, ct).ConfigureAwait(false);
            key = await DrawingCacheKeyAsync(fullPath, sha256!, refs, ct).ConfigureAwait(false);
        }

        if (ReadCache.TryGet(adapter.CadId, key) is { } cached)
        {
            cached.Path = fullPath;
            cached.Kind = kind;
            cached.References = refs ?? await adapter.ReadReferencesAsync(fullPath, ct).ConfigureAwait(false);
            return cached;
        }

        var before = FileOps.StampOf(fullPath);
        var read = await adapter.ReadAsync(fullPath, ct).ConfigureAwait(false);
        if (before != null && before.SameAs(FileOps.StampOf(fullPath)))
            ReadCache.Put(adapter.CadId, key, read);
        return read;
    }

    /// <summary>
    /// Ключ кэша чертежа: SHA-256 от хеша чертежа и пар «путь ссылки — хеш файла, который по ней найден».
    /// Файл ищется как в CAD: по пути из ссылки, затем с тем же именем в папке чертежа; не найден — отметка «нет файла».
    /// </summary>
    private async Task<string> DrawingCacheKeyAsync(string drawingPath, string drawingSha, List<CadReference> refs, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder("drawing:").Append(drawingSha).Append('\n');
        var dir = Path.GetDirectoryName(drawingPath)!;
        foreach (var r in refs.OrderBy(r => r.SavedPath, StringComparer.OrdinalIgnoreCase))
        {
            string? file = null;
            try
            {
                if (Path.IsPathFullyQualified(r.SavedPath) && File.Exists(r.SavedPath)) file = Path.GetFullPath(r.SavedPath);
                else if (r.FileName.Length > 0 && File.Exists(Path.Combine(dir, r.FileName))) file = Path.Combine(dir, r.FileName);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
            var hash = file == null ? "-" : await HashCachedAsync(file, ct).ConfigureAwait(false) ?? "-";
            sb.Append(r.SavedPath.ToUpperInvariant()).Append('|').Append(hash).Append('\n');
        }
        using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return SolidBrew.Core.Hashing.Sha256Hex.Compute(keyStream);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (FileStamp Stamp, string Sha)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Запомнить хеш файла, посчитанный по пути (например, при копировании), — для <see cref="HashCachedAsync"/>.</summary>
    private void RememberHash(string path, FileStamp stamp, string sha) => _hashes[Path.GetFullPath(path)] = (stamp, sha);

    /// <summary>Хеш файла с запоминанием по размеру и времени изменения (модели общие для многих чертежей). Ошибка чтения — null.</summary>
    private async Task<string?> HashCachedAsync(string path, CancellationToken ct)
    {
        var stamp = FileOps.StampOf(path);
        if (stamp == null) return null;
        if (_hashes.TryGetValue(path, out var known) && known.Stamp.SameAs(stamp)) return known.Sha;
        try
        {
            var sha = await FileOps.HashAsync(path, ct).ConfigureAwait(false);
            if (stamp.SameAs(FileOps.StampOf(path))) _hashes[path] = (stamp, sha);
            return sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Наибольший limit поиска, который принимает сервер (больше он молча ограничивает).</summary>
    internal const int ServerSearchLimit = 2000;

    /// <summary>
    /// Сведения о документах с сервера: сначала поиском по проекту (сервер отдаёт не больше <see cref="ServerSearchLimit"/>),
    /// недостающие — по одному.
    /// </summary>
    internal async Task<Dictionary<Guid, DocumentSummaryDto>> FetchSummariesAsync(Guid projectId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = new HashSet<Guid>(ids);
        var result = new Dictionary<Guid, DocumentSummaryDto>();
        if (wanted.Count == 0) return result;
        if (projectId != Guid.Empty && wanted.Count > 2)
        {
            var found = await Api.SearchDocumentsAsync(projectId: projectId, includeUnused: true, limit: ServerSearchLimit, ct: ct).ConfigureAwait(false);
            foreach (var d in found)
            {
                if (wanted.Contains(d.Id)) result[d.Id] = d;
            }
        }
        foreach (var id in wanted)
        {
            if (result.ContainsKey(id)) continue;
            try
            {
                var detail = await Api.GetDocumentAsync(id, ct).ConfigureAwait(false);
                result[id] = detail.Document;
            }
            catch (PdmApiException ex) when (ex.StatusCode == 404)
            {
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ локальный обход

    internal sealed class LocalFile
    {
        public string RelPath = string.Empty;
        public string FullPath = string.Empty;
        public bool Exists;
        public string? Sha;
        public long Size;
        public WorkspaceEntry? Entry;
        public ICadAdapter? Adapter;
        public bool IsModified => Entry != null && Exists && Sha != null && !string.Equals(Sha, Entry.LocalSha, StringComparison.Ordinal);
        public bool IsDraft => Entry == null && Adapter != null && Exists;
    }

    internal sealed class LocalScan
    {
        public string Root = string.Empty;
        public Dictionary<string, LocalFile> Files = new(StringComparer.Ordinal);
        public bool StateDirty;

        public LocalFile? Get(string relPath) => Files.TryGetValue(RelPath.Key(relPath), out var f) ? f : null;

        public IEnumerable<LocalFile> ByFileName(string fileName) =>
            Files.Values.Where(f => (f.Exists || f.Entry != null) && string.Equals(Texts.FileNameOf(f.RelPath), fileName, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsServiceFile(string relPath)
    {
        if (relPath.StartsWith(WorkspaceState.FolderName + "/", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Texts.FileNameOf(relPath);
        return name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal)
            || name.EndsWith(".sbtmp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Обойти рабочую папку: файлы state (с хешем, если отметка изменилась) и прочие файлы на диске.
    /// <paramref name="hashDrafts"/> — считать хеши черновиков.
    /// </summary>
    internal async Task<LocalScan> ScanAsync(string root, WorkspaceState state, bool hashDrafts, IProgress<WorkProgress>? progress, CancellationToken ct)
    {
        var scan = new LocalScan { Root = root };
        foreach (var e in state.Entries)
        {
            var full = RelPath.Combine(root, e.RelPath);
            scan.Files[RelPath.Key(e.RelPath)] = new LocalFile
            {
                RelPath = e.RelPath,
                FullPath = full,
                Entry = e,
                Adapter = Adapters.Get(e.Cad) ?? Adapters.FindFor(e.RelPath),
            };
        }

        if (Directory.Exists(root))
        {
            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel;
                try
                {
                    rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                    if (IsServiceFile(rel)) continue;
                    rel = RelPath.Normalize(rel);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                var key = RelPath.Key(rel);
                if (!scan.Files.TryGetValue(key, out var lf))
                {
                    lf = new LocalFile { RelPath = rel, FullPath = full, Adapter = Adapters.FindFor(rel) };
                    scan.Files[key] = lf;
                }
                lf.Exists = true;
            }
        }

        var toHash = scan.Files.Values.Where(f => f.Exists && (f.Entry != null || (hashDrafts && f.Adapter != null))).ToList();
        var i = 0;
        foreach (var f in toHash)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var stamp = FileOps.StampOf(f.FullPath);
            f.Size = stamp?.Length ?? 0;
            if (f.Entry != null && f.Entry.Stamp != null && f.Entry.Stamp.SameAs(stamp))
            {
                f.Sha = f.Entry.LocalSha;
                continue;
            }
            progress?.Report(new WorkProgress(WorkStage.Hashing, i, toHash.Count, $"Проверка «{Texts.FileNameOf(f.RelPath)}»"));
            try
            {
                f.Sha = await FileOps.HashAsync(f.FullPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new WorkspaceException($"Не удалось прочитать «{f.RelPath}»: {ex.Message}", ex);
            }
            if (f.Entry != null && f.Sha == f.Entry.LocalSha && stamp != null)
            {
                f.Entry.Stamp = stamp;
                scan.StateDirty = true;
            }
        }
        return scan;
    }

    // ------------------------------------------------------------------ статус

    /// <summary>
    /// Состояние рабочей папки: локальные изменения, черновики, блокировки и последние версии.
    /// Принудительно снятая блокировка (в папке была моя, на сервере нет) делает файл «только для чтения»; правки не трогаются.
    /// </summary>
    public async Task<WorkspaceStatus> GetStatusAsync(string workRoot, bool queryServer = true, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var scan = await ScanAsync(root, state, hashDrafts: false, progress, ct).ConfigureAwait(false);
        var status = new WorkspaceStatus { WorkRoot = root, ProjectId = state.ProjectId };

        Dictionary<Guid, DocumentSummaryDto>? server = null;
        UserDto? me = null;
        if (queryServer)
        {
            progress?.Report(new WorkProgress(WorkStage.Preparing, 0, 0, "Запрос состояния документов на сервере"));
            try
            {
                me = await GetMeAsync(ct).ConfigureAwait(false);
                server = await FetchSummariesAsync(state.ProjectId, state.Entries.Select(e => e.DocumentId), ct).ConfigureAwait(false);
                status.ServerChecked = true;
            }
            catch (PdmApiException ex)
            {
                status.ServerError = ex.Message;
                status.Messages.Add("Состояние на сервере не получено: " + ex.Message);
            }
        }

        foreach (var f in scan.Files.Values.OrderBy(f => f.RelPath, StringComparer.OrdinalIgnoreCase))
        {
            if (f.Entry == null && !f.IsDraft) continue;
            var fs = new FileStatus
            {
                RelPath = f.RelPath,
                FullPath = f.FullPath,
                IsReadOnly = f.Exists && FileOps.IsReadOnly(f.FullPath),
            };
            if (f.Entry == null)
            {
                fs.Local = LocalFileState.Draft;
                fs.Cad = f.Adapter!.CadId;
                fs.Kind = f.Adapter.KindOf(f.FullPath);
                status.Files.Add(fs);
                continue;
            }

            var e = f.Entry;
            fs.DocumentId = e.DocumentId;
            fs.ProjectFolder = e.ProjectFolder;
            fs.Cad = e.Cad;
            fs.Kind = e.Kind;
            fs.Designation = e.Designation;
            fs.Name = e.Name;
            fs.LocalVersion = e.Version;
            fs.Local = !f.Exists ? LocalFileState.MissingLocally : f.IsModified ? LocalFileState.ModifiedLocally : LocalFileState.Unchanged;
            fs.Lock = e.LockedByMe ? LockState.LockedByMe : LockState.Unknown;

            if (server != null && me != null && server.TryGetValue(e.DocumentId, out var doc))
            {
                fs.LatestVersion = doc.LatestVersion;
                fs.ServerStatus = doc.Status;
                fs.Designation = doc.Designation ?? fs.Designation;
                fs.Name = doc.Name ?? fs.Name;
                fs.LockInfo = doc.Lock;
                fs.Lock = doc.Lock == null ? LockState.Free : doc.Lock.UserId == me.Id ? LockState.LockedByMe : LockState.LockedByOther;

                if (e.LockedByMe && fs.Lock != LockState.LockedByMe)
                {
                    fs.LockRemovedByOthers = true;
                    e.LockedByMe = false;
                    scan.StateDirty = true;
                    if (f.Exists)
                    {
                        FileOps.SetReadOnly(f.FullPath, true);
                        fs.IsReadOnly = true;
                    }
                    var who = doc.Lock != null ? $" Сейчас документ взят: {doc.Lock.UserDisplayName}, с {Texts.Time(doc.Lock.At)}." : string.Empty;
                    var edits = fs.Local == LocalFileState.ModifiedLocally
                        ? " Ваши несданные правки сохранены в файле; чтобы сдать их, снова возьмите документ в работу (если он не изменился на сервере)."
                        : string.Empty;
                    status.Messages.Add($"Блокировку документа {Texts.Doc(e.RelPath, fs.Designation)} сняли принудительно; файл снова только для чтения.{who}{edits}");
                }
                else if (!e.LockedByMe && fs.Lock == LockState.LockedByMe && f.Exists && fs.IsReadOnly)
                {
                    status.Messages.Add($"Документ {Texts.Doc(e.RelPath, fs.Designation)} взят вами в работу в другом месте. Чтобы править его здесь, выполните «Взять в работу».");
                }
            }
            status.Files.Add(fs);
        }

        if (scan.StateDirty) state.Save(root);
        return status;
    }
}
