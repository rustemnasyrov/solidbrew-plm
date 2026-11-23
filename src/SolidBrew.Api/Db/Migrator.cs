using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace SolidBrew.Api.Db;

/// <summary>
/// Подготовка БД при старте под ролью-владельцем: роль приложения <see cref="PdmOptions.AppRole"/>
/// (создаётся при отсутствии, пароль берётся из настройки) и пронумерованные миграции
/// (встроенные ресурсы <c>Migrations.NNNN_*.sql</c>) с учётом в таблице <c>schema_version</c>.
/// </summary>
public static partial class Migrator
{
    // Номер ключа рекомендательной блокировки: несколько экземпляров сервера не мигрируют одновременно.
    private const long AdvisoryLockKey = 0x5B_D0_0001;

    public static async Task MigrateAsync(PdmOptions options, ILogger logger, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString(options));
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey });
        try
        {
            await EnsureAppRoleAsync(connection, options.DbAppPassword);
            await connection.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_version (
                    version    integer     PRIMARY KEY,
                    name       text        NOT NULL,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """);
            var applied = (await connection.QueryAsync<int>("SELECT version FROM schema_version")).ToHashSet();

            foreach (var (version, name, sql) in LoadScripts())
            {
                if (applied.Contains(version)) continue;
                logger.LogInformation("Применяется миграция БД {Version} ({Name})", version, name);
                await using var tx = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(sql, transaction: tx);
                await connection.ExecuteAsync("INSERT INTO schema_version (version, name) VALUES (@version, @name)",
                    new { version, name }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
        }
    }

    /// <summary>
    /// Создаёт роль приложения, если её нет, и выставляет ей пароль из настройки.
    /// DDL не принимает параметры, поэтому пароль передаётся параметром в set_config,
    /// а в команду подставляется через format('%L') на стороне сервера — без склейки строк в C#.
    /// </summary>
    private static async Task EnsureAppRoleAsync(NpgsqlConnection connection, string password)
    {
        await connection.ExecuteAsync("SELECT set_config('pdm.app_password', @password, false)", new { password });
        try
        {
            await connection.ExecuteAsync($$"""
                DO $do$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{{PdmOptions.AppRole}}') THEN
                        EXECUTE format('CREATE ROLE {{PdmOptions.AppRole}} LOGIN PASSWORD %L', current_setting('pdm.app_password'));
                    ELSE
                        EXECUTE format('ALTER ROLE {{PdmOptions.AppRole}} WITH LOGIN PASSWORD %L', current_setting('pdm.app_password'));
                    END IF;
                END
                $do$
                """);
            // Право подключаться к базе (у PUBLIC оно есть по умолчанию, но его могли отозвать).
            await connection.ExecuteAsync($"GRANT CONNECT ON DATABASE \"{connection.Database.Replace("\"", "\"\"")}\" TO {PdmOptions.AppRole}");
        }
        finally
        {
            await connection.ExecuteAsync("SELECT set_config('pdm.app_password', '', false)");
        }
    }

    internal static IEnumerable<(int Version, string Name, string Sql)> LoadScripts()
    {
        var assembly = typeof(Migrator).Assembly;
        var scripts = new List<(int, string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = ScriptName().Match(resource);
            if (!match.Success) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            scripts.Add((int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups["name"].Value, reader.ReadToEnd()));
        }
        var ordered = scripts.OrderBy(s => s.Item1).ToList();
        if (ordered.Select(s => s.Item1).Distinct().Count() != ordered.Count)
            throw new InvalidOperationException("Номера миграций БД повторяются.");
        return ordered;
    }

    public static string OwnerConnectionString(PdmOptions options) => new NpgsqlConnectionStringBuilder
    {
        Host = options.DbHost,
        Port = options.DbPort,
        Database = options.DbName,
        Username = options.DbOwner,
        Password = options.DbOwnerPassword,
        Pooling = false,
        // Kerberos не используется; без этого Npgsql пытается загрузить libgssapi и пишет ошибку в журнал.
        GssEncryptionMode = GssEncryptionMode.Disable,
    }.ConnectionString;

    public static string AppConnectionString(PdmOptions options) => new NpgsqlConnectionStringBuilder
    {
        Host = options.DbHost,
        Port = options.DbPort,
        Database = options.DbName,
        Username = PdmOptions.AppRole,
        Password = options.DbAppPassword,
        GssEncryptionMode = GssEncryptionMode.Disable,
    }.ConnectionString;

    [GeneratedRegex(@"^Migrations\.(?<n>\d{4})_(?<name>[A-Za-z0-9_]+)\.sql$")]
    private static partial Regex ScriptName();
}
