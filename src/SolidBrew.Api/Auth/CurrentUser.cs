using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using SolidBrew.Api.Db;
using SolidBrew.Api.Http;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Auth;

/// <summary>Вошедший пользователь текущего запроса. Параметр обработчика заполняется из <see cref="HttpContext"/>.</summary>
public sealed record CurrentUser(Guid Id, string Login, string DisplayName, UserRole Role, byte[] TokenHash)
{
    private const string ItemKey = "pdm.user";

    public bool IsAdmin => Role == UserRole.Admin;
    public bool CanWrite => Role is UserRole.Admin or UserRole.Designer;

    public UserDto ToDto() => new() { Id = Id, Login = Login, DisplayName = DisplayName, Role = Role, Active = true };

    public static ValueTask<CurrentUser?> BindAsync(HttpContext context, ParameterInfo parameter) =>
        ValueTask.FromResult(context.Items[ItemKey] as CurrentUser);

    internal static void Set(HttpContext context, CurrentUser user) => context.Items[ItemKey] = user;

    /// <summary>Отказ для читателя (операции изменения).</summary>
    public IResult? RequireWriter(string action) => CanWrite
        ? null
        : ApiErrors.Forbidden($"Ваша роль «читатель» позволяет только просматривать и выгружать документы — {action} недоступно. Обратитесь к администратору.");

    public IResult? RequireAdmin() => IsAdmin
        ? null
        : ApiErrors.Forbidden("Операция доступна только администратору PDM.");
}

/// <summary>Проверка токена Bearer на всех адресах /api, кроме входа.</summary>
internal sealed class AuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, NpgsqlDataSource db)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments(ApiRoutes.Prefix) || path.Equals(ApiRoutes.Templates.Login, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length <= 7)
        {
            await Reject(context, "Требуется вход в PDM. Войдите под своей учётной записью.");
            return;
        }

        var tokenHash = Sessions.HashToken(header[7..].Trim());
        (Guid Id, string Login, string DisplayName, string Role) row;
        await using (var connection = await db.OpenConnectionAsync(context.RequestAborted))
        {
            row = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Login, string DisplayName, string Role)>("""
                SELECT u.id, u.login, u.display_name, u.role
                FROM sessions s JOIN users u ON u.id = s.user_id
                WHERE s.token_hash = @tokenHash AND s.expires_at > now() AND u.active
                """, new { tokenHash });
        }
        if (row.Id == Guid.Empty)
        {
            await Reject(context, "Сессия истекла или завершена. Войдите в PDM заново.");
            return;
        }

        CurrentUser.Set(context, new CurrentUser(row.Id, row.Login, row.DisplayName, DbText.Role(row.Role), tokenHash));
        await next(context);
    }

    private static async Task Reject(HttpContext context, string message)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await ApiErrors.Unauthorized(message).ExecuteAsync(context);
    }
}

internal static class Sessions
{
    public static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
