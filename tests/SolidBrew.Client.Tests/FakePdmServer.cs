using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using SolidBrew.Core.Paths;

namespace SolidBrew.Client.Tests;

/// <summary>
/// Копия из SolidBrew.Workspace.Tests (с уведомлениями и отказом входа).
/// Сервер PDM в памяти с честной семантикой: блокировки, номера версий, проверки сдачи (блокировка, исходная версия,
/// объекты, зависимости, пути, версия CAD), атомарная публикация, комплект по ссылкам, «где применяется».
/// Несколько пользователей — несколько <see cref="FakePdmApi"/> над одним сервером.
/// </summary>
public sealed class FakePdmServer
{
    private readonly object _sync = new();
    private long _checkinId;

    public List<UserDto> Users { get; } = new();
    public List<ProjectDto> Projects { get; } = new();
    public Dictionary<Guid, FakeDoc> Docs { get; } = new();
    public Dictionary<string, byte[]> Objects { get; } = new();
    public Dictionary<string, string> Settings { get; } = new();
    public List<CheckinRequest> Checkins { get; } = new();
    public List<NotificationDto> Notifications { get; } = new();

    /// <summary>Сколько раз скачивались объекты.</summary>
    public int DownloadCount;
    public int UploadCount;
    /// <summary>Подмена содержимого при скачивании (проверка хеша в кэше).</summary>
    public Func<string, byte[], byte[]>? DownloadTamper { get; set; }
    /// <summary>Отказ сдачи: вернуть исключение — сдача не выполняется.</summary>
    public Func<CheckinRequest, PdmApiException?>? CheckinFailure { get; set; }
    /// <summary>Предел числа записей поиска, как у настоящего сервера (он ограничивает limit сверху).</summary>
    public int SearchLimitCap { get; set; } = 2000;

    public UserDto AddUser(string login, string displayName, UserRole role = UserRole.Designer)
    {
        var u = new UserDto { Id = Guid.NewGuid(), Login = login, DisplayName = displayName, Role = role, Active = true };
        Users.Add(u);
        return u;
    }

    public ProjectDto AddProject(string name, ProjectKind kind = ProjectKind.Project)
    {
        var p = new ProjectDto { Id = Guid.NewGuid(), Name = name, Kind = kind };
        Projects.Add(p);
        return p;
    }

    public FakePdmApi Connect(string login) => new(this, Users.First(u => u.Login == login));

    public FakeDoc Doc(Guid id) => Docs[id];

    public FakeDoc DocByPath(string relPath) => Docs.Values.Single(d => string.Equals(d.Latest.RelPath, relPath, StringComparison.OrdinalIgnoreCase));

    internal static PdmApiException Error(int status, string message, params ProblemDto[] problems) =>
        new(status, message, new ErrorResponse { Message = message, Problems = problems.ToList() });

    // ---------------------------------------------------------------- операции (вызываются из FakePdmApi под замком)

    internal T Locked<T>(Func<T> f)
    {
        lock (_sync) return f();
    }

    internal DocumentSummaryDto Summary(FakeDoc d)
    {
        var v = d.Latest;
        var p = Projects.First(x => x.Id == d.ProjectId);
        return new DocumentSummaryDto
        {
            Id = d.Id,
            ProjectId = d.ProjectId,
            ProjectName = p.Name,
            ProjectKind = p.Kind,
            Cad = v.Cad,
            Kind = v.Kind,
            Status = d.Status,
            RelPath = v.RelPath,
            FileName = RelPath.FileName(v.RelPath),
            LatestVersion = v.Number,
            Designation = DocumentProperties.Designation(v.Properties),
            Name = DocumentProperties.Name(v.Properties),
            LatestAuthorLogin = v.Author.Login,
            LatestAuthorDisplayName = v.Author.DisplayName,
            LatestAt = v.At,
            Lock = Clone(d.Lock),
            UsedInCount = UsedInCount(d),
        };
    }

    /// <summary>Сколько активных документов (кроме чертежей) ссылаются последней версией на какую-либо версию документа.</summary>
    internal int UsedInCount(FakeDoc d) => Docs.Values.Count(p => p.Id != d.Id && p.Status == DocumentStatus.Active &&
        p.Latest.Kind != DocumentKind.Drawing && p.Latest.Refs.Any(r => r.Child == d.Id));

