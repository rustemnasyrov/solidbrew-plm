using System.Globalization;

namespace SolidBrew.Api;

/// <summary>
/// Настройки сервера из конфигурации (переменные окружения <c>PDM_*</c>).
/// Читаются после сборки приложения, поэтому тесты могут задать их через <c>UseSetting</c>.
/// </summary>
public sealed class PdmOptions
{
    public const long DefaultMaxObjectBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Роль БД, под которой работает приложение (создаётся миграцией).</summary>
    public const string AppRole = "pdm_app";

    public required string DbHost { get; init; }
    public int DbPort { get; init; } = 5432;
    public string DbName { get; init; } = "pdm";
    /// <summary>Роль-владелец схемы: применяет миграции.</summary>
    public string DbOwner { get; init; } = "pdm";
    public required string DbOwnerPassword { get; init; }
    public required string DbAppPassword { get; init; }
    public string? BootstrapAdminPassword { get; init; }

    /// <summary>«s3» (MinIO) или «fs» (каталог на диске).</summary>
    public string Storage { get; init; } = "s3";
    public string? BlobDir { get; init; }
    public string? S3Endpoint { get; init; }
    public string? S3AccessKey { get; init; }
    public string? S3SecretKey { get; init; }
    public string S3Bucket { get; init; } = "pdm-objects";

    public long MaxObjectBytes { get; init; } = DefaultMaxObjectBytes;
    /// <summary>Предельный размер тела обычных (JSON) запросов.</summary>
    public long MaxJsonBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Каталог временных файлов при загрузке объектов.</summary>
    public string TempDir { get; init; } = Path.GetTempPath();

    /// <summary>Сертификат HTTPS (PFX). null — сервер слушает только HTTP (тесты, лаборатория).</summary>
    public string? TlsPfxPath { get; init; }
    public string? TlsPfxPassword { get; init; }
    /// <summary>Порт HTTPS внутри контейнера.</summary>
    public int HttpsPort { get; init; } = 8443;
    /// <summary>Порт HTTP внутри контейнера при включённом HTTPS (локальная диагностика); null — без HTTP.</summary>
    public int? HttpPort { get; init; } = 8080;

    public static PdmOptions FromConfiguration(IConfiguration config)
    {
        string Required(string key) => config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Не задан параметр {key} (переменная окружения или .env).");
        string? Optional(string key) => config[key] is { Length: > 0 } value ? value : null;
        int? Port(string key) => Optional(key) is not { } text
            ? null
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
                ? port
                : throw new InvalidOperationException($"{key}: ожидается номер порта 1–65535, задано «{text}».");

        var storage = (Optional("PDM_STORAGE") ?? "s3").Trim().ToLowerInvariant();
        if (storage is not ("s3" or "fs"))
            throw new InvalidOperationException($"PDM_STORAGE должен быть «s3» или «fs», задано «{storage}».");

        var options = new PdmOptions
        {
            DbHost = Optional("PDM_DB_HOST") ?? "localhost",
            DbPort = Optional("PDM_DB_PORT") is { } port ? int.Parse(port, CultureInfo.InvariantCulture) : 5432,
            DbName = Optional("PDM_DB_NAME") ?? "pdm",
            DbOwner = Optional("PDM_DB_USER") ?? "pdm",
            DbOwnerPassword = Required("PDM_DB_PASSWORD"),
            DbAppPassword = Required("PDM_APP_DB_PASSWORD"),
            BootstrapAdminPassword = Optional("PDM_BOOTSTRAP_ADMIN_PASSWORD"),
            Storage = storage,
            BlobDir = Optional("PDM_BLOB_DIR"),
            S3Endpoint = Optional("PDM_S3_ENDPOINT"),
            S3AccessKey = Optional("PDM_S3_ACCESS_KEY"),
            S3SecretKey = Optional("PDM_S3_SECRET_KEY"),
            S3Bucket = Optional("PDM_S3_BUCKET") ?? "pdm-objects",
            MaxObjectBytes = Optional("PDM_MAX_OBJECT_BYTES") is { } max ? long.Parse(max, CultureInfo.InvariantCulture) : DefaultMaxObjectBytes,
            TempDir = Optional("PDM_TEMP_DIR") ?? Path.GetTempPath(),
            TlsPfxPath = Optional("PDM_TLS_PFX_PATH"),
            TlsPfxPassword = Optional("PDM_TLS_PFX_PASSWORD"),
            HttpsPort = Port("PDM_HTTPS_LISTEN_PORT") ?? 8443,
            HttpPort = Optional("PDM_HTTP_LISTEN_PORT") is "off" or "0" ? null : Port("PDM_HTTP_LISTEN_PORT") ?? 8080,
        };

        if (options.DbAppPassword.Length < 16)
            throw new InvalidOperationException("PDM_APP_DB_PASSWORD должен содержать не меньше 16 символов.");
        if (options.MaxObjectBytes <= 0)
            throw new InvalidOperationException("PDM_MAX_OBJECT_BYTES должен быть положительным.");
        if (storage == "fs" && options.BlobDir is null)
            throw new InvalidOperationException("Для PDM_STORAGE=fs задайте каталог архива PDM_BLOB_DIR.");
        if (storage == "s3" && (options.S3Endpoint is null || options.S3AccessKey is null || options.S3SecretKey is null))
            throw new InvalidOperationException("Для PDM_STORAGE=s3 задайте PDM_S3_ENDPOINT, PDM_S3_ACCESS_KEY и PDM_S3_SECRET_KEY.");
        return options;
    }
}
