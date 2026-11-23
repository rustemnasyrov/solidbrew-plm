using Dapper;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Endpoints;

/// <summary>Взять в работу, отменить взятие; администратор — список и принудительное снятие блокировок.</summary>
internal static class LockEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.Templates.DocumentLock, LockAsync);
        app.MapDelete(ApiRoutes.Templates.DocumentLock, UnlockAsync);

        app.MapGet(ApiRoutes.Templates.AdminLocks, async (CurrentUser user, NpgsqlDataSource db, CancellationToken ct) =>
        {
            if (user.RequireAdmin() is { } denied) return denied;
            await using var connection = await db.OpenConnectionAsync(ct);
            return Results.Ok(await DocumentQueries.GetLocksAsync(connection));
        });
        app.MapDelete(ApiRoutes.Templates.AdminLock, ForceUnlockAsync);
    }

    private static async Task<IResult> LockAsync(Guid id, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireWriter("взятие в работу") is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        var doc = await connection.QuerySingleOrDefaultAsync<(Guid Id, string RelPath, string Status, string ProjectKind, string ProjectName)>("""
            SELECT d.id, d.current_rel_path, d.status, p.kind, p.name FROM documents d JOIN projects p ON p.id = d.project_id WHERE d.id = @id
            """, new { id });
        if (doc.Id == Guid.Empty) return ApiErrors.DocumentNotFound(id);
        if (doc.Status == "unused")
            return ApiErrors.Unprocessable(ProblemCodes.Validation,
                $"Документ «{doc.RelPath}» помечен «не используется» — взять его в работу нельзя. Сначала снимите пометку.", id, doc.RelPath);
        if (doc.ProjectKind == "library" && !user.IsAdmin)
            return ApiErrors.Forbidden($"Документ «{doc.RelPath}» находится в библиотеке «{doc.ProjectName}» — изменять библиотеку может только администратор.", ProblemCodes.LibraryReadOnly);

        // Атомарно: из одновременных запросов строку вставит ровно один.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var inserted = await connection.ExecuteAsync(
                "INSERT INTO locks (document_id, user_id) VALUES (@id, @userId) ON CONFLICT (document_id) DO NOTHING",
                new { id, userId = user.Id });
            if (inserted == 1)
                await Journal.WriteAsync(connection, user.Id, JournalActions.Lock, id, null, new { relPath = doc.RelPath });

            var current = (await DocumentQueries.GetLocksAsync(connection, [id])).FirstOrDefault();
            if (current is null) continue; // блокировку сняли между вставкой и чтением — пробуем снова
            if (current.UserId == user.Id) return Results.Ok(current);
            return ApiErrors.Conflict(ProblemCodes.LockedByOther, ApiErrors.LockedByOtherMessage(doc.RelPath, current), id, current);
        }
        return ApiErrors.Conflict(ProblemCodes.LockedByOther, $"Не удалось взять документ «{doc.RelPath}» в работу: блокировка часто меняется. Повторите попытку.", id);
    }

    private static async Task<IResult> UnlockAsync(Guid id, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireWriter("отмена взятия") is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        var relPath = await connection.ExecuteScalarAsync<string?>("SELECT current_rel_path FROM documents WHERE id = @id", new { id });
        if (relPath is null) return ApiErrors.DocumentNotFound(id);

        var deleted = await connection.ExecuteAsync("DELETE FROM locks WHERE document_id = @id AND user_id = @userId", new { id, userId = user.Id });
        if (deleted == 1)
        {
            await Journal.WriteAsync(connection, user.Id, JournalActions.Unlock, id, null, new { relPath });
            return Results.NoContent();
        }

        var current = (await DocumentQueries.GetLocksAsync(connection, [id])).FirstOrDefault();
        if (current is not null)
            return ApiErrors.Conflict(ProblemCodes.LockedByOther,
                $"Документ «{relPath}» взят в работу не вами, а пользователем {ApiErrors.Owner(current)} — отменить чужое взятие нельзя. " +
                "Если он недоступен, обратитесь к администратору.", id, current);
        return Results.NoContent(); // блокировки уже нет — цель достигнута
    }

    private static async Task<IResult> ForceUnlockAsync(Guid documentId, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var relPath = await connection.ExecuteScalarAsync<string?>("SELECT current_rel_path FROM documents WHERE id = @documentId", new { documentId }, tx);
        if (relPath is null) return ApiErrors.DocumentNotFound(documentId);

        var removed = await connection.QuerySingleOrDefaultAsync<(Guid UserId, DateTime At)>(
            "DELETE FROM locks WHERE document_id = @documentId RETURNING user_id, at", new { documentId }, tx);
        if (removed.UserId == Guid.Empty)
            return ApiErrors.NotFound($"Документ «{relPath}» никем не взят в работу — снимать нечего.");

        var owner = await connection.QuerySingleAsync<(string Login, string DisplayName)>(
            "SELECT login, display_name FROM users WHERE id = @id", new { id = removed.UserId }, tx);
        if (removed.UserId != user.Id)
        {
            var text = $"Администратор {user.DisplayName} ({user.Login}) снял вашу блокировку документа «{relPath}» " +
                       $"(взят вами {ApiErrors.Time(Db.DbText.Time(removed.At))}). Ваши локальные правки не удалены. " +
                       "Чтобы продолжить работу, возьмите документ в работу снова — если его не изменили другие, правки можно будет сдать.";
            await connection.ExecuteAsync("INSERT INTO notifications (user_id, text) VALUES (@userId, @text)", new { userId = removed.UserId, text }, tx);
        }
        await Journal.WriteAsync(connection, user.Id, JournalActions.ForceUnlock, documentId, null,
            new { relPath, previousOwner = owner.Login, lockedAt = Db.DbText.Time(removed.At) }, tx);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
