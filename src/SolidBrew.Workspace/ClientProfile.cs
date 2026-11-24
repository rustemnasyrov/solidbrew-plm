using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SolidBrew.Core.Api;
using SolidBrew.Core.Json;

namespace SolidBrew.Workspace;

/// <summary>
/// Профиль клиента на компьютере конструктора: адрес сервера, сохранённый вход, недавние рабочие папки.
/// Общий для WPF-клиента и CLI. Токен хранится зашифрованным DPAPI (только текущий пользователь Windows).
/// </summary>
public sealed class ClientProfile
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8088/";
    public string? Login { get; set; }
    /// <summary>Токен в открытом виде — только в памяти; на диск пишется <see cref="ProtectedToken"/>.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Token { get; set; }
    public string? ProtectedToken { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }
    public string? CacheRoot { get; set; }
    public string? LastWorkspace { get; set; }
    public List<string> RecentWorkspaces { get; set; } = new();
    /// <summary>Показывать тестовый CAD (для демонстрации и проверки без SOLIDWORKS).</summary>
    public bool EnableTestCad { get; set; }

    /// <summary>
    /// SHA-256-отпечаток сертификата сервера, которому пользователь доверился при первом подключении
    /// (самоподписанный сертификат). null — доверять только сертификатам, подтверждённым Windows.
    /// </summary>
    public string? TrustedCertificateThumbprint { get; set; }

    public bool HasValidToken => !string.IsNullOrEmpty(Token) && (TokenExpiresAt is null || TokenExpiresAt > DateTimeOffset.UtcNow);

    public void RememberWorkspace(string path)
    {
        var full = Path.GetFullPath(path);
        LastWorkspace = full;
        RecentWorkspaces.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        RecentWorkspaces.Insert(0, full);
        if (RecentWorkspaces.Count > 10) RecentWorkspaces.RemoveRange(10, RecentWorkspaces.Count - 10);
    }
}

public static class ClientProfileStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SolidBrew.PDM.token.v1");

    /// <summary>%APPDATA%\SolidBrew\profile.json; переопределяется переменной SOLIDBREW_PROFILE.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("SOLIDBREW_PROFILE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SolidBrew", "profile.json");

    public static ClientProfile Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new ClientProfile();
        try
        {
            var profile = JsonSerializer.Deserialize<ClientProfile>(File.ReadAllText(path), PdmJson.Options) ?? new ClientProfile();
            if (!string.IsNullOrEmpty(profile.ProtectedToken))
            {
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(profile.ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
                    profile.Token = Encoding.UTF8.GetString(bytes);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    profile.ProtectedToken = null; // токен от другого пользователя Windows — войти заново
                }
            }
            return profile;
        }
        catch (JsonException)
        {
            return new ClientProfile();
        }
    }

    public static void Save(ClientProfile profile, string? path = null)
    {
        path ??= DefaultPath;
        profile.ProtectedToken = string.IsNullOrEmpty(profile.Token)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(profile.Token), Entropy, DataProtectionScope.CurrentUser));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, PdmJson.Options));
        File.Move(temp, path, overwrite: true);
    }
}

public static class PdmClientFactory
{
    /// <summary>Сколько ждать установления TCP-соединения с сервером.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Клиент API для адреса сервера. Таймаут бесконечный: большие файлы идут потоком, отмена — через CancellationToken.</summary>
    public static PdmApiClient Create(string serverUrl, string? token = null) => Create(serverUrl, token, null);

    /// <summary>
    /// Клиент API с проверкой сертификата HTTPS: сертификат, проверенный Windows, принимается всегда;
    /// иначе — только при совпадении SHA-256-отпечатка с <paramref name="trustedThumbprint"/>.
    /// Неизвестный сертификат — <see cref="PdmApiException"/> с вложенным <see cref="UntrustedServerCertificateException"/>
    /// (найти: <see cref="UntrustedServerCertificateException.Find"/>).
    /// </summary>
    public static PdmApiClient Create(string serverUrl, string? token, string? trustedThumbprint)
    {
        var baseUri = ParseServerUrl(serverUrl);
        var http = new HttpClient(CreateHandler(trustedThumbprint)) { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        return new PdmApiClient(http, token);
    }

    /// <summary>Клиент API по профилю: адрес, сохранённый вход и доверенный отпечаток.</summary>
    public static PdmApiClient Create(ClientProfile profile) => Create(profile.ServerUrl, profile.Token, profile.TrustedCertificateThumbprint);

    /// <summary>Обработчик HTTP с таймаутом соединения и проверкой сертификата сервера.</summary>
    public static HttpMessageHandler CreateHandler(string? trustedThumbprint)
    {
        var validator = new ServerCertificateValidator(trustedThumbprint);
        var sockets = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = validator.Validate },
        };
        return new CertificateErrorHandler(validator) { InnerHandler = sockets };
    }

    private static Uri ParseServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new WorkspaceException($"Неверный адрес сервера PDM «{serverUrl}». Пример: https://10.0.1.9:8443/");
        return baseUri;
    }

    /// <summary>
    /// Отказ в сертификате превращает в <see cref="UntrustedServerCertificateException"/> (наследник HttpRequestException):
    /// клиент API оборачивает его в PdmApiException с понятным текстом, а отпечаток остаётся во вложенном исключении.
    /// </summary>
    private sealed class CertificateErrorHandler(ServerCertificateValidator validator) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex is not UntrustedServerCertificateException && validator.LastRejected is { } rejected)
            {
                throw new UntrustedServerCertificateException(rejected, request.RequestUri, ex);
            }
        }
    }
}