    internal static LockDto? Clone(LockDto? l) => l == null ? null : new LockDto { DocumentId = l.DocumentId, UserId = l.UserId, UserLogin = l.UserLogin, UserDisplayName = l.UserDisplayName, At = l.At };

    internal VersionDto VersionDto(FakeDoc d, FakeVersion v) => new()
    {
        DocumentId = d.Id,
        Number = v.Number,
        AuthorLogin = v.Author.Login,
        AuthorDisplayName = v.Author.DisplayName,
        At = v.At,
        CheckinId = v.CheckinId,
        Comment = v.Comment,
        Sha256 = v.Sha256,
        Size = v.Size,
        RelPath = v.RelPath,
        Cad = v.Cad,
        Kind = v.Kind,
        CadVersion = v.CadVersion,
        CadVersionMajor = v.CadVersionMajor,
        Configurations = v.Configurations.ToList(),
        Properties = v.Properties.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
        References = v.Refs.Select(r => new VersionRefDto
        {
            RefPath = r.RefPath,
            ChildDocumentId = r.Child,
            ChildVersion = r.ChildVersion,
            ChildRelPath = r.Child != null ? Docs[r.Child.Value].Versions.First(x => x.Number == r.ChildVersion).RelPath : null,
            ChildLatestVersion = r.Child != null ? Docs[r.Child.Value].Latest.Number : null,
        }).ToList(),
    };

    internal KitDto Kit(Guid id, int version, bool includeDrawings = false)
    {
        if (!Docs.TryGetValue(id, out var head) || head.Versions.All(v => v.Number != version))
            throw Error(404, "Версия документа не найдена.");
        var kit = new KitDto { HeadDocumentId = id, HeadVersion = version };
        var seen = new HashSet<(Guid, int)>();
        var queue = new Queue<(Guid, int)>();
        queue.Enqueue((id, version));
        seen.Add((id, version));
        while (queue.Count > 0)
        {
            var (did, ver) = queue.Dequeue();
            var d = Docs[did];
            var v = d.Versions.First(x => x.Number == ver);
            kit.Items.Add(new KitItemDto
            {
                DocumentId = did,
                Version = ver,
                LatestVersion = d.Latest.Number,
                RelPath = v.RelPath,
                Sha256 = v.Sha256,
                Size = v.Size,
                Cad = v.Cad,
                Kind = v.Kind,
                CadVersion = v.CadVersion,
                CadVersionMajor = v.CadVersionMajor,
                Designation = DocumentProperties.Designation(v.Properties),
                Name = DocumentProperties.Name(v.Properties),
                Status = d.Status,
                Lock = Clone(d.Lock),
                ProjectId = d.ProjectId,
            });
            foreach (var r in v.Refs)
            {
                if (r.Child == null)
                {
                    kit.Unresolved.Add(new KitUnresolvedRefDto { ParentDocumentId = did, ParentVersion = ver, RefPath = r.RefPath });
                    continue;
                }
                kit.Edges.Add(new KitEdgeDto { ParentDocumentId = did, ParentVersion = ver, ChildDocumentId = r.Child.Value, ChildVersion = r.ChildVersion!.Value, RefPath = r.RefPath });
                if (seen.Add((r.Child.Value, r.ChildVersion.Value))) queue.Enqueue((r.Child.Value, r.ChildVersion.Value));
            }
        }
        return kit;
    }

    internal WhereUsedDto WhereUsed(Guid id, int version)
    {
        if (!Docs.TryGetValue(id, out var child)) throw Error(404, "Документ не найден.");
        var dto = new WhereUsedDto();
        foreach (var d in Docs.Values)
        {
            foreach (var v in d.Versions)
            {
                foreach (var r in v.Refs.Where(r => r.Child == id))
                {
                    dto.Items.Add(new WhereUsedItemDto
                    {
                        ChildVersion = r.ChildVersion!.Value,
                        ParentDocumentId = d.Id,
                        ParentVersion = v.Number,
                        ParentRelPath = v.RelPath,
                        ParentDesignation = DocumentProperties.Designation(v.Properties),
                        ParentName = DocumentProperties.Name(v.Properties),
                        ParentLatestVersion = d.Latest.Number,
                        IsParentLatest = v.Number == d.Latest.Number,
                        UsesLatestChild = r.ChildVersion == child.Latest.Number,
                    });
                }
            }
        }
        return dto;
    }

