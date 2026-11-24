using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Вход» (6.1 п. 1).</summary>
public sealed partial class LoginViewModel : DialogViewModel
{
    private readonly AppServices _services;
    private readonly ClientProfile _profile;

    public LoginViewModel(AppServices services, ClientProfile profile) : base(services.Dialogs)
    {
        _services = services;
        _profile = profile;
        _serverUrl = profile.ServerUrl;
        _login = profile.Login ?? string.Empty;
        _remember = true;
        Busy.ErrorHandler = null;
    }

    public override string Title => "SolidBrew PDM — вход";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _serverUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _login;

    /// <summary>Пароль (из PasswordBox; в профиль не сохраняется).</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>«Запомнить» — сохранить сеанс (токен под DPAPI), чтобы не вводить пароль.</summary>
    [ObservableProperty]
    private bool _remember;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Сеанс после успешного входа.</summary>
    public AppSession? Session { get; private set; }

    /// <summary>Непрочитанные уведомления (например, о принудительно снятой блокировке).</summary>
    public List<NotificationDto> Notifications { get; } = new();

    private bool CanLogin() => !Busy.IsBusy && !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Login);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Введите пароль.";
            return;
        }
        var url = ServerUrl.Trim();
        var login = Login.Trim();
        var password = Password;
        var outcome = await RunTrustingAsync("Вход", async (thumbprint, t) =>
        {
            var api = _services.Factory.CreateApi(url, null, thumbprint);
            var response = await api.LoginAsync(login, password, t);
            var notes = await LoadNotificationsAsync(api, t);
            return (api, response, notes);
        });
        if (!outcome.Ok)
        {
            ErrorMessage = outcome.Canceled ? null : LoginError(outcome.Error!);
            return;
        }
        var (api, resp, notifications) = outcome.Value;
        Password = string.Empty;
        _profile.ServerUrl = url;
        _profile.Login = login;
        if (Remember)
        {
            _profile.Token = resp.Token;
            _profile.TokenExpiresAt = resp.ExpiresAt;
        }
        else
        {
            _profile.Token = null;
            _profile.TokenExpiresAt = null;
        }
        Complete(api, resp.User, notifications);
    }

    /// <summary>Войти по сохранённому сеансу без пароля. false — сеанса нет или он истёк.</summary>
    public async Task<bool> TryResumeAsync()
    {
        if (!_profile.HasValidToken) return false;
        var url = _profile.ServerUrl;
        var token = _profile.Token;
        var outcome = await RunTrustingAsync("Восстановление сеанса", async (thumbprint, t) =>
        {
            var api = _services.Factory.CreateApi(url, token, thumbprint);
            var me = await api.GetMeAsync(t);
            var notes = await LoadNotificationsAsync(api, t);
            return (api, me, notes);
        });
        if (!outcome.Ok)
        {
            if (outcome.Error?.UntrustedCertificate != null)
            {
                // Сеанс не трогаем: дело в сертификате, а не во входе.
                ErrorMessage = outcome.Error!.Message;
            }
            else if (outcome.Error != null && !outcome.Canceled)
            {
                ErrorMessage = "Сохранённый сеанс недействителен. Войдите заново.";
                _profile.Token = null;
                _profile.TokenExpiresAt = null;
                TrySaveProfile();
            }
            return false;
        }
        Complete(outcome.Value.api, outcome.Value.me, outcome.Value.notes);
        return true;
    }

    /// <summary>
    /// Выполнить обращение к серверу с доверием при первом подключении: если сервер предъявил неизвестный
    /// сертификат, спросить пользователя (отпечаток), сохранить отпечаток в профиль и повторить.
    /// </summary>
    private async Task<Outcome<T>> RunTrustingAsync<T>(string title, Func<string?, CancellationToken, Task<T>> work)
    {
        var thumbprint = _profile.TrustedCertificateThumbprint;
        while (true)
        {
            var current = thumbprint;
            var outcome = await Busy.RunAsync(title, (_, ct) => WithTimeoutAsync(LoginTimeout, ct, t => work(current, t)), reportErrors: false);
            if (outcome.Ok || outcome.Error?.UntrustedCertificate is not { } untrusted) return outcome;
            if (string.Equals(untrusted.Thumbprint, current, StringComparison.OrdinalIgnoreCase)) return outcome;
            if (!CertificateTrust.Ask(Dialogs, untrusted))
            {
                var declined = "Подключение отменено: сертификат сервера не подтверждён. Отпечаток SHA-256: " + untrusted.DisplayThumbprint + ".";
                return new Outcome<T>(false, default, new ErrorInfo(declined) { UntrustedCertificate = untrusted }, false);
            }
            _profile.TrustedCertificateThumbprint = untrusted.Thumbprint;
            TrySaveProfile();
            thumbprint = untrusted.Thumbprint;
        }
    }

    /// <summary>Сколько ждать ответа сервера при входе.</summary>
    public static TimeSpan LoginTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Таймаут ожидания сервера: «сервер не отвечает» вместо бесконечного ожидания.</summary>
    private static async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, CancellationToken ct, Func<CancellationToken, Task<T>> work)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await work(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PdmApiException(0, $"Сервер PDM не ответил за {timeout.TotalSeconds:0} с. Проверьте адрес сервера и сеть.");
        }
    }

    private static async Task<List<NotificationDto>> LoadNotificationsAsync(IPdmApi api, CancellationToken ct)
    {
        try
        {
            return (await api.GetNotificationsAsync(ct)).Where(n => !n.Seen).OrderBy(n => n.At).ToList();
        }
        catch (PdmApiException)
        {
            return new List<NotificationDto>();
        }
    }

    private void Complete(IPdmApi api, UserDto me, List<NotificationDto> notifications)
    {
        TrySaveProfile();
        WorkspaceService workspace;
        try
        {
            workspace = _services.Factory.CreateWorkspace(api, _profile);
        }
        catch (Exception ex)
        {
            ErrorMessage = ErrorText.Describe(ex).Message;
            return;
        }
        Session = new AppSession(_profile, api, me, workspace, _services);
        Notifications.Clear();
        Notifications.AddRange(notifications);
        Close(true);
    }

    private void TrySaveProfile()
    {
        try
        {
            _services.Profiles.Save(_profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Профиль не сохранился — вход всё равно возможен, просто без запоминания.
        }
    }

    private static string LoginError(ErrorInfo error) => error.Message;

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(_services, _profile) { ServerUrl = ServerUrl };
        if (Dialogs.ShowDialog(vm) == true) ServerUrl = _profile.ServerUrl;
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