/// <summary>Сведения о сертификате, предъявленном сервером.</summary>
public sealed record ServerCertificateInfo(string Thumbprint, string Subject, DateTimeOffset NotBefore, DateTimeOffset NotAfter, SslPolicyErrors Errors)
{
    /// <summary>Отпечаток для показа: пары шестнадцатеричных цифр через двоеточие.</summary>
    public string DisplayThumbprint => ServerCertificateValidator.FormatThumbprint(Thumbprint);

    public static ServerCertificateInfo From(X509Certificate2 certificate, SslPolicyErrors errors) => new(
        ServerCertificateValidator.Sha256Thumbprint(certificate), certificate.Subject,
        new DateTimeOffset(certificate.NotBefore), new DateTimeOffset(certificate.NotAfter), errors);
}

/// <summary>
/// Проверка сертификата сервера PDM: проверенный Windows — принимается; иначе — только с сохранённым SHA-256-отпечатком
/// (доверие при первом подключении к самоподписанному сертификату).
/// </summary>
public sealed class ServerCertificateValidator
{
    private readonly string? _trusted;
    private volatile ServerCertificateInfo? _lastRejected;
    private volatile ServerCertificateInfo? _lastPresented;

    public ServerCertificateValidator(string? trustedThumbprint)
    {
        _trusted = string.IsNullOrWhiteSpace(trustedThumbprint) ? null : NormalizeThumbprint(trustedThumbprint);
    }

    /// <summary>Последний предъявленный сервером сертификат (принятый или нет).</summary>
    public ServerCertificateInfo? LastPresented => _lastPresented;

    /// <summary>Последний отвергнутый сертификат; сбрасывается при успешной проверке.</summary>
    public ServerCertificateInfo? LastRejected => _lastRejected;

    /// <summary>Обратный вызов проверки для SslStream / SocketsHttpHandler.</summary>
    public bool Validate(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        ServerCertificateInfo info;
        if (certificate is X509Certificate2 cert2)
        {
            info = ServerCertificateInfo.From(cert2, errors);
        }
        else
        {
            using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            info = ServerCertificateInfo.From(copy, errors);
        }
        _lastPresented = info;
        var ok = errors == SslPolicyErrors.None || (_trusted != null && string.Equals(_trusted, info.Thumbprint, StringComparison.Ordinal));
        _lastRejected = ok ? null : info;
        return ok;
    }

    /// <summary>SHA-256 от DER-кодировки сертификата, заглавными шестнадцатеричными цифрами без разделителей.</summary>
    public static string Sha256Thumbprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData));

    /// <summary>Отпечаток к виду без разделителей, заглавными буквами. Ошибка — если это не SHA-256.</summary>
    public static string NormalizeThumbprint(string text)
    {
        var sb = new StringBuilder(64);
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
            else if (c is not (':' or ' ' or '-' or '\t'))
                throw new WorkspaceException($"Отпечаток «{text}» содержит недопустимый символ «{c}». Ожидается SHA-256: 64 шестнадцатеричные цифры.");
        }
        if (sb.Length != 64)
            throw new WorkspaceException($"Отпечаток «{text}» — не SHA-256: ожидается 64 шестнадцатеричные цифры, указано {sb.Length}.");
        return sb.ToString();
    }

    /// <summary>Отпечаток для показа: «AB:CD:…».</summary>
    public static string FormatThumbprint(string thumbprint)
    {
        var hex = NormalizeThumbprint(thumbprint);
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (var i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hex, i, 2);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Сервер предъявил сертификат, которому клиент не доверяет: не подтверждён Windows и отпечаток не совпал с сохранённым.
/// Наследует HttpRequestException, поэтому PdmApiClient оборачивает его в PdmApiException(0, …); найти — <see cref="Find"/>.
/// </summary>
public sealed class UntrustedServerCertificateException : HttpRequestException
{
    public UntrustedServerCertificateException(ServerCertificateInfo certificate, Uri? requestUri, Exception? inner = null)
        : base(BuildMessage(certificate, requestUri), inner)
    {
        Certificate = certificate;
        Server = requestUri is null ? null : requestUri.GetLeftPart(UriPartial.Authority) + "/";
    }

    public ServerCertificateInfo Certificate { get; }

    /// <summary>SHA-256-отпечаток без разделителей (для сохранения в профиль).</summary>
    public string Thumbprint => Certificate.Thumbprint;

    /// <summary>Отпечаток для показа пользователю.</summary>
    public string DisplayThumbprint => Certificate.DisplayThumbprint;

    /// <summary>Адрес сервера (схема, узел, порт).</summary>
    public string? Server { get; }

    /// <summary>Найти исключение о недоверенном сертификате в цепочке вложенных исключений.</summary>
    public static UntrustedServerCertificateException? Find(Exception? ex)
    {
        for (var depth = 0; ex != null && depth < 16; depth++)
        {
            if (ex is UntrustedServerCertificateException found) return found;
            ex = ex is AggregateException { InnerExceptions.Count: 1 } agg ? agg.InnerExceptions[0] : ex.InnerException;
        }
        return null;
    }

    private static string BuildMessage(ServerCertificateInfo c, Uri? uri)
    {
        var reason = c.Errors == SslPolicyErrors.RemoteCertificateNameMismatch
            ? "сертификат выдан на другое имя"
            : "сертификат не подтверждён Windows";
        var server = uri is null ? string.Empty : " " + uri.GetLeftPart(UriPartial.Authority);
        return $"Сервер PDM{server} предъявил неизвестный сертификат ({reason}). Отпечаток SHA-256: {c.DisplayThumbprint}. " +
               "Сверьте его с администратором PDM и, если совпадает, подтвердите доверие.";
    }
}
