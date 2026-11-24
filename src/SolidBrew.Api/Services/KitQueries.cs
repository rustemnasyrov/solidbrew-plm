using System.Data;
using Dapper;
using SolidBrew.Api.Db;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Services;

/// <summary>Комплект (замыкание по ссылкам версий) и «где применяется».</summary>
internal static class KitQueries
{
    public static Task<bool> VersionExistsAsync(IDbConnection connection, Guid id, int version) =>
        connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM versions WHERE document_id = @id AND number = @version)", new { id, version });

    private const string ItemColumns = """
        SELECT v.id AS version_id, v.document_id, v.number AS version, lv.latest AS latest_version, v.rel_path, v.sha256, v.size,
               d.cad, d.kind, v.cad_version, v.cad_version_major, v.designation, v.name, d.status, d.project_id, v.at,
               l.user_id AS lock_user_id, lu.login AS lock_login, lu.display_name AS lock_display_name, l.at AS lock_at
        """;

    private const string ItemJoins = """
            JOIN documents d ON d.id = v.document_id
            JOIN LATERAL (SELECT max(x.number) AS latest FROM versions x WHERE x.document_id = d.id) lv ON true
            LEFT JOIN locks l ON l.document_id = d.id
            LEFT JOIN users lu ON lu.id = l.user_id
        """;

    /// <summary>
    /// Комплект версии. Элементы уникальны по (документ, версия); головной — первый.
    /// <paramref name="includeDrawings"/> — добавить последние версии активных чертежей, ссылающихся на любую версию любого
    /// документа комплекта, и замыкание их зависимостей по документам, которых в комплекте нет (документ, уже входящий
    /// в комплект, второй раз в другой версии не добавляется). Добавленные элементы — после элементов комплекта,
    /// с <see cref="KitItemDto.IsAttachedDrawing"/>.
    /// </summary>
    public static async Task<(KitDto Kit, List<long> VersionIds)> GetKitAsync(IDbConnection connection, Guid id, int version, bool includeDrawings = false)
    {
        var items = (await connection.QueryAsync<KitRow>($"""
            WITH RECURSIVE kit(document_id, number) AS (
                SELECT CAST(@id AS uuid), CAST(@version AS integer)
              UNION
                SELECT r.child_document_id, r.child_version
                FROM kit k
                JOIN versions v ON v.document_id = k.document_id AND v.number = k.number
                JOIN version_refs r ON r.version_id = v.id
                WHERE r.child_document_id IS NOT NULL
            )
            {ItemColumns}
            FROM kit k
            JOIN versions v ON v.document_id = k.document_id AND v.number = k.number
            {ItemJoins}
            ORDER BY (v.document_id = @id AND v.number = @version) DESC, lower(v.rel_path), v.number
            """, new { id, version })).ToList();

        var attached = new List<KitRow>();
        if (includeDrawings)
        {
            var addedIds = await AttachedVersionIdsAsync(connection, items);
            if (addedIds.Count > 0)
            {
                attached = (await connection.QueryAsync<KitRow>($"""
                    {ItemColumns}
                    FROM versions v
                    {ItemJoins}
                    WHERE v.id = ANY(@addedIds)
                    ORDER BY (d.kind = 'drawing') DESC, lower(v.rel_path), v.number
                    """, new { addedIds = addedIds.ToArray() })).ToList();
            }
        }

        var versionIds = items.Concat(attached).Select(i => i.VersionId).ToArray();
        var refs = await connection.QueryAsync<EdgeRow>("""
            SELECT pv.document_id AS parent_document_id, pv.number AS parent_version, r.child_document_id, r.child_version, r.ref_path
            FROM version_refs r JOIN versions pv ON pv.id = r.version_id
            WHERE r.version_id = ANY(@versionIds)
            ORDER BY r.id
            """, new { versionIds });

        var kit = new KitDto { HeadDocumentId = id, HeadVersion = version };
        kit.Items = items.Select(i => ToDto(i, false)).Concat(attached.Select(i => ToDto(i, true))).ToList();
        foreach (var r in refs)
        {
            if (r.ChildDocumentId is { } childId && r.ChildVersion is { } childVersion)
                kit.Edges.Add(new KitEdgeDto { ParentDocumentId = r.ParentDocumentId, ParentVersion = r.ParentVersion, ChildDocumentId = childId, ChildVersion = childVersion, RefPath = r.RefPath });
            else
                kit.Unresolved.Add(new KitUnresolvedRefDto { ParentDocumentId = r.ParentDocumentId, ParentVersion = r.ParentVersion, RefPath = r.RefPath });
        }

        // Чертёж сдан с другой версией модели, чем в комплекте.
        if (attached.Count > 0)
        {
            var kitVersions = items.GroupBy(i => i.DocumentId).ToDictionary(g => g.Key, g => g.Select(i => i.Version).ToHashSet());
            foreach (var item in kit.Items.Where(i => i.IsAttachedDrawing && i.Kind == DocumentKind.Drawing))
            {
                item.DrawingModelVersionMismatch = kit.Edges.Any(e =>
                    e.ParentDocumentId == item.DocumentId && e.ParentVersion == item.Version &&
                    kitVersions.TryGetValue(e.ChildDocumentId, out var versions) && !versions.Contains(e.ChildVersion));
            }
        }
        return (kit, versionIds.ToList());
    }

    /// <summary>
    /// Версии, добавляемые к комплекту с чертежами: последние версии активных чертежей, ссылающихся на документы комплекта,
    /// и (замыканием) их зависимости по документам, которых в комплекте ещё нет.
    /// </summary>
    private static async Task<List<long>> AttachedVersionIdsAsync(IDbConnection connection, List<KitRow> items)
    {
        var present = new HashSet<Guid>(items.Select(i => i.DocumentId));
        var docIds = present.ToArray();
        var drawings = (await connection.QueryAsync<(long VersionId, Guid DocumentId)>("""
            SELECT lv.id, d.id
            FROM documents d
            JOIN LATERAL (SELECT x.id FROM versions x WHERE x.document_id = d.id ORDER BY x.number DESC LIMIT 1) lv ON true
            WHERE d.kind = 'drawing' AND d.status = 'active' AND NOT (d.id = ANY(@docIds))
              AND EXISTS (SELECT 1 FROM version_refs r WHERE r.version_id = lv.id AND r.child_document_id = ANY(@docIds))
            ORDER BY lower(d.current_rel_path), d.id
            """, new { docIds })).ToList();

        var added = new List<long>();
        var frontier = new List<long>();
        foreach (var (versionId, documentId) in drawings)
        {
            if (!present.Add(documentId)) continue;
            added.Add(versionId);
            frontier.Add(versionId);
        }
        while (frontier.Count > 0)
        {
            var children = await connection.QueryAsync<(Guid ChildDocumentId, long ChildVersionId)>("""
                SELECT r.child_document_id, cv.id
                FROM version_refs r
                JOIN versions cv ON cv.document_id = r.child_document_id AND cv.number = r.child_version
                WHERE r.version_id = ANY(@frontier)
                ORDER BY r.id
                """, new { frontier = frontier.ToArray() });
            frontier = new List<long>();
            foreach (var (childId, childVersionId) in children)
            {
                if (!present.Add(childId)) continue;
                added.Add(childVersionId);
                frontier.Add(childVersionId);
            }
        }
        return added;
    }

    private static KitItemDto ToDto(KitRow i, bool attached) => new()
    {
        DocumentId = i.DocumentId,
        Version = i.Version,
        LatestVersion = i.LatestVersion,
        RelPath = i.RelPath,
        Sha256 = i.Sha256,
        Size = i.Size,
        Cad = i.Cad,
        Kind = DbText.Kind(i.Kind),
        CadVersion = i.CadVersion,
        CadVersionMajor = i.CadVersionMajor,
        Designation = i.Designation,
        Name = i.Name,
        Status = DbText.Status(i.Status),
        ProjectId = i.ProjectId,
        Lock = i.LockUserId is { } userId
            ? new LockDto { DocumentId = i.DocumentId, UserId = userId, UserLogin = i.LockLogin ?? "", UserDisplayName = i.LockDisplayName ?? "", At = DbText.Time(i.LockAt!.Value) }
            : null,
        IsAttachedDrawing = attached,
    };

    /// <summary>Применения всех версий документа.</summary>
    public static async Task<WhereUsedDto> GetWhereUsedAsync(IDbConnection connection, Guid id)
    {
        var childLatest = await connection.ExecuteScalarAsync<int>(
            "SELECT coalesce(max(number), 0) FROM versions WHERE document_id = @id", new { id });
        var rows = await connection.QueryAsync<WhereUsedRow>("""
            SELECT DISTINCT r.child_version, pv.document_id AS parent_document_id, pv.number AS parent_version,
                   pv.rel_path AS parent_rel_path, pv.designation AS parent_designation, pv.name AS parent_name,
                   lv.latest AS parent_latest_version
            FROM version_refs r
            JOIN versions pv ON pv.id = r.version_id
            JOIN LATERAL (SELECT max(x.number) AS latest FROM versions x WHERE x.document_id = pv.document_id) lv ON true
            WHERE r.child_document_id = @id
            ORDER BY parent_rel_path, parent_version DESC, child_version DESC
            """, new { id });
        return new WhereUsedDto
        {
            Items = rows.Select(r => new WhereUsedItemDto
            {
                ChildVersion = r.ChildVersion,
                ParentDocumentId = r.ParentDocumentId,
                ParentVersion = r.ParentVersion,
                ParentRelPath = r.ParentRelPath,
                ParentDesignation = r.ParentDesignation,
                ParentName = r.ParentName,
                ParentLatestVersion = r.ParentLatestVersion,
                IsParentLatest = r.ParentVersion == r.ParentLatestVersion,
                UsesLatestChild = r.ChildVersion == childLatest,
            }).ToList(),
        };
    }

    internal sealed class KitRow
    {
        public long VersionId { get; set; }
        public Guid DocumentId { get; set; }
        public int Version { get; set; }
        public int LatestVersion { get; set; }
        public string RelPath { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string Cad { get; set; } = "";
        public string Kind { get; set; } = "";
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public string? Designation { get; set; }
        public string? Name { get; set; }
        public string Status { get; set; } = "";
        public Guid ProjectId { get; set; }
        public DateTime At { get; set; }
        public Guid? LockUserId { get; set; }
        public string? LockLogin { get; set; }
        public string? LockDisplayName { get; set; }
        public DateTime? LockAt { get; set; }
    }

    private sealed class EdgeRow
    {
        public Guid ParentDocumentId { get; set; }
        public int ParentVersion { get; set; }
        public Guid? ChildDocumentId { get; set; }
        public int? ChildVersion { get; set; }
        public string RefPath { get; set; } = "";
    }

    private sealed class WhereUsedRow
    {
        public int ChildVersion { get; set; }
        public Guid ParentDocumentId { get; set; }
        public int ParentVersion { get; set; }
        public string ParentRelPath { get; set; } = "";
        public string? ParentDesignation { get; set; }
        public string? ParentName { get; set; }
        public int ParentLatestVersion { get; set; }
    }
}
