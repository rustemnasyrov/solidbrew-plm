using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SolidBrew.Api.Services;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using Testcontainers.PostgreSql;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>
/// Настоящий сервер PDM в процессе (WebApplicationFactory) над PostgreSQL 17 в контейнере и архивом на диске (PDM_STORAGE=fs).
/// Один на все сквозные тесты; тесты изолируются данными (свои проекты и пользователи) и временными папками.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public const string AdminPassword = "e2e-bootstrap-admin";
    public const string UserPassword = "e2e-user-password-1";
    private const string OwnerPassword = "e2e-owner-password";
    private const string AppDbPassword = "e2e-app-db-password-0123";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("pdm")
        .WithUsername("pdm")
        .WithPassword(OwnerPassword)
        .Build();

    private Factory? _factory;
    private int _counter;

    public string BlobDir { get; } = Path.Combine(Path.GetTempPath(), "solidbrew-e2e-blobs", Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> App => _factory ?? throw new InvalidOperationException("Сервер не запущен.");

    /// <summary>Внедрение сбоя в транзакцию сдачи (перед COMMIT).</summary>
    public CheckinHooks Hooks => App.Services.GetRequiredService<CheckinHooks>();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new Factory(this);
        _ = _factory.Server; // запуск: миграции и начальные данные
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        TempDir.TryDelete(BlobDir);
    }

    public string Unique(string prefix) => $"{prefix}{Interlocked.Increment(ref _counter)}-{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>Клиент API через HTTP-клиент фабрики; <paramref name="counter"/> считает скачивания объектов.</summary>
    public PdmApiClient NewClient(RequestCounter? counter = null, string? token = null) =>
        new(counter is null ? App.CreateClient() : App.CreateDefaultClient(counter), token);

    public async Task<PdmApiClient> AdminAsync()
    {
        var client = NewClient();
        await client.LoginAsync("admin", AdminPassword);
        return client;
    }

    public async Task<UserDto> NewUserAsync(string prefix, UserRole role = UserRole.Designer)
    {
        var admin = await AdminAsync();
        var login = Unique(prefix);
        return await admin.CreateUserAsync(new CreateUserRequest { Login = login, DisplayName = $"Конструктор {login}", Password = UserPassword, Role = role });
    }

    public async Task<ProjectDto> NewProjectAsync()
    {
        var admin = await AdminAsync();
        return await admin.CreateProjectAsync(new CreateProjectRequest { Name = Unique("Изделие "), Kind = ProjectKind.Project });
    }

    private sealed class Factory(ServerFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var cs = new NpgsqlConnectionStringBuilder(fixture._postgres.GetConnectionString());
            builder.UseSetting("PDM_DB_HOST", cs.Host);
            builder.UseSetting("PDM_DB_PORT", cs.Port.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("PDM_DB_NAME", cs.Database);
            builder.UseSetting("PDM_DB_USER", cs.Username);
            builder.UseSetting("PDM_DB_PASSWORD", OwnerPassword);
            builder.UseSetting("PDM_APP_DB_PASSWORD", AppDbPassword);
            builder.UseSetting("PDM_BOOTSTRAP_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("PDM_STORAGE", "fs");
            builder.UseSetting("PDM_BLOB_DIR", fixture.BlobDir);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class EndToEndCollection : ICollectionFixture<ServerFixture>
{
    public const string Name = "e2e";
}

/// <summary>Счётчик запросов клиента: сколько объектов скачано с сервера (GET /api/objects/…).</summary>
public sealed class RequestCounter : DelegatingHandler
{
    private int _downloads;

    public int Downloads => Volatile.Read(ref _downloads);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri is { } uri && uri.AbsolutePath.StartsWith("/api/objects/", StringComparison.Ordinal))
            Interlocked.Increment(ref _downloads);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Временная папка теста; удаляется вместе с файлами «только для чтения».</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "solidbrew-e2e", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public void Dispose() => TryDelete(Root);

    public static void TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
