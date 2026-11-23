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

    /// <summary>Комплект версии. Элементы уникальны по (документ, версия); головной — первый.</summary>
    public static async Task<(KitDto Kit, List<long> VersionIds)> GetKitAsync(IDbConnection connection, Guid id, int version)
    {
        var items = (await connection.QueryAsync<KitRow>("""
            WITH RECURSIVE kit(document_id, number) AS (
                SELECT CAST(@id AS uuid), CAST(@version AS integer)
              UNION
                SELECT r.child_document_id, r.child_version
                FROM kit k
                JOIN versions v ON v.document_id = k.document_id AND v.number = k.number
                JOIN version_refs r ON r.version_id = v.id
                WHERE r.child_document_id IS NOT NULL
            )
            SELECT v.id AS version_id, v.document_id, v.number AS version, lv.latest AS latest_version, v.rel_path, v.sha256, v.size,
                   d.cad, d.kind, v.cad_version, v.cad_version_major, v.designation, v.name, d.status, d.project_id, v.at,
                   l.user_id AS lock_user_id, lu.login AS lock_login, lu.display_name AS lock_display_name, l.at AS lock_at
            FROM kit k
            JOIN versions v ON v.document_id = k.document_id AND v.number = k.number
            JOIN documents d ON d.id = v.document_id
            JOIN LATERAL (SELECT max(x.number) AS latest FROM versions x WHERE x.document_id = d.id) lv ON true
            LEFT JOIN locks l ON l.document_id = d.id
            LEFT JOIN users lu ON lu.id = l.user_id
            ORDER BY (v.document_id = @id AND v.number = @version) DESC, lower(v.rel_path), v.number
            """, new { id, version })).ToList();

        var versionIds = items.Select(i => i.VersionId).ToArray();
        var refs = await connection.QueryAsync<EdgeRow>("""
            SELECT pv.document_id AS parent_document_id, pv.number AS parent_version, r.child_document_id, r.child_version, r.ref_path
            FROM version_refs r JOIN versions pv ON pv.id = r.version_id
            WHERE r.version_id = ANY(@versionIds)
            ORDER BY r.id
            """, new { versionIds });

        var kit = new KitDto { HeadDocumentId = id, HeadVersion = version };
        kit.Items = items.Select(i => new KitItemDto
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
        }).ToList();
        foreach (var r in refs)
        {
            if (r.ChildDocumentId is { } childId && r.ChildVersion is { } childVersion)
                kit.Edges.Add(new KitEdgeDto { ParentDocumentId = r.ParentDocumentId, ParentVersion = r.ParentVersion, ChildDocumentId = childId, ChildVersion = childVersion, RefPath = r.RefPath });
            else
                kit.Unresolved.Add(new KitUnresolvedRefDto { ParentDocumentId = r.ParentDocumentId, ParentVersion = r.ParentVersion, RefPath = r.RefPath });
        }
        return (kit, items.Select(i => i.VersionId).ToList());
    }

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
