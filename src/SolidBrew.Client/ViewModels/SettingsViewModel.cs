using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Api;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Настройки»: адрес сервера и доверенный сертификат, папка кэша, тестовый CAD.</summary>
public sealed partial class SettingsViewModel : DialogViewModel
{
    private readonly AppServices _services;
    private readonly ClientProfile _profile;

    public SettingsViewModel(AppServices services, ClientProfile profile) : base(services.Dialogs)
    {
        _services = services;
        _profile = profile;
        _serverUrl = profile.ServerUrl;
        _cacheRoot = string.IsNullOrWhiteSpace(profile.CacheRoot) ? LocalCache.DefaultRoot : profile.CacheRoot;
        _enableTestCad = profile.EnableTestCad;
        _thumbprint = profile.TrustedCertificateThumbprint;
        _certificateText = DescribeThumbprint(_thumbprint);
    }

    /// <summary>Доверенный отпечаток (сохраняется в профиль по «Сохранить»).</summary>
    private string? _thumbprint;

    /// <summary>Сколько ждать сервер при проверке подключения.</summary>
    public static TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public override string Title => "Настройки";

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _cacheRoot;

    [ObservableProperty]
    private bool _enableTestCad;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Доверенный сертификат сервера — для показа.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForgetCertificateCommand))]
    private string _certificateText;

    /// <summary>Итог «Проверить подключение».</summary>
    [ObservableProperty]
    private string? _connectionStatus;

    /// <summary>Доверенный отпечаток, который будет сохранён (null — нет).</summary>
    public string? TrustedThumbprint => _thumbprint;

    private static string DescribeThumbprint(string? thumbprint) => string.IsNullOrEmpty(thumbprint)
        ? "Не задан: принимаются только сертификаты, подтверждённые Windows."
        : "SHA-256 " + ServerCertificateValidator.FormatThumbprint(thumbprint);

    private bool HasCertificate() => !string.IsNullOrEmpty(_thumbprint);

    /// <summary>Забыть доверенный сертификат (например, на сервере его заменили).</summary>
    [RelayCommand(CanExecute = nameof(HasCertificate))]
    private void ForgetCertificate()
    {
        _thumbprint = null;
        CertificateText = DescribeThumbprint(null);
        ConnectionStatus = null;
    }

    /// <summary>Проверить, что сервер отвечает; неизвестный сертификат — вопрос о доверии.</summary>
    [RelayCommand]
    private async Task CheckConnectionAsync()
    {
        ErrorMessage = null;
        ConnectionStatus = null;
        if (!TryParseUrl(ServerUrl.Trim(), out var url))
        {
            ErrorMessage = "Неверный адрес сервера. Пример: https://10.0.1.9:8443/";
            return;
        }
        while (true)
        {
            var thumbprint = _thumbprint;
            var outcome = await Busy.RunAsync("Проверка подключения", async (_, ct) =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(CheckTimeout);
                var api = _services.Factory.CreateApi(url, null, thumbprint);
                try
                {
                    await api.GetMeAsync(cts.Token);
                }
                catch (PdmApiException ex) when (ex.StatusCode is 401 or 403)
                {
                    // Сервер ответил: соединение и сертификат в порядке, вход — отдельно.
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new PdmApiException(0, $"Сервер PDM не ответил за {CheckTimeout.TotalSeconds:0} с. Проверьте адрес сервера и сеть.");
                }
                return true;
            }, reportErrors: false);
            if (outcome.Ok)
            {
                ConnectionStatus = "Сервер отвечает" + (url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? ", сертификат проверен." : " (без шифрования — HTTP).");
                return;
            }
            if (outcome.Canceled) return;
            if (outcome.Error?.UntrustedCertificate is { } untrusted
                && !string.Equals(untrusted.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                if (!CertificateTrust.Ask(Dialogs, untrusted))
                {
                    ErrorMessage = "Сертификат сервера не подтверждён. Отпечаток SHA-256: " + untrusted.DisplayThumbprint + ".";
                    return;
                }
                _thumbprint = untrusted.Thumbprint;
                CertificateText = DescribeThumbprint(_thumbprint);
                continue;
            }
            ErrorMessage = outcome.Error?.Message;
            return;
        }
    }

    private static bool TryParseUrl(string url, out string normalized)
    {
        normalized = url;
        if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        return true;
    }

    public string DefaultCacheRoot => LocalCache.DefaultRoot;

    /// <summary>Изменился адрес сервера (нужен повторный вход).</summary>
    public bool ServerChanged { get; private set; }

    /// <summary>Изменились кэш или набор CAD (пересоздать службу рабочих папок).</summary>
    public bool WorkspaceChanged { get; private set; }

    [RelayCommand]
    private void BrowseCache()
    {
        var folder = Dialogs.PickFolder("Папка кэша", Directory.Exists(CacheRoot) ? CacheRoot : null);
        if (folder != null) CacheRoot = folder;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        var url = ServerUrl.Trim();
        if (!TryParseUrl(url, out _))
        {
            ErrorMessage = "Неверный адрес сервера. Пример: https://10.0.1.9:8443/";
            return;
        }
        string? cache = string.IsNullOrWhiteSpace(CacheRoot) ? null : CacheRoot.Trim();
        if (cache != null)
        {
            try
            {
                cache = Path.GetFullPath(cache);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ErrorMessage = "Неверный путь к папке кэша.";
                return;
            }
            if (string.Equals(cache.TrimEnd('\\'), LocalCache.DefaultRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) cache = null;
        }

        ServerChanged = !string.Equals(_profile.ServerUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        WorkspaceChanged = !string.Equals(_profile.CacheRoot ?? string.Empty, cache ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                           || _profile.EnableTestCad != EnableTestCad;
        _profile.ServerUrl = url;
        _profile.CacheRoot = cache;
        _profile.EnableTestCad = EnableTestCad;
        _profile.TrustedCertificateThumbprint = _thumbprint;
        try
        {
            _services.Profiles.Save(_profile);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Настройки не сохранены: " + ErrorText.Describe(ex).Message;
            return;
        }
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