    internal CheckinResponse Checkin(UserDto user, CheckinRequest req)
    {
        Checkins.Add(req);
        var problems = new List<ProblemDto>();
        void P(string code, string msg, CheckinItem? it = null) =>
            problems.Add(new ProblemDto { Code = code, Message = msg, DocumentId = it?.DocumentId, TempKey = it?.TempKey, RelPath = it?.RelPath });

        if (Projects.All(p => p.Id != req.ProjectId)) P(ProblemCodes.NotFound, "Проект не найден.");
        var tempKeys = new HashSet<string>(req.Items.Where(i => i.TempKey != null).Select(i => i.TempKey!));
        var docIds = new HashSet<Guid>(req.Items.Where(i => i.DocumentId != null).Select(i => i.DocumentId!.Value));
        var lockProblem = false;

        foreach (var it in req.Items)
        {
            if (it.DocumentId != null)
            {
                if (!Docs.TryGetValue(it.DocumentId.Value, out var d)) { P(ProblemCodes.NotFound, "Документ не найден.", it); continue; }
                if (d.Lock == null) { P(ProblemCodes.NotLocked, "Документ не взят в работу.", it); lockProblem = true; }
                else if (d.Lock.UserId != user.Id) { P(ProblemCodes.LockedByOther, $"Документ взят в работу: {d.Lock.UserDisplayName}.", it); lockProblem = true; }
                if (it.BaseVersion != d.Latest.Number) P(ProblemCodes.StaleBaseVersion, $"Исходная версия устарела: последняя v{d.Latest.Number}.", it);
            }
            else if (string.IsNullOrEmpty(it.TempKey)) P(ProblemCodes.Validation, "Не задан ни документ, ни временный ключ.", it);

            if (!Objects.TryGetValue(it.Sha256, out var bytes)) P(ProblemCodes.MissingObject, "Содержимое не загружено.", it);
            else if (bytes.Length != it.Size) P(ProblemCodes.HashMismatch, "Размер не совпадает.", it);
            if (!RelPath.IsValid(it.RelPath)) P(ProblemCodes.Validation, "Неверный относительный путь.", it);

            foreach (var r in it.References)
            {
                if (r.DocumentId != null && r.Version != null)
                {
                    if (!Docs.TryGetValue(r.DocumentId.Value, out var c) || c.Versions.All(v => v.Number != r.Version))
                        P(ProblemCodes.MissingDependency, $"Зависимость «{r.RefPath}» не найдена в хранилище.", it);
                }
                else if (r.DocumentId != null)
                {
                    if (!docIds.Contains(r.DocumentId.Value)) P(ProblemCodes.MissingDependency, $"Зависимость «{r.RefPath}» должна быть в этой же сдаче.", it);
                }
                else if (r.TempKey != null)
                {
                    if (!tempKeys.Contains(r.TempKey)) P(ProblemCodes.MissingDependency, $"Новый документ «{r.TempKey}» не найден в сдаче.", it);
                }
                else if (!req.AllowUnresolvedReferences)
                {
                    P(ProblemCodes.UnresolvedReference, $"Отсутствующая ссылка «{r.RefPath}».", it);
                }
            }

            if (it.CadVersionMajor != null && Settings.TryGetValue(SettingKeys.WorkingVersion(it.Cad), out var w) &&
                int.TryParse(w, out var wv) && it.CadVersionMajor > wv && !req.AcceptNewerCadVersion)
                P(ProblemCodes.NewerCadVersion, "Файл сохранён в версии CAD новее рабочей.", it);
        }

        foreach (var g in req.Items.GroupBy(i => RelPath.Key(i.RelPath)).Where(g => g.Count() > 1))
            P(ProblemCodes.DuplicatePath, $"Путь «{g.First().RelPath}» повторяется в сдаче.");
        foreach (var it in req.Items)
        {
            var clash = Docs.Values.FirstOrDefault(d => d.ProjectId == req.ProjectId && !docIds.Contains(d.Id) &&
                RelPath.Key(d.Latest.RelPath) == RelPath.Key(it.RelPath));
            if (clash != null) P(ProblemCodes.DuplicatePath, $"Путь «{it.RelPath}» уже занят другим документом.", it);
        }

        if (problems.Count == 0 && CheckinFailure?.Invoke(req) is { } injected) throw injected;
        if (problems.Count > 0)
            throw new PdmApiException(lockProblem ? 409 : 422, "Сдача отклонена: " + problems[0].Message,
                new ErrorResponse { Message = "Сдача отклонена.", Problems = problems });

        // Публикация.
        var checkinId = ++_checkinId;
        var at = DateTimeOffset.UtcNow;
        var newIds = req.Items.Where(i => i.TempKey != null && i.DocumentId == null).ToDictionary(i => i.TempKey!, _ => Guid.NewGuid());
        var newNumbers = new Dictionary<Guid, int>();
        foreach (var it in req.Items)
        {
            var id = it.DocumentId ?? newIds[it.TempKey!];
            newNumbers[id] = it.DocumentId != null ? Docs[id].Latest.Number + 1 : 1;
        }
        var response = new CheckinResponse { CheckinId = checkinId };
        foreach (var it in req.Items)
        {
            var id = it.DocumentId ?? newIds[it.TempKey!];
            if (!Docs.TryGetValue(id, out var d))
            {
                d = new FakeDoc { Id = id, ProjectId = req.ProjectId };
                Docs[id] = d;
            }
            var v = new FakeVersion
            {
                Number = newNumbers[id],
                Sha256 = it.Sha256,
                Size = it.Size,
                RelPath = it.RelPath,
                Cad = it.Cad,
                Kind = it.Kind,
                CadVersion = it.CadVersion,
                CadVersionMajor = it.CadVersionMajor,
                Configurations = it.Configurations.ToList(),
                Properties = it.Properties.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
                Author = user,
                At = at,
                Comment = req.Comment,
                CheckinId = checkinId,
            };
            foreach (var r in it.References)
            {
                Guid? child = null;
                int? cv = null;
                if (r.DocumentId != null && r.Version != null) { child = r.DocumentId; cv = r.Version; }
                else if (r.DocumentId != null) { child = r.DocumentId; cv = newNumbers[r.DocumentId.Value]; }
                else if (r.TempKey != null) { child = newIds[r.TempKey]; cv = 1; }
                v.Refs.Add(new FakeRef(r.RefPath, child, cv));
            }
            d.Versions.Add(v);
            if (it.DocumentId != null && !req.KeepLocks) d.Lock = null;
            response.Items.Add(new CheckinResultItem { TempKey = it.TempKey, DocumentId = id, Version = v.Number, RelPath = it.RelPath });
        }
        return response;
    }

