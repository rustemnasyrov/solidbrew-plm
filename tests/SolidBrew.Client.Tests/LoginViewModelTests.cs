using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class LoginViewModelTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private LoginViewModel Create(ClientProfile? profile = null) =>
        new(_env.Services, profile ?? new ClientProfile { ServerUrl = "http://pdm:8088/", CacheRoot = _env.Path("cache"), EnableTestCad = true });

    [Fact]
    public async Task Login_Success_CreatesSession_SavesProfile_ReturnsNotifications()
    {
        _env.Server.Notifications.Add(new NotificationDto { Id = 1, Text = "Администратор снял вашу блокировку документа «Вал».", At = DateTimeOffset.UtcNow });
        _env.Server.Notifications.Add(new NotificationDto { Id = 2, Text = "старое", At = DateTimeOffset.UtcNow, Seen = true });
        var vm = Create();
        vm.Login = "ivanov";
        vm.Password = "secret";
        vm.Remember = true;

        Assert.True(vm.LoginCommand.CanExecute(null));
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.Session);
        Assert.Equal("ivanov", vm.Session!.Me.Login);
        Assert.True(vm.Session.CanModify);
        Assert.False(vm.Session.IsAdmin);
        Assert.True(vm.DialogResult);
        Assert.Equal(string.Empty, vm.Password);
        var note = Assert.Single(vm.Notifications);
        Assert.Contains("блокировку", note.Text);

        Assert.True(_env.Profiles.Saves > 0);
        Assert.Equal("ivanov", _env.Profiles.Stored.Login);
        Assert.Equal("http://pdm:8088/", _env.Profiles.Stored.ServerUrl);
        Assert.Equal("token-ivanov", _env.Profiles.SavedToken);

        // Главное окно показывает уведомления; «Прочитано» отмечает их на сервере.
        var main = new MainViewModel(vm.Session, vm.Notifications);
        Assert.Single(main.Notices);
        await main.DismissNoticesCommand.ExecuteAsync(null);
        Assert.Empty(main.Notices);
        Assert.All(_env.Server.Notifications, n => Assert.True(n.Seen));
    }

    [Fact]
    public async Task Login_WithoutRemember_DoesNotStoreToken()
    {
        var vm = Create();
        vm.Login = "ivanov";
        vm.Password = "secret";
        vm.Remember = false;
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session);
        Assert.Null(_env.Profiles.SavedToken);
    }

    [Fact]
    public async Task Login_WrongPassword_ShowsErrorAndNoSession()
    {
        _env.Factory.Configure = api => api.LoginFailure = new PdmApiException(401, "Неверный логин или пароль.");
        var vm = Create();
        vm.Login = "ivanov";
        vm.Password = "wrong";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Null(vm.Session);
        Assert.Null(vm.DialogResult);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Неверный логин или пароль", vm.ErrorMessage);
        Assert.DoesNotContain("   at ", vm.ErrorMessage); // без стека
        Assert.Null(_env.Profiles.SavedToken);
        Assert.False(vm.Busy.IsBusy);
    }

    [Fact]
    public async Task Login_ServerUnreachable_ShowsNetworkError()
    {
        _env.Factory.Configure = api => api.Offline = true;
        var vm = Create();
        vm.Login = "ivanov";
        vm.Password = "x";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Null(vm.Session);
        Assert.Contains("Нет связи с сервером", vm.ErrorMessage);
    }

    [Fact]
    public async Task Login_EmptyPassword_AsksForPassword_WithoutCallingServer()
    {
        var vm = Create();
        vm.Login = "ivanov";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Equal("Введите пароль.", vm.ErrorMessage);
        Assert.Null(_env.Factory.LastApi);
        vm.Login = " ";
        Assert.False(vm.LoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task Resume_WithSavedToken_LogsInWithoutPassword()
    {
        var profile = new ClientProfile { ServerUrl = "http://pdm:8088/", Login = "ivanov", Token = "token-ivanov", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1), CacheRoot = _env.Path("cache") };
        var vm = Create(profile);
        Assert.True(await vm.TryResumeAsync());
        Assert.Equal("ivanov", vm.Session!.Me.Login);

        var expired = new ClientProfile { ServerUrl = "http://pdm:8088/", Token = "t", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        Assert.False(await Create(expired).TryResumeAsync());
    }

    [Fact]
    public void Reader_SessionCannotModify()
    {
        _env.Server.AddUser("reader", "Читатель", UserRole.Reader);
        var s = _env.Session("reader");
        Assert.False(s.CanModify);
        var main = new MainViewModel(s);
        Assert.True(main.IsReader);
        Assert.False(main.ImportCommand.CanExecute(null));
        Assert.False(main.CheckinCommand.CanExecute(null));
        Assert.Null(main.Admin);
    }
}
