using System.Net;
using System.Text;
using System.Text.Json;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;

namespace SolidBrew.Core.Tests;

public class PdmApiClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<byte[]?> Bodies { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return await _respond(request);
        }
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value, PdmJson.Options), Encoding.UTF8, "application/json") };

    private static (PdmApiClient client, FakeHandler handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, string baseAddress = "http://pdm.local/")
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
        return (new PdmApiClient(http), handler);
    }

    [Fact]
    public async Task Login_PostsCredentials_AndStoresToken_ThenSendsBearer()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/api/auth/login" => Json(new LoginResponse { Token = "tok-1", User = new UserDto { Login = "ivanov" } }),
            "/api/me" => Json(new UserDto { Login = "ivanov", DisplayName = "Иванов И." }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var login = await client.LoginAsync("ivanov", "secret");
        Assert.Equal("tok-1", login.Token);
        Assert.Equal("tok-1", client.Token);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        var sent = JsonSerializer.Deserialize<LoginRequest>(handler.Bodies[0]!, PdmJson.Options)!;
        Assert.Equal("ivanov", sent.Login);
        Assert.Equal("secret", sent.Password);
        Assert.Contains("\"login\"", Encoding.UTF8.GetString(handler.Bodies[0]!));

        var me = await client.GetMeAsync();
        Assert.Equal("Иванов И.", me.DisplayName);
        Assert.Equal("Bearer", handler.Requests[1].Headers.Authorization!.Scheme);
        Assert.Equal("tok-1", handler.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Logout_ClearsToken()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        client.Token = "saved";
        await client.LogoutAsync();
        Assert.Null(client.Token);
        Assert.Equal("/api/auth/logout", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("saved", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Requests_UseExpectedMethodsAndPaths()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var (client, handler) = Create(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/lock") && r.Method == HttpMethod.Post) return Json(new LockDto { DocumentId = id });
            if (path.EndsWith("/kit")) return Json(new KitDto { HeadDocumentId = id, HeadVersion = 2 });
            if (path.EndsWith("/where-used")) return Json(new WhereUsedDto());
            if (path.EndsWith("/unused")) return Json(new DocumentSummaryDto { Id = id, Status = DocumentStatus.Unused });
            if (path == "/api/documents") return Json(new List<DocumentSummaryDto>());
            if (path == "/api/objects/missing") return Json(new MissingObjectsResponse { Missing = { "ab" } });
            if (path == "/api/checkins") return Json(new CheckinResponse { CheckinId = 7 });
            if (path.StartsWith("/api/admin/users/")) return Json(new UserDto { Id = id });
            if (path == "/api/admin/journal") return Json(new List<JournalEntryDto>());
            if (path == "/api/admin/settings") return Json(new SettingsDto());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        client.Token = "t";

        await client.LockAsync(id);
        await client.UnlockAsync(id);
        await client.GetKitAsync(id, 2);
        await client.GetWhereUsedAsync(id, 2);
        var doc = await client.MarkUnusedAsync(id, true);
        await client.SearchDocumentsAsync("АИРТ 467", projectId: id, includeUnused: true, limit: 50);
        var missing = await client.GetMissingObjectsAsync(new[] { "ab", "cd" });
        var checkin = await client.CheckinAsync(new CheckinRequest());
        await client.UpdateUserAsync(id, new UpdateUserRequest { Active = false });
        await client.ForceUnlockAsync(id);
        await client.GetJournalAsync(100);
        await client.UpdateSettingsAsync(new SettingsDto());
        await client.MarkNotificationsSeenAsync();

        var calls = handler.Requests.Select(r => $"{r.Method} {r.RequestUri!.PathAndQuery}").ToList();
        var g = id.ToString("D");
        Assert.Equal(new[]
        {
            $"POST /api/documents/{g}/lock",
            $"DELETE /api/documents/{g}/lock",
            $"GET /api/documents/{g}/versions/2/kit",
            $"GET /api/documents/{g}/versions/2/where-used",
            $"POST /api/documents/{g}/unused",
            $"GET /api/documents?q=%D0%90%D0%98%D0%A0%D0%A2%20467&projectId={g}&includeUnused=true&limit=50",
            "POST /api/objects/missing",
            "POST /api/checkins",
            $"PUT /api/admin/users/{g}",
            $"DELETE /api/admin/locks/{g}",
            "GET /api/admin/journal?limit=100",
            "PUT /api/admin/settings",
            "POST /api/me/notifications/seen",
        }, calls);
        Assert.All(handler.Requests, r => Assert.Equal("t", r.Headers.Authorization!.Parameter));
        Assert.Equal(DocumentStatus.Unused, doc.Status);
        Assert.Equal(new[] { "ab" }, missing);
        Assert.Equal(7, checkin.CheckinId);
        Assert.Contains("\"unused\":true", Encoding.UTF8.GetString(handler.Bodies[4]!));
        Assert.Equal("{\"active\":false}", Encoding.UTF8.GetString(handler.Bodies[8]!));
    }

    [Fact]
    public async Task BaseAddressWithSubPath_IsRespected()
    {
        var (client, handler) = Create(_ => Json(new List<ProjectDto>()), "http://host/pdm/");
        await client.GetProjectsAsync();
        Assert.Equal("http://host/pdm/api/projects", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Conflict_WithLock_IsParsedIntoException()
    {
        var lockDto = new LockDto { DocumentId = Guid.NewGuid(), UserLogin = "petrov", UserDisplayName = "Петров П." };
        var (client, _) = Create(_ => Json(new ErrorResponse
        {
            Message = "Документ взят в работу: Петров П.",
            Problems = { new ProblemDto { Code = ProblemCodes.LockedByOther, Message = "Занят" } },
            Lock = lockDto,
        }, HttpStatusCode.Conflict));

        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.LockAsync(lockDto.DocumentId));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Документ взят в работу: Петров П.", ex.Message);
        Assert.Equal("petrov", ex.Error!.Lock!.UserLogin);
        Assert.Equal(ProblemCodes.LockedByOther, ex.Error.Problems.Single().Code);
        Assert.False(ex.IsNetworkError);
    }

    [Fact]
    public async Task Conflict_WithLockButNoMessage_DescribesOwner()
    {
        var (client, _) = Create(_ => Json(new ErrorResponse { Lock = new LockDto { UserLogin = "petrov", UserDisplayName = "Петров П." } }, HttpStatusCode.Conflict));
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.LockAsync(Guid.NewGuid()));
        Assert.Contains("Петров П.", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Требуется вход")]
    [InlineData(HttpStatusCode.Forbidden, "Недостаточно прав")]
    [InlineData(HttpStatusCode.NotFound, "не найден")]
    [InlineData(HttpStatusCode.BadGateway, "Сервер недоступен")]
    [InlineData(HttpStatusCode.InternalServerError, "Сервер недоступен")]
    public async Task Errors_WithoutBody_GetDefaultMessage(HttpStatusCode status, string expected)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status) { Content = new StringContent("<html>oops</html>") });
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.GetMeAsync());
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Contains(expected, ex.Message);
        Assert.Null(ex.Error);
    }

    [Fact]
    public async Task NetworkError_IsWrapped()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("No connection could be made"));
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.GetProjectsAsync());
        Assert.Equal(0, ex.StatusCode);
        Assert.True(ex.IsNetworkError);
        Assert.StartsWith("Нет связи с сервером", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancellation_IsNotWrapped()
    {
        var handler = new FakeHandler(async r => { await Task.Delay(5000, CancellationToken.None); return new HttpResponseMessage(); });
        var client = new PdmApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://x/") });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetProjectsAsync(cts.Token));
    }

    [Fact]
    public async Task Upload_StreamsContentWithLengthAndProgress()
    {
        var data = new byte[3 * 1024 * 1024 + 17];
        new Random(1).NextBytes(data);
        var sha = new string('a', 64);
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.Created));
        client.Token = "t";
        var reports = new List<long>();
        var progress = new SyncProgress(reports.Add);

        using var source = new MemoryStream(data);
        await client.UploadObjectAsync(sha, source, data.Length, progress);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal($"/api/objects/{sha}", req.RequestUri!.AbsolutePath);
        Assert.Equal(data.Length, req.Content!.Headers.ContentLength);
        Assert.Equal("application/octet-stream", req.Content.Headers.ContentType!.MediaType);
        Assert.Equal(data, handler.Bodies[0]);
        Assert.Equal(data.Length, reports.Last());
        Assert.True(source.CanRead); // поток вызывающего не закрыт
    }

    [Fact]
    public async Task Download_WritesToDestinationWithProgress()
    {
        var data = Encoding.UTF8.GetBytes(new string('ж', 100_000));
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        var reports = new List<long>();
        using var dest = new MemoryStream();

        await client.DownloadObjectAsync(new string('b', 64), dest, new SyncProgress(reports.Add));

        Assert.Equal(data, dest.ToArray());
        Assert.Equal(data.Length, reports.Last());
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task Export_UsesDerivedQuery()
    {
        var id = Guid.NewGuid();
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) });
        using var dest = new MemoryStream();
        await client.ExportAsync(id, 4, includeDerived: false, dest);
        Assert.Equal($"/api/documents/{id:D}/versions/4/export?derived=false", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal(2, dest.Length);
    }

    [Fact]
    public async Task Download_NotFound_Throws()
    {
        var (client, _) = Create(_ => Json(new ErrorResponse { Message = "Объект не найден в архиве." }, HttpStatusCode.NotFound));
        using var dest = new MemoryStream();
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.DownloadObjectAsync(new string('c', 64), dest, null));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("Объект не найден в архиве.", ex.Message);
        Assert.Equal(0, dest.Length);
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
