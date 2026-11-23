using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Db;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Endpoints;

/// <summary>Администрирование: пользователи, настройки, общий журнал. Проекты (чтение — все, создание — администратор).</summary>
internal static partial class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Templates.AdminUsers, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            if (user.RequireAdmin() is { } denied) return denied;
            await using var connection = await db.OpenConnectionAsync(ct);
            return Results.Ok((await LoadUsersAsync(connection, null)).Select(u => u.ToDto()).ToList());
        });
        app.MapPost(ApiRoutes.Templates.AdminUsers, CreateUserAsync);
        app.MapPut(ApiRoutes.Templates.AdminUser, UpdateUserAsync);

        // Чтение: администратор видит все настройки, остальные — только общедоступные ключи «cad.*»
        // (рабочие версии CAD нужны клиенту каждого конструктора для предупреждений). Изменение — только администратор.
        app.MapGet(ApiRoutes.Templates.AdminSettings, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            var settings = await LoadSettingsAsync(connection);
            if (!user.IsAdmin)
                settings.Values = settings.Values.Where(p => IsPublicSetting(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            return Results.Ok(settings);
        });
        app.MapPut(ApiRoutes.Templates.AdminSettings, UpdateSettingsAsync);

        app.MapGet(ApiRoutes.Templates.AdminJournal, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct,
            [FromQuery(Name = ApiRoutes.Query.Limit)] int? limit) =>
        {
            if (user.RequireAdmin() is { } denied) return denied;
            await using var connection = await db.OpenConnectionAsync(ct);
            return Results.Ok(await Journal.ReadAsync(connection, null, Math.Clamp(limit ?? 200, 1, 10000)));
        });

        app.MapGet(ApiRoutes.Templates.Projects, async (NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<(Guid Id, string Name, string Kind)>("SELECT id, name, kind FROM projects ORDER BY kind DESC, lower(name)");
            return Results.Ok(rows.Select(r => new ProjectDto { Id = r.Id, Name = r.Name, Kind = DbText.ProjectKind(r.Kind) }).ToList());
        });
        app.MapPost(ApiRoutes.Templates.Projects, CreateProjectAsync);
    }

    private static async Task<IResult> CreateProjectAsync(CreateProjectRequest request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200 || name.Any(char.IsControl))
            return ApiErrors.Validation("Название проекта должно быть непустым, без управляющих символов и не длиннее 200 символов.");

        await using var connection = await db.OpenConnectionAsync(ct);
        var id = Guid.CreateVersion7();
        var inserted = await connection.ExecuteAsync("""
            INSERT INTO projects (id, name, kind) VALUES (@id, @name, @kind) ON CONFLICT DO NOTHING
            """, new { id, name, kind = DbText.ProjectKind(request.Kind) });
        if (inserted == 0)
            return ApiErrors.Conflict(ProblemCodes.Validation, $"Проект «{name}» уже существует. Выберите другое название.");
        await Journal.WriteAsync(connection, user.Id, JournalActions.ProjectCreated, null, null, new { id, name, kind = DbText.ProjectKind(request.Kind) });
        return Results.Ok(new ProjectDto { Id = id, Name = name, Kind = request.Kind });
    }

    private static async Task<IResult> CreateUserAsync(CreateUserRequest request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;
        var login = request.Login?.Trim() ?? "";
        if (!LoginPattern().IsMatch(login))
            return ApiErrors.Validation("Имя пользователя: от 1 до 64 символов — буквы, цифры, точка, дефис, подчёркивание; без пробелов.");
        if (ValidatePassword(request.Password) is { } bad) return bad;
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? login : request.DisplayName.Trim();
        if (!Enum.IsDefined(request.Role)) return ApiErrors.Validation("Неизвестная роль пользователя.");

        await using var connection = await db.OpenConnectionAsync(ct);
        var id = Guid.CreateVersion7();
        var inserted = await connection.ExecuteAsync("""
            INSERT INTO users (id, login, display_name, password_hash, role) VALUES (@id, @login, @displayName, @hash, @role)
            ON CONFLICT DO NOTHING
            """, new { id, login, displayName, hash = PasswordHasher.Hash(request.Password), role = DbText.Role(request.Role) });
        if (inserted == 0)
            return ApiErrors.Conflict(ProblemCodes.Validation, $"Пользователь «{login}» уже существует. Выберите другое имя.");
        await Journal.WriteAsync(connection, user.Id, JournalActions.UserCreated, null, null, new { id, login, role = DbText.Role(request.Role) });
        return Results.Ok((await LoadUsersAsync(connection, id)).Single().ToDto());
    }

    private static async Task<IResult> UpdateUserAsync(Guid id, UpdateUserRequest request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;
        if (request.Password is not null && ValidatePassword(request.Password) is { } bad) return bad;
        if (request.Role is { } role && !Enum.IsDefined(role)) return ApiErrors.Validation("Неизвестная роль пользователя.");
        if (request.DisplayName is not null && string.IsNullOrWhiteSpace(request.DisplayName))
            return ApiErrors.Validation("Отображаемое имя не может быть пустым.");

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // Все изменения пользователей идут последовательно — проверка «последнего администратора» не гоняется.
        await connection.ExecuteAsync("LOCK TABLE users IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        var existing = (await LoadUsersAsync(connection, id, tx)).SingleOrDefault();
        if (existing is null) return ApiErrors.NotFound($"Пользователь {id} не найден. Обновите список пользователей.");

        var newRole = request.Role is { } r ? DbText.Role(r) : existing.Role;
        var newActive = request.Active ?? existing.Active;
        if (existing.Role == "admin" && existing.Active && (newRole != "admin" || !newActive))
        {
            var admins = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM users WHERE role = 'admin' AND active AND id <> @id", new { id }, tx);
            if (admins == 0)
                return ApiErrors.Unprocessable(ProblemCodes.Validation,
                    $"Нельзя отключить или понизить «{existing.Login}»: это последний администратор PDM. Сначала назначьте другого администратора.");
        }

        await connection.ExecuteAsync("""
            UPDATE users SET display_name = @displayName, role = @role, active = @active,
                             password_hash = coalesce(@hash, password_hash)
            WHERE id = @id
            """, new
        {
            id,
            displayName = request.DisplayName?.Trim() ?? existing.DisplayName,
            role = newRole,
            active = newActive,
            hash = request.Password is null ? null : PasswordHasher.Hash(request.Password),
        }, tx);
        // Смена пароля или отключение завершают все сессии пользователя.
        if (request.Password is not null || !newActive)
            await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id", new { id }, tx);

        await Journal.WriteAsync(connection, user.Id, JournalActions.UserUpdated, null, null, new
        {
            id,
            login = existing.Login,
            displayName = request.DisplayName,
            role = request.Role is null ? null : newRole,
            active = request.Active,
            passwordChanged = request.Password is not null,
        }, tx);
        await tx.CommitAsync(ct);
        return Results.Ok((await LoadUsersAsync(connection, id)).Single().ToDto());
    }

    private static async Task<IResult> UpdateSettingsAsync(SettingsDto request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;
        var values = request.Values ?? [];
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
                return ApiErrors.Validation("Пустой или слишком длинный ключ настройки.");
            if (WorkingVersionKey().IsMatch(key) && !string.IsNullOrWhiteSpace(value) &&
                !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return ApiErrors.Validation($"Рабочая версия CAD («{key}») должна быть числом, например 2022. Указано: «{value}».");
        }

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        // PUT дополняет и меняет переданные ключи; пустое значение удаляет ключ.
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                await connection.ExecuteAsync("DELETE FROM settings WHERE key = @key", new { key }, tx);
            else
                await connection.ExecuteAsync("""
                    INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value
                    """, new { key, value = value.Trim() }, tx);
        }
        await Journal.WriteAsync(connection, user.Id, JournalActions.SettingsChanged, null, null, values, tx);
        await tx.CommitAsync(ct);
        return Results.Ok(await LoadSettingsAsync(connection));
    }

    /// <summary>Настройка, которую может читать любой вошедший пользователь.</summary>
    internal static bool IsPublicSetting(string key) => key.StartsWith("cad.", StringComparison.Ordinal);

    private static IResult? ValidatePassword(string? password) =>
        password is null || password.Length < PasswordHasher.MinPasswordLength
            ? ApiErrors.Validation($"Пароль должен содержать не меньше {PasswordHasher.MinPasswordLength} символов.")
            : password.Length > 1024 ? ApiErrors.Validation("Пароль слишком длинный.") : null;

    private static async Task<SettingsDto> LoadSettingsAsync(NpgsqlConnection connection)
    {
        var rows = await connection.QueryAsync<(string Key, string Value)>("SELECT key, value FROM settings ORDER BY key");
        return new SettingsDto { Values = rows.ToDictionary(r => r.Key, r => r.Value) };
    }

    private static async Task<List<AuthEndpoints.UserRow>> LoadUsersAsync(NpgsqlConnection connection, Guid? id, NpgsqlTransaction? tx = null) =>
        (await connection.QueryAsync<AuthEndpoints.UserRow>("""
            SELECT id, login, display_name, role, active, password_hash FROM users
            WHERE CAST(@id AS uuid) IS NULL OR id = @id ORDER BY lower(login)
            """, new { id }, tx)).ToList();

    [GeneratedRegex(@"^[\p{L}\p{N}._-]{1,64}$")]
    private static partial Regex LoginPattern();

    [GeneratedRegex(@"^cad\.[^.]+\.workingVersion$")]
    private static partial Regex WorkingVersionKey();
}
