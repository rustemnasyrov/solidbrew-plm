using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SolidBrew.Api.Services;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using Testcontainers.PostgreSql;

namespace SolidBrew.Api.Tests;

/// <summary>
/// Один контейнер PostgreSQL 17 и один экземпляр сервера на всю сборку тестов.
/// Тесты изолируются данными: каждый создаёт свои проекты и пользователей.
/// </summary>
public sealed class PdmServerFixture : IAsyncLifetime
{
    public const string AdminPassword = "bootstrap-admin-pass";
    public const string OwnerPassword = "owner-db-password";
    public const string AppDbPassword = "app-db-password-0123456789";
    public const string UserPassword = "user-password-123";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("pdm")
        .WithUsername("pdm")
        .WithPassword(OwnerPassword)
        .Build();

    private Factory? _factory;
    private int _counter;

    public string BlobDir { get; } = Path.Combine(Path.GetTempPath(), "solidbrew-api-tests", Guid.NewGuid().ToString("N"));

    public WebApplicationFactory<Program> App => _factory ?? throw new InvalidOperationException("Сервер не запущен.");

    public CheckinHooks Hooks => App.Services.GetRequiredService<CheckinHooks>();

    public string OwnerConnectionString => _postgres.GetConnectionString();

    public string AppConnectionString => new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
    {
        Username = "pdm_app",
        Password = AppDbPassword,
    }.ConnectionString;

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
        try
        {
            if (Directory.Exists(BlobDir))
            {
                foreach (var file in Directory.EnumerateFiles(BlobDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(BlobDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    public PdmApiClient NewClient() => new(App.CreateClient());

    public HttpClient NewHttpClient() => App.CreateClient();

    public string Unique(string prefix) => $"{prefix}{Interlocked.Increment(ref _counter)}-{Guid.NewGuid().ToString("N")[..6]}";

    public async Task<PdmApiClient> AdminAsync()
    {
        var client = NewClient();
        await client.LoginAsync("admin", AdminPassword);
        return client;
    }

    /// <summary>Новый пользователь с ролью и вошедший клиент для него.</summary>
    public async Task<(PdmApiClient Client, UserDto User)> NewUserAsync(UserRole role, PdmApiClient? admin = null)
    {
        admin ??= await AdminAsync();
        var login = Unique(role.ToString().ToLowerInvariant());
        var user = await admin.CreateUserAsync(new CreateUserRequest { Login = login, DisplayName = $"Тест {login}", Password = UserPassword, Role = role });
        var client = NewClient();
        await client.LoginAsync(login, UserPassword);
        return (client, user);
    }

    public async Task<ProjectDto> NewProjectAsync(PdmApiClient? admin = null, ProjectKind kind = ProjectKind.Project)
    {
        admin ??= await AdminAsync();
        return await admin.CreateProjectAsync(new CreateProjectRequest { Name = Unique("Проект "), Kind = kind });
    }

    public async Task<ProjectDto> LibraryAsync()
    {
        var admin = await AdminAsync();
        return (await admin.GetProjectsAsync()).First(p => p.Kind == ProjectKind.Library);
    }

    private sealed class Factory(PdmServerFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var cs = new NpgsqlConnectionStringBuilder(fixture._postgres.GetConnectionString());
            builder.UseSetting("PDM_DB_HOST", cs.Host);
            builder.UseSetting("PDM_DB_PORT", cs.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("PDM_DB_NAME", cs.Database);
            builder.UseSetting("PDM_DB_USER", cs.Username);
            builder.UseSetting("PDM_DB_PASSWORD", OwnerPassword);
            builder.UseSetting("PDM_APP_DB_PASSWORD", AppDbPassword);
            builder.UseSetting("PDM_BOOTSTRAP_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("PDM_STORAGE", "fs");
            builder.UseSetting("PDM_BLOB_DIR", fixture.BlobDir);
            builder.UseSetting("PDM_MAX_OBJECT_BYTES", (1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PdmCollection : ICollectionFixture<PdmServerFixture>
{
    public const string Name = "pdm";
}