    internal void WriteExport(Guid id, int version, Stream destination)
    {
        var kit = Kit(id, version);
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = new ExportManifest { HeadDocumentId = id, HeadVersion = version, CreatedAt = DateTimeOffset.UtcNow };
        foreach (var it in kit.Items)
        {
            var entry = zip.CreateEntry(it.RelPath);
            using (var s = entry.Open()) s.Write(Objects[it.Sha256]);
            manifest.Files.Add(new ExportManifestFile { RelPath = it.RelPath, DocumentId = it.DocumentId, Version = it.Version, Sha256 = it.Sha256, Size = it.Size });
        }
        var m = zip.CreateEntry(ExportManifest.FileName);
        using var ms = m.Open();
        JsonSerializer.Serialize(ms, manifest, PdmJson.Options);
    }
}

public sealed class FakeDoc
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DocumentStatus Status { get; set; }
    public LockDto? Lock { get; set; }
    public List<FakeVersion> Versions { get; } = new();
    public FakeVersion Latest => Versions[^1];
}

public sealed class FakeVersion
{
    public int Number { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string RelPath { get; set; } = string.Empty;
    public string Cad { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; }
    public string? CadVersion { get; set; }
    public int? CadVersionMajor { get; set; }
    public List<string> Configurations { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> Properties { get; set; } = new();
    public List<FakeRef> Refs { get; } = new();
    public UserDto Author { get; set; } = new();
    public DateTimeOffset At { get; set; }
    public string? Comment { get; set; }
    public long CheckinId { get; set; }
}

public sealed record FakeRef(string RefPath, Guid? Child, int? ChildVersion);

/// <summary>Клиент API одного пользователя над <see cref="FakePdmServer"/>.</summary>
public sealed class FakePdmApi : IPdmApi
{
    private readonly FakePdmServer _s;

    public FakePdmApi(FakePdmServer server, UserDto user)
    {
        _s = server;
        User = user;
        Token = "token-" + user.Login;
    }

    public UserDto User { get; }
    public string? Token { get; set; }
    /// <summary>Имитация обрыва связи.</summary>
    public bool Offline { get; set; }

    private Task<T> Run<T>(Func<T> f)
    {
        if (Offline) return Task.FromException<T>(new PdmApiException(0, PdmApiException.DefaultMessage(0)));
        if (ConnectFailure != null) return Task.FromException<T>(ConnectFailure);
        try
        {
            return Task.FromResult(_s.Locked(f));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private Task Run(Action a) => Run(() => { a(); return 0; });

    private FakeDoc Doc(Guid id) => _s.Docs.TryGetValue(id, out var d) ? d : throw FakePdmServer.Error(404, "Документ не найден.");

    /// <summary>Отказ любого обращения на уровне соединения (например, недоверенный сертификат).</summary>
    public PdmApiException? ConnectFailure { get; set; }

    /// <summary>Отказ входа (неверный пароль и т. п.).</summary>
    public PdmApiException? LoginFailure { get; set; }

    public int LoginCalls;

    public Task<LoginResponse> LoginAsync(string login, string password, CancellationToken ct = default) => Run(() =>
    {
        LoginCalls++;
        if (LoginFailure != null) throw LoginFailure;
        Token = "token-" + User.Login;
        return new LoginResponse { Token = Token, User = User, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
    });

    public Task LogoutAsync(CancellationToken ct = default) => Run(() => { Token = null; });

    public Task<UserDto> GetMeAsync(CancellationToken ct = default) => Run(() => User);

    public Task<List<NotificationDto>> GetNotificationsAsync(CancellationToken ct = default) => Run(() => _s.Notifications.ToList());

    public Task MarkNotificationsSeenAsync(CancellationToken ct = default) => Run(() => { foreach (var n in _s.Notifications) n.Seen = true; });

    public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default) => Run(() => _s.Projects.ToList());

    public Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default) => Run(() => _s.AddProject(request.Name, request.Kind));

    public Task<List<DocumentSummaryDto>> SearchDocumentsAsync(string? q = null, Guid? projectId = null, string? author = null, bool includeUnused = false, int? limit = null, bool roots = false, CancellationToken ct = default) =>
        Run(() => _s.Docs.Values
            .Where(d => projectId == null || d.ProjectId == projectId)
            .Where(d => includeUnused || d.Status == DocumentStatus.Active)
            .Where(d => !roots || (d.Status == DocumentStatus.Active && d.Latest.Kind == DocumentKind.Assembly && _s.UsedInCount(d) == 0))
            .Select(_s.Summary)
            .Where(d => q == null || d.RelPath.Contains(q, StringComparison.OrdinalIgnoreCase) || (d.Designation ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit ?? 50, 1, _s.SearchLimitCap)).ToList());

    public Task<DocumentDetailDto> GetDocumentAsync(Guid id, CancellationToken ct = default) => Run(() =>
    {
        var d = Doc(id);
        return new DocumentDetailDto
        {
            Document = _s.Summary(d),
            Versions = d.Versions.OrderByDescending(v => v.Number).Select(v => _s.VersionDto(d, v)).ToList(),
        };
    });

    public Task<KitDto> GetKitAsync(Guid id, int version, bool includeDrawings = false, CancellationToken ct = default) => Run(() => _s.Kit(id, version, includeDrawings));

    public Task<WhereUsedDto> GetWhereUsedAsync(Guid id, int version, CancellationToken ct = default) => Run(() => _s.WhereUsed(id, version));

    public Task ExportAsync(Guid id, int version, bool includeDerived, Stream destination, CancellationToken ct = default) => Run(() => _s.WriteExport(id, version, destination));

    public Task<List<JournalEntryDto>> GetDocumentJournalAsync(Guid id, CancellationToken ct = default) => Run(() => new List<JournalEntryDto>());

    public Task<LockDto> LockAsync(Guid id, CancellationToken ct = default) => Run(() =>
    {
        var d = Doc(id);
        if (d.Lock != null && d.Lock.UserId != User.Id)
            throw new PdmApiException(409, $"Документ взят в работу: {d.Lock.UserDisplayName}.", new ErrorResponse { Message = "Документ занят.", Lock = FakePdmServer.Clone(d.Lock) });
        d.Lock ??= new LockDto { DocumentId = id, UserId = User.Id, UserLogin = User.Login, UserDisplayName = User.DisplayName, At = DateTimeOffset.UtcNow };
        return FakePdmServer.Clone(d.Lock)!;
    });

    public Task UnlockAsync(Guid id, CancellationToken ct = default) => Run(() =>
    {
        var d = Doc(id);
        if (d.Lock == null) throw FakePdmServer.Error(409, "Документ не взят в работу.");
        if (d.Lock.UserId != User.Id) throw new PdmApiException(409, "Документ взят другим.", new ErrorResponse { Message = "Документ взят другим.", Lock = FakePdmServer.Clone(d.Lock) });
        d.Lock = null;
    });

    public Task<DocumentSummaryDto> MarkUnusedAsync(Guid id, bool unused, CancellationToken ct = default) => Run(() =>
    {
        var d = Doc(id);
        d.Status = unused ? DocumentStatus.Unused : DocumentStatus.Active;
        return _s.Summary(d);
    });

    public Task<List<string>> GetMissingObjectsAsync(IEnumerable<string> hashes, CancellationToken ct = default) =>
        Run(() => hashes.Distinct().Where(h => !_s.Objects.ContainsKey(h)).ToList());

    public Task UploadObjectAsync(string sha256, Stream content, long size, IProgress<long>? progress, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        content.CopyTo(ms);
        var bytes = ms.ToArray();
        return Run(() =>
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (actual != sha256 || bytes.Length != size) throw FakePdmServer.Error(400, "Хеш загруженного объекта не совпадает.");
            _s.Objects[sha256] = bytes;
            _s.UploadCount++;
            progress?.Report(bytes.Length);
        });
    }

    public Task DownloadObjectAsync(string sha256, Stream destination, IProgress<long>? progress, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            bytes = _s.Locked(() =>
            {
                if (!_s.Objects.TryGetValue(sha256, out var b)) throw FakePdmServer.Error(404, "Объект не найден.");
                _s.DownloadCount++;
                return _s.DownloadTamper?.Invoke(sha256, b) ?? b;
            });
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
        return WriteAsync();

        async Task WriteAsync()
        {
            await destination.WriteAsync(bytes, ct);
            progress?.Report(bytes.Length);
        }
    }

    public Task<CheckinResponse> CheckinAsync(CheckinRequest request, CancellationToken ct = default) => Run(() => _s.Checkin(User, request));

    public Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default) => Run(() => _s.Users.ToList());

    public Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default) => Run(() => _s.AddUser(request.Login, request.DisplayName, request.Role));

    public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) => Run(() =>
    {
        var u = _s.Users.First(x => x.Id == id);
        if (request.Role != null) u.Role = request.Role.Value;
        if (request.Active != null) u.Active = request.Active.Value;
        if (request.DisplayName != null) u.DisplayName = request.DisplayName;
        return u;
    });

    public Task<List<LockDto>> GetLocksAsync(CancellationToken ct = default) =>
        Run(() => _s.Docs.Values.Where(d => d.Lock != null).Select(d => FakePdmServer.Clone(d.Lock)!).ToList());

    public Task ForceUnlockAsync(Guid documentId, CancellationToken ct = default) => Run(() => { Doc(documentId).Lock = null; });

    public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        Run(() => new SettingsDto { Values = new Dictionary<string, string>(_s.Settings) });

    public Task<SettingsDto> UpdateSettingsAsync(SettingsDto settings, CancellationToken ct = default) => Run(() =>
    {
        foreach (var p in settings.Values) _s.Settings[p.Key] = p.Value;
        return new SettingsDto { Values = new Dictionary<string, string>(_s.Settings) };
    });

    public Task<List<JournalEntryDto>> GetJournalAsync(int? limit = null, CancellationToken ct = default) => Run(() => new List<JournalEntryDto>());
}
