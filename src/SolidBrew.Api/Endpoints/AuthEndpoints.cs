using Dapper;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Db;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Endpoints;

/// <summary>Вход, выход, текущий пользователь и его уведомления.</summary>
internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.Templates.Login, LoginAsync);
        app.MapPost(ApiRoutes.Templates.Logout, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await connection.ExecuteAsync("DELETE FROM sessions WHERE token_hash = @hash", new { hash = user.TokenHash });
            return Results.NoContent();
        });
        app.MapGet(ApiRoutes.Templates.Me, (CurrentUser user) => Results.Ok(user.ToDto()));
        app.MapGet(ApiRoutes.Templates.Notifications, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<(long Id, string Text, DateTime At, bool Seen)>("""
                SELECT id, text, at, seen FROM notifications WHERE user_id = @id ORDER BY id DESC LIMIT 200
                """, new { id = user.Id });
            return Results.Ok(rows.Select(r => new NotificationDto { Id = r.Id, Text = r.Text, At = DbText.Time(r.At), Seen = r.Seen }).ToList());
        });
        app.MapPost(ApiRoutes.Templates.NotificationsSeen, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            await connection.ExecuteAsync("UPDATE notifications SET seen = true WHERE user_id = @id AND NOT seen", new { id = user.Id });
            return Results.NoContent();
        });
    }

    private static async Task<IResult> LoginAsync(LoginRequest? request, NpgsqlDataSource db, PdmOptions options, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Login) || string.IsNullOrEmpty(request.Password))
            return ApiErrors.Validation("Введите имя пользователя и пароль.");

        await using var connection = await db.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>("""
            SELECT id, login, display_name, role, active, password_hash FROM users WHERE lower(login) = lower(@login)
            """, new { login = request.Login.Trim() });

        // Проверка пароля выполняется всегда, чтобы время ответа не выдавало существование учётной записи.
        var valid = PasswordHasher.Verify(request.Password, row?.PasswordHash);
        if (row is null || !valid)
            return ApiErrors.Unauthorized("Неверное имя пользователя или пароль. Проверьте раскладку клавиатуры и Caps Lock.");
        if (!row.Active)
            return ApiErrors.Unauthorized($"Учётная запись «{row.Login}» отключена. Обратитесь к администратору PDM.");

        var token = Sessions.NewToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(options.SessionLifetime);
        await connection.ExecuteAsync("DELETE FROM sessions WHERE user_id = @id AND expires_at <= now()", new { id = row.Id });
        await connection.ExecuteAsync("INSERT INTO sessions (token_hash, user_id, expires_at) VALUES (@hash, @id, @expiresAt)",
            new { hash = Sessions.HashToken(token), id = row.Id, expiresAt = expiresAt.UtcDateTime });
        await Journal.WriteAsync(connection, row.Id, JournalActions.Login);

        return Results.Ok(new LoginResponse
        {
            Token = token,
            ExpiresAt = expiresAt,
            User = row.ToDto(),
        });
    }

    internal sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Login { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Role { get; set; } = "";
        public bool Active { get; set; }
        public string PasswordHash { get; set; } = "";

        public UserDto ToDto() => new() { Id = Id, Login = Login, DisplayName = DisplayName, Role = DbText.Role(Role), Active = Active };
    }
}
