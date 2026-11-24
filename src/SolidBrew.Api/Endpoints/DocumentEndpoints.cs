using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Api.Endpoints;

/// <summary>Поиск и карточка документа, комплект, применяемость, выгрузка, журнал документа, пометка «не используется».</summary>
internal static class DocumentEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(ApiRoutes.Templates.Documents, async (NpgsqlDataSource db, CancellationToken ct,
            [FromQuery(Name = ApiRoutes.Query.Q)] string? q,
            [FromQuery(Name = ApiRoutes.Query.ProjectId)] Guid? projectId,
            [FromQuery(Name = ApiRoutes.Query.Author)] string? author,
            [FromQuery(Name = ApiRoutes.Query.IncludeUnused)] bool? includeUnused,
            [FromQuery(Name = ApiRoutes.Query.Limit)] int? limit,
            [FromQuery(Name = ApiRoutes.Query.Roots)] bool? roots) =>
        {
            var take = Math.Clamp(limit ?? 200, 1, 2000);
            await using var connection = await db.OpenConnectionAsync(ct);
            return Results.Ok(await DocumentQueries.SearchAsync(connection, q, projectId, author, includeUnused ?? false, take, roots ?? false));
        });

        app.MapGet(ApiRoutes.Templates.Document, async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            return await DocumentQueries.GetDetailAsync(connection, id) is { } detail ? Results.Ok(detail) : ApiErrors.DocumentNotFound(id);
        });

        app.MapGet(ApiRoutes.Templates.Kit, async (Guid id, int version, NpgsqlDataSource db, CancellationToken ct,
            [FromQuery(Name = ApiRoutes.Query.IncludeDrawings)] bool? includeDrawings) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            if (!await KitQueries.VersionExistsAsync(connection, id, version)) return VersionNotFound(id, version);
            return Results.Ok((await KitQueries.GetKitAsync(connection, id, version, includeDrawings ?? false)).Kit);
        });

        app.MapGet(ApiRoutes.Templates.WhereUsed, async (Guid id, int version, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            if (!await KitQueries.VersionExistsAsync(connection, id, version)) return VersionNotFound(id, version);
            return Results.Ok(await KitQueries.GetWhereUsedAsync(connection, id));
        });

        app.MapGet(ApiRoutes.Templates.Export, (Guid id, int version, HttpContext context, CurrentUser user, ExportService export,
            [FromQuery(Name = ApiRoutes.Query.Derived)] bool? derived) =>
            export.ExportAsync(context, id, version, derived ?? false, user));

        app.MapGet(ApiRoutes.Templates.DocumentJournal, async (Guid id, NpgsqlDataSource db, CancellationToken ct) =>
        {
            await using var connection = await db.OpenConnectionAsync(ct);
            if (await DocumentQueries.GetSummaryAsync(connection, id) is null) return ApiErrors.DocumentNotFound(id);
            return Results.Ok(await Journal.ReadAsync(connection, id, 1000));
        });

        app.MapPost(ApiRoutes.Templates.DocumentUnused, MarkUnusedAsync);
    }

    private static IResult VersionNotFound(Guid id, int version) =>
        ApiErrors.NotFound($"Версия v{version} документа {id} не найдена. Обновите карточку документа.");

    private static async Task<IResult> MarkUnusedAsync(Guid id, MarkUnusedRequest request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireWriter("пометка «не используется»") is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var doc = await connection.QuerySingleOrDefaultAsync<(Guid Id, Guid ProjectId, string RelPath, string RelKey, string Status, string ProjectKind, string ProjectName)>("""
            SELECT d.id, d.project_id, d.current_rel_path, d.rel_key, d.status, p.kind, p.name
            FROM documents d JOIN projects p ON p.id = d.project_id WHERE d.id = @id FOR UPDATE OF d
            """, new { id }, tx);
        if (doc.Id == Guid.Empty) return ApiErrors.DocumentNotFound(id);
        if (doc.ProjectKind == "library" && !user.IsAdmin)
            return ApiErrors.Forbidden($"Документ «{doc.RelPath}» находится в библиотеке «{doc.ProjectName}» — изменять библиотеку может только администратор.", ProblemCodes.LibraryReadOnly);

        var locks = await DocumentQueries.GetLocksAsync(connection, [id], tx, forUpdate: true);
        if (locks.FirstOrDefault(l => l.UserId != user.Id) is { } other)
            return ApiErrors.Conflict(ProblemCodes.LockedByOther, ApiErrors.LockedByOtherMessage(doc.RelPath, other), id, other);

        var status = request.Unused ? "unused" : "active";
        if (doc.Status != status)
        {
            if (!request.Unused)
            {
                var occupied = await connection.ExecuteScalarAsync<string?>("""
                    SELECT current_rel_path FROM documents WHERE project_id = @ProjectId AND rel_key = @RelKey AND status = 'active' AND id <> @Id
                    """, new { doc.ProjectId, doc.RelKey, doc.Id }, tx);
                if (occupied is not null)
                    return ApiErrors.Conflict(ProblemCodes.DuplicatePath,
                        $"Нельзя вернуть документ «{doc.RelPath}» в работу: этот путь уже занят другим документом проекта. Переименуйте один из них.", id);
            }
            await connection.ExecuteAsync("UPDATE documents SET status = @status WHERE id = @id", new { status, id }, tx);
            await Journal.WriteAsync(connection, user.Id, JournalActions.MarkUnused, id, null, new { unused = request.Unused, relPath = doc.RelPath }, tx);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(await DocumentQueries.GetSummaryAsync(connection, id));
    }
}
