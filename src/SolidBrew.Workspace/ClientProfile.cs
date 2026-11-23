using System.Security.Cryptography;
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
    /// <summary>Клиент API для адреса сервера. Таймаут бесконечный: большие файлы идут потоком, отмена — через CancellationToken.</summary>
    public static PdmApiClient Create(string serverUrl, string? token = null)
    {
        if (!Uri.TryCreate(serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new WorkspaceException($"Неверный адрес сервера PDM «{serverUrl}». Пример: http://10.0.1.9:8088/");
        var http = new HttpClient { BaseAddress = baseUri, Timeout = Timeout.InfiniteTimeSpan };
        return new PdmApiClient(http, token);
    }
}
