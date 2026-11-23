using System.Net;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

[Collection(PdmCollection.Name)]
public sealed class AuthTests(PdmServerFixture fx)
{
    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        var response = await fx.NewHttpClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_Me_Logout()
    {
        var (client, user) = await fx.NewUserAsync(UserRole.Designer);
        var me = await client.GetMeAsync();
        Assert.Equal(user.Id, me.Id);
        Assert.Equal(UserRole.Designer, me.Role);

        var token = client.Token;
        await client.LogoutAsync();
        Assert.Null(client.Token);

        client.Token = token;
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.GetMeAsync());
        Assert.Equal(401, ex.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.Error?.Message));
    }

    [Fact]
    public async Task WrongPassword_Is401WithRussianMessage()
    {
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => fx.NewClient().LoginAsync("admin", "wrong-password"));
        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Неверное имя пользователя или пароль", ex.Message);

        var unknown = await Assert.ThrowsAsync<PdmApiException>(() => fx.NewClient().LoginAsync("nobody-here", "whatever-pass"));
        Assert.Equal(401, unknown.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401_AndUnknownRoute_HasErrorBody()
    {
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => fx.NewClient().GetProjectsAsync());
        Assert.Equal(401, ex.StatusCode);
        Assert.NotNull(ex.Error);

        var admin = await fx.AdminAsync();
        var http = fx.NewHttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin.Token);
        var response = await http.GetAsync("/api/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("message", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DisabledUser_CannotLogin_AndSessionsEnd()
    {
        var admin = await fx.AdminAsync();
        var (client, user) = await fx.NewUserAsync(UserRole.Designer, admin);
        await admin.UpdateUserAsync(user.Id, new UpdateUserRequest { Active = false });

        Assert.Equal(401, (await Assert.ThrowsAsync<PdmApiException>(() => client.GetMeAsync())).StatusCode);
        Assert.Equal(401, (await Assert.ThrowsAsync<PdmApiException>(() => fx.NewClient().LoginAsync(user.Login, PdmServerFixture.UserPassword))).StatusCode);
    }

    [Fact]
    public async Task Admin_UserValidation()
    {
        var admin = await fx.AdminAsync();
        var shortPassword = await Assert.ThrowsAsync<PdmApiException>(() =>
            admin.CreateUserAsync(new CreateUserRequest { Login = fx.Unique("u"), Password = "short", Role = UserRole.Designer }));
        Assert.Equal(400, shortPassword.StatusCode);
        Assert.Equal(ProblemCodes.Validation, shortPassword.Error!.Problems.Single().Code);

        var (_, user) = await fx.NewUserAsync(UserRole.Reader, admin);
        var duplicate = await Assert.ThrowsAsync<PdmApiException>(() =>
            admin.CreateUserAsync(new CreateUserRequest { Login = user.Login.ToUpperInvariant(), Password = "long-enough-1", Role = UserRole.Designer }));
        Assert.Equal(409, duplicate.StatusCode);

        var users = await admin.GetUsersAsync();
        Assert.Contains(users, u => u.Login == "admin" && u.Role == UserRole.Admin);
    }

    [Fact]
    public async Task Reader_CannotLockOrCheckin_ButCanRead()
    {
        var admin = await fx.AdminAsync();
        var project = await fx.NewProjectAsync(admin);
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var part = await NewAsync(designer, "p", "Деталь.prt", DocumentKind.Part, "part-r1");
        var published = await designer.CheckinAsync(Request(project.Id, part));
        var id = published.Items[0].DocumentId;

        var (reader, _) = await fx.NewUserAsync(UserRole.Reader, admin);
        var lockEx = await Assert.ThrowsAsync<PdmApiException>(() => reader.LockAsync(id));
        Assert.Equal(403, lockEx.StatusCode);
        Assert.Contains("читатель", lockEx.Message);

        var item = new CheckinItem { TempKey = "x", RelPath = "x.prt", Sha256 = part.Sha256, Size = part.Size, Cad = Cad, Kind = DocumentKind.Part };
        var checkinEx = await Assert.ThrowsAsync<PdmApiException>(() => reader.CheckinAsync(Request(project.Id, item)));
        Assert.Equal(403, checkinEx.StatusCode);

        var detail = await reader.GetDocumentAsync(id);
        Assert.Equal(1, detail.Document.LatestVersion);
        Assert.Equal(403, (await Assert.ThrowsAsync<PdmApiException>(() => reader.CreateProjectAsync(new CreateProjectRequest { Name = "нельзя" }))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<PdmApiException>(() => reader.GetLocksAsync())).StatusCode);
    }

    [Fact]
    public async Task Designer_CannotWriteLibrary_AdminCan()
    {
        var admin = await fx.AdminAsync();
        var library = await fx.LibraryAsync();
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer, admin);

        var bolt = await NewAsync(designer, "bolt", fx.Unique("Крепёж/Болт-") + ".prt", DocumentKind.Part, "bolt-" + Guid.NewGuid());
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => designer.CheckinAsync(Request(library.Id, bolt)));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains(ex.Error!.Problems, p => p.Code == ProblemCodes.LibraryReadOnly);

        var published = await admin.CheckinAsync(Request(library.Id, bolt));
        var boltId = published.Items[0].DocumentId;

        var lockEx = await Assert.ThrowsAsync<PdmApiException>(() => designer.LockAsync(boltId));
        Assert.Equal(403, lockEx.StatusCode);
        Assert.Equal(ProblemCodes.LibraryReadOnly, lockEx.Error!.Problems.Single().Code);

        // Конструктор ссылается на библиотечную версию из своего проекта.
        var project = await fx.NewProjectAsync(admin);
        var asm = await NewAsync(designer, "asm", "Сборка.asm", DocumentKind.Assembly, "asm-lib-" + Guid.NewGuid(), RefPublished("Болт.prt", boltId, 1));
        var result = await designer.CheckinAsync(Request(project.Id, asm));
        var kit = await designer.GetKitAsync(result.Items[0].DocumentId, 1);
        Assert.Contains(kit.Items, i => i.DocumentId == boltId && i.ProjectId == library.Id);
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        var admin = await fx.AdminAsync();
        var key = SettingKeys.WorkingVersion("othercad");
        var saved = await admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "2022" } });
        Assert.Equal("2022", saved.Values[key]);
        var bad = await Assert.ThrowsAsync<PdmApiException>(() => admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "новая" } }));
        Assert.Equal(400, bad.StatusCode);
        saved = await admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "" } });
        Assert.False(saved.Values.ContainsKey(key));
    }
}
