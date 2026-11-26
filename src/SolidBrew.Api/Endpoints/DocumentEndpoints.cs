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

        app.MapDelete(ApiRoutes.Templates.Document, DeleteAsync);

        app.MapPost(ApiRoutes.Templates.VersionsByHash, async (VersionsByHashRequest request, NpgsqlDataSource db, CancellationToken ct) =>
        {
            var hashes = (request.Hashes ?? []).Distinct(StringComparer.Ordinal).ToList();
            if (hashes.Count > 100_000) return ApiErrors.Validation("Слишком много хешей в одном запросе.");
            if (hashes.FirstOrDefault(h => !Core.Hashing.Sha256Hex.IsValid(h)) is { } bad)
                return ApiErrors.Validation($"Неверный хеш содержимого «{bad}»: нужно 64 шестнадцатеричных символа в нижнем регистре.");
            var response = new VersionsByHashResponse();
            if (hashes.Count == 0) return Results.Ok(response);
            await using var connection = await db.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<(string Sha256, Guid DocumentId, int Number)>(
                "SELECT sha256, document_id, number FROM versions WHERE sha256 = ANY(@hashes) ORDER BY sha256, document_id, number",
                new { hashes = hashes.ToArray() });
            var summaries = new Dictionary<Guid, DocumentSummaryDto?>();
            foreach (var r in rows)
            {
                if (!summaries.TryGetValue(r.DocumentId, out var summary))
                    summaries[r.DocumentId] = summary = await DocumentQueries.GetSummaryAsync(connection, r.DocumentId);
                // Удалённый документ не подставляется при постановке на учёт: файл ставится заново.
                if (summary is { Status: not DocumentStatus.Deleted }) response.Items.Add(new VersionByHashDto { Sha256 = r.Sha256, Version = r.Number, Document = summary });
            }
            return Results.Ok(response);
        });
    }

    private static IResult VersionNotFound(Guid id, int version) =>
        ApiErrors.NotFound($"Версия v{version} документа {id} не найдена. Обновите карточку документа.");

    /// <summary>
    /// Удалить документ (только администратор, ТЗ 7.9): нельзя, если на любую его версию ссылается опубликованная версия
    /// другого неудалённого документа или он взят другим. Документ получает статус «удалён» — пропадает из поиска и списков,
    /// путь в проекте освобождается; версии и журнал остаются (опубликованное не удаляется).
    /// </summary>
    private static async Task<IResult> DeleteAsync(Guid id, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireAdmin() is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var doc = await connection.QuerySingleOrDefaultAsync<(Guid Id, string RelPath, string Status)>(
            "SELECT id, current_rel_path, status FROM documents WHERE id = @id FOR UPDATE", new { id }, tx);
        if (doc.Id == Guid.Empty || doc.Status == "deleted") return ApiErrors.DocumentNotFound(id);

        var locks = await DocumentQueries.GetLocksAsync(connection, [id], tx, forUpdate: true);
        if (locks.FirstOrDefault(l => l.UserId != user.Id) is { } other)
            return ApiErrors.Conflict(ProblemCodes.LockedByOther, ApiErrors.LockedByOtherMessage(doc.RelPath, other), id, other);

        var usedIn = (await connection.QueryAsync<(string RelPath, int Number)>("""
            SELECT DISTINCT pd.current_rel_path, pv.number
            FROM version_refs r
            JOIN versions pv ON pv.id = r.version_id
            JOIN documents pd ON pd.id = pv.document_id
            WHERE r.child_document_id = @id AND pd.id <> @id AND pd.status <> 'deleted'
            ORDER BY 1, 2
            """, new { id }, tx)).ToList();
        if (usedIn.Count > 0)
        {
            var list = string.Join(", ", usedIn.Take(5).Select(u => $"«{u.RelPath}» v{u.Number}")) + (usedIn.Count > 5 ? $" и ещё {usedIn.Count - 5}" : string.Empty);
            return ApiErrors.Conflict(ProblemCodes.ReferencedByPublished,
                $"Документ «{doc.RelPath}» нельзя удалить: на него ссылаются опубликованные версии — {list}. Пометьте его «не используется».", id);
        }

        await connection.ExecuteAsync("DELETE FROM locks WHERE document_id = @id", new { id }, tx);
        await connection.ExecuteAsync("UPDATE documents SET status = 'deleted' WHERE id = @id", new { id }, tx);
        await Journal.WriteAsync(connection, user.Id, JournalActions.Deleted, id, null, new { relPath = doc.RelPath }, tx);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkUnusedAsync(Guid id, MarkUnusedRequest request, CurrentUser user, NpgsqlDataSource db, CancellationToken ct)
    {
        if (user.RequireWriter("пометка «не используется»") is { } denied) return denied;

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var doc = await connection.QuerySingleOrDefaultAsync<(Guid Id, Guid ProjectId, string RelPath, string RelKey, string Status, string ProjectKind, string ProjectName)>("""
            SELECT d.id, d.project_id, d.current_rel_path, d.rel_key, d.status, p.kind, p.name
            FROM documents d JOIN projects p ON p.id = d.project_id WHERE d.id = @id FOR UPDATE OF d
            """, new { id }, tx);
        if (doc.Id == Guid.Empty || doc.Status == "deleted") return ApiErrors.DocumentNotFound(id);
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
