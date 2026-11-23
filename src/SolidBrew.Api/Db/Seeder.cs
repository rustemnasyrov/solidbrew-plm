using Dapper;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Services;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Db;

/// <summary>Начальные данные: первый администратор и проект «Библиотека».</summary>
internal static class Seeder
{
    public const string BootstrapAdminLogin = "admin";
    public const string LibraryName = "Библиотека";

    public static async Task SeedAsync(NpgsqlDataSource db, PdmOptions options, ILogger logger, CancellationToken ct = default)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync("LOCK TABLE users IN SHARE ROW EXCLUSIVE MODE", transaction: tx);

        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users)", transaction: tx))
        {
            if (options.BootstrapAdminPassword is not { Length: >= PasswordHasher.MinPasswordLength } password)
                throw new InvalidOperationException(
                    $"В БД нет пользователей. Задайте PDM_BOOTSTRAP_ADMIN_PASSWORD (не короче {PasswordHasher.MinPasswordLength} символов) для создания администратора «{BootstrapAdminLogin}».");
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync("""
                INSERT INTO users (id, login, display_name, password_hash, role) VALUES (@id, @login, 'Администратор', @hash, 'admin')
                """, new { id, login = BootstrapAdminLogin, hash = PasswordHasher.Hash(password) }, tx);
            await Journal.WriteAsync(connection, null, JournalActions.UserCreated, null, null, new { id, login = BootstrapAdminLogin, role = "admin", bootstrap = true }, tx);
            logger.LogInformation("Создан первый администратор «{Login}»", BootstrapAdminLogin);
        }

        if (!await connection.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM projects WHERE kind = 'library')", transaction: tx))
        {
            var id = Guid.CreateVersion7();
            await connection.ExecuteAsync("INSERT INTO projects (id, name, kind) VALUES (@id, @name, 'library') ON CONFLICT DO NOTHING",
                new { id, name = LibraryName }, tx);
            await Journal.WriteAsync(connection, null, JournalActions.ProjectCreated, null, null, new { id, name = LibraryName, kind = "library" }, tx);
        }
        await tx.CommitAsync(ct);
    }
}
