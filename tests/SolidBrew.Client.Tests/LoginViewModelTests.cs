using SolidBrew.Client.Infrastructure;
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

    private static ServerCertificateInfo Certificate(char fill = 'C') =>
        new(new string(fill, 64), "CN=D-VPAVLOV", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);

    [Fact]
    public async Task Login_UnknownCertificate_AsksWithThumbprint_TrustSavedAndLoginRetried()
    {
        var cert = Certificate();
        _env.Factory.ServerCertificate = cert;
        var vm = Create(new ClientProfile { ServerUrl = "https://pdm:8443/", CacheRoot = _env.Path("cache"), EnableTestCad = true });
        vm.Login = "ivanov";
        vm.Password = "secret";

        await vm.LoginCommand.ExecuteAsync(null);

        var ask = Assert.Single(_env.Dialogs.Confirms);
        Assert.Equal(CertificateTrust.DialogTitle, ask.Title);
        Assert.Contains("Сервер предъявил неизвестный сертификат", ask.Message);
        Assert.Contains(cert.DisplayThumbprint, ask.Message);
        Assert.Contains("Сверьте", ask.Message);
        Assert.Contains("Доверять", ask.Message);
        Assert.Contains(ask.Details, d => d.Contains("https://pdm:8443/"));

        Assert.NotNull(vm.Session);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(new string?[] { null, cert.Thumbprint }, _env.Factory.Thumbprints);
        Assert.Equal(cert.Thumbprint, _env.Profiles.Stored.TrustedCertificateThumbprint);

        // Следующий вход с тем же сертификатом — без вопроса.
        var again = Create(_env.Profiles.Stored);
        again.Password = "secret";
        await again.LoginCommand.ExecuteAsync(null);
        Assert.NotNull(again.Session);
        Assert.Single(_env.Dialogs.Confirms);
    }

    [Fact]
    public async Task Login_UnknownCertificate_Declined_NoSessionAndNothingTrusted()
    {
        var cert = Certificate();
        _env.Factory.ServerCertificate = cert;
        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        var vm = Create(new ClientProfile { ServerUrl = "https://pdm:8443/", CacheRoot = _env.Path("cache") });
        vm.Login = "ivanov";
        vm.Password = "secret";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Null(vm.Session);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains(cert.DisplayThumbprint, vm.ErrorMessage);
        Assert.Null(_env.Profiles.Stored.TrustedCertificateThumbprint);
        Assert.Equal(0, _env.Factory.LastApi!.LoginCalls); // пароль на сервер с неизвестным сертификатом не уходил
    }

    [Fact]
    public async Task Login_CertificateChanged_AsksAgain()
    {
        _env.Factory.ServerCertificate = Certificate('D');
        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        var profile = new ClientProfile { ServerUrl = "https://pdm:8443/", TrustedCertificateThumbprint = new string('C', 64), CacheRoot = _env.Path("cache") };
        var vm = Create(profile);
        vm.Login = "ivanov";
        vm.Password = "secret";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Single(_env.Dialogs.Confirms);
        Assert.Null(vm.Session);
        Assert.Equal(new string('C', 64), profile.TrustedCertificateThumbprint); // отказ не меняет доверенный отпечаток
        Assert.Contains(new string('D', 2) + ":" + new string('D', 2), vm.ErrorMessage);
    }

    [Fact]
    public async Task Resume_UnknownCertificate_KeepsSavedSession()
    {
        _env.Factory.ServerCertificate = Certificate();
        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        var profile = new ClientProfile { ServerUrl = "https://pdm:8443/", Login = "ivanov", Token = "token-ivanov", TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1), CacheRoot = _env.Path("cache") };
        var vm = Create(profile);

        Assert.False(await vm.TryResumeAsync());

        Assert.Contains("SHA-256", vm.ErrorMessage);
        Assert.Equal("token-ivanov", profile.Token); // сеанс не сброшен: дело в сертификате
    }

    [Fact]
    public async Task Settings_CheckConnection_TrustsCertificate_SavedOnSave_ForgetClears()
    {
        var cert = Certificate();
        _env.Factory.ServerCertificate = cert;
        var profile = new ClientProfile { ServerUrl = "https://pdm:8443/", CacheRoot = _env.Path("cache") };
        var vm = new SettingsViewModel(_env.Services, profile);
        Assert.False(vm.ForgetCertificateCommand.CanExecute(null));

        await vm.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Single(_env.Dialogs.Confirms);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("Сервер отвечает", vm.ConnectionStatus);
        Assert.Contains(cert.DisplayThumbprint, vm.CertificateText);
        Assert.Null(profile.TrustedCertificateThumbprint); // до «Сохранить» профиль не меняется
        vm.SaveCommand.Execute(null);
        Assert.Equal(cert.Thumbprint, _env.Profiles.Stored.TrustedCertificateThumbprint);

        var again = new SettingsViewModel(_env.Services, _env.Profiles.Stored);
        Assert.True(again.ForgetCertificateCommand.CanExecute(null));
        again.ForgetCertificateCommand.Execute(null);
        again.SaveCommand.Execute(null);
        Assert.Null(_env.Profiles.Stored.TrustedCertificateThumbprint);
    }
}
