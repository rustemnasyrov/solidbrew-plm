using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SolidBrew.Core.Api;
using SolidBrew.Workspace;

namespace SolidBrew.Workspace.Tests;

/// <summary>
/// Проверка сертификата сервера PDM клиентом: настоящее TLS-рукопожатие с самоподписанным сертификатом.
/// Неизвестный сертификат — ошибка с отпечатком SHA-256; сохранённый отпечаток — соединение принимается.
/// </summary>
public sealed class ServerCertificateTests : IDisposable
{
    private readonly X509Certificate2 _certificate = CreateSelfSigned();
    private readonly TlsStubServer _server;

    public ServerCertificateTests() => _server = new TlsStubServer(_certificate);

    public void Dispose()
    {
        _server.Dispose();
        _certificate.Dispose();
    }

    private string Thumbprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData));

    [Fact]
    public async Task UnknownSelfSignedCertificate_FailsWithThumbprint()
    {
        var api = PdmClientFactory.Create(_server.Url, null, null);

        var ex = await Assert.ThrowsAsync<PdmApiException>(() => api.GetMeAsync());

        Assert.Equal(0, ex.StatusCode);
        var untrusted = UntrustedServerCertificateException.Find(ex);
        Assert.NotNull(untrusted);
        Assert.Equal(Thumbprint, untrusted!.Thumbprint);
        Assert.Equal(ServerCertificateValidator.FormatThumbprint(Thumbprint), untrusted.DisplayThumbprint);
        Assert.Contains(untrusted.DisplayThumbprint, ex.Message); // текст ошибки клиента API уже содержит отпечаток
        Assert.Contains("неизвестный сертификат", ex.Message);
        Assert.Equal(_server.Url, untrusted.Server);
        Assert.Contains("CN=pdm-test", untrusted.Certificate.Subject);
        Assert.Equal(0, _server.Requests); // запрос с паролем или токеном до сервера не дошёл
    }

    [Fact]
    public async Task TrustedThumbprint_ConnectionAccepted()
    {
        // Отпечаток в любом виде: с двоеточиями и строчными буквами.
        var api = PdmClientFactory.Create(_server.Url, "token", ServerCertificateValidator.FormatThumbprint(Thumbprint).ToLowerInvariant());

        var ex = await Assert.ThrowsAsync<PdmApiException>(() => api.GetMeAsync());

        Assert.Equal(401, ex.StatusCode); // ответ сервера, а не ошибка соединения
        Assert.Null(UntrustedServerCertificateException.Find(ex));
        Assert.Equal(1, _server.Requests);
    }

    [Fact]
    public async Task OtherThumbprint_Rejected()
    {
        var other = new string('A', 64);
        var api = PdmClientFactory.Create(_server.Url, null, other);

        var ex = await Assert.ThrowsAsync<PdmApiException>(() => api.GetMeAsync());

        Assert.Equal(Thumbprint, UntrustedServerCertificateException.Find(ex)?.Thumbprint);
        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public void Validator_SystemTrusted_AcceptedWithoutPin_UnknownRejected()
    {
        var validator = new ServerCertificateValidator(null);
        Assert.True(validator.Validate(null, _certificate, null, SslPolicyErrors.None));
        Assert.Null(validator.LastRejected);
        Assert.Equal(Thumbprint, validator.LastPresented!.Thumbprint);

        Assert.False(validator.Validate(null, _certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.Equal(Thumbprint, validator.LastRejected!.Thumbprint);

        var pinned = new ServerCertificateValidator(Thumbprint);
        Assert.True(pinned.Validate(null, _certificate, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(pinned.Validate(null, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData("ab:cd")]
    [InlineData("zz")]
    [InlineData("")]
    public void NormalizeThumbprint_RejectsNonSha256(string text) =>
        Assert.Throws<WorkspaceException>(() => ServerCertificateValidator.NormalizeThumbprint(text));

    [Fact]
    public void Profile_TrustedThumbprint_SavedAndLoaded()
    {
        var path = Path.Combine(Path.GetTempPath(), "sbprofile-" + Guid.NewGuid().ToString("N"), "profile.json");
        try
        {
            ClientProfileStore.Save(new ClientProfile { ServerUrl = "https://10.0.1.9:8443/", TrustedCertificateThumbprint = Thumbprint }, path);
            var loaded = ClientProfileStore.Load(path);
            Assert.Equal(Thumbprint, loaded.TrustedCertificateThumbprint);
            Assert.Equal("https://10.0.1.9:8443/", loaded.ServerUrl);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=pdm-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        // SChannel не работает с эфемерным ключом — переносим через PFX.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Минимальный HTTPS-сервер: на любой запрос отвечает 401 в формате ошибок PDM.</summary>
    private sealed class TlsStubServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public TlsStubServer(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _ = AcceptLoopAsync();
        }

        public string Url { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(_certificate, false, false);
                    var buffer = new byte[8192];
                    var head = new StringBuilder();
                    while (!head.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await ssl.ReadAsync(buffer, _cts.Token);
                        if (read == 0) return;
                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }
                    Interlocked.Increment(ref _requests);
                    var body = Encoding.UTF8.GetBytes("{\"message\":\"Требуется вход.\"}");
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await ssl.WriteAsync(response, _cts.Token);
                    await ssl.WriteAsync(body, _cts.Token);
                    await ssl.FlushAsync(_cts.Token);
                }
                catch (Exception)
                {
                    // Клиент отверг сертификат или закрыл соединение — это и проверяется.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
