using System.Data;
using Dapper;
using SolidBrew.Api.Db;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Api.Services;

/// <summary>Чтение документов, версий и блокировок.</summary>
internal static class DocumentQueries
{
    private const string SummarySelect = """
        SELECT d.id, d.project_id, p.name AS project_name, p.kind AS project_kind, d.cad, d.kind, d.status,
               d.current_rel_path AS rel_path, lv.number AS latest_version, lv.designation, lv.name,
               au.login AS latest_author_login, au.display_name AS latest_author_display_name, lv.at AS latest_at,
               l.user_id AS lock_user_id, lu.login AS lock_login, lu.display_name AS lock_display_name, l.at AS lock_at
        FROM documents d
        JOIN projects p ON p.id = d.project_id
        JOIN LATERAL (
            SELECT v.number, v.designation, v.name, v.author_id, v.at
            FROM versions v WHERE v.document_id = d.id ORDER BY v.number DESC LIMIT 1
        ) lv ON true
        JOIN users au ON au.id = lv.author_id
        LEFT JOIN locks l ON l.document_id = d.id
        LEFT JOIN users lu ON lu.id = l.user_id
        """;

    public static async Task<DocumentSummaryDto?> GetSummaryAsync(IDbConnection connection, Guid id, IDbTransaction? tx = null)
    {
        var row = await connection.QuerySingleOrDefaultAsync<SummaryRow>(SummarySelect + " WHERE d.id = @id", new { id }, tx);
        return row?.ToDto();
    }

    public static async Task<List<DocumentSummaryDto>> SearchAsync(IDbConnection connection, string? q, Guid? projectId, string? author,
        bool includeUnused, int limit)
    {
        var pattern = string.IsNullOrWhiteSpace(q) ? null : "%" + EscapeLike(q.Trim()) + "%";
        var authorText = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        var authorPattern = authorText is null ? null : "%" + EscapeLike(authorText) + "%";
        var rows = await connection.QueryAsync<SummaryRow>(SummarySelect + """

            WHERE (@includeUnused OR d.status = 'active')
              AND (CAST(@projectId AS uuid) IS NULL OR d.project_id = @projectId)
              AND (CAST(@pattern AS text) IS NULL
                   OR lv.designation ILIKE @pattern OR lv.name ILIKE @pattern OR d.current_rel_path ILIKE @pattern)
              AND (CAST(@authorText AS text) IS NULL OR EXISTS (
                   SELECT 1 FROM versions av JOIN users au2 ON au2.id = av.author_id
                   WHERE av.document_id = d.id
                     AND (lower(au2.login) = lower(@authorText) OR au2.display_name ILIKE @authorPattern)))
            ORDER BY lower(d.current_rel_path), d.id
            LIMIT @limit
            """, new { includeUnused, projectId, pattern, authorText, authorPattern, limit });
        return rows.Select(r => r.ToDto()).ToList();
    }

    public static async Task<DocumentDetailDto?> GetDetailAsync(IDbConnection connection, Guid id)
    {
        var summary = await GetSummaryAsync(connection, id);
        if (summary is null) return null;

        var versions = (await connection.QueryAsync<VersionRow>("""
            SELECT v.id, v.number, u.login AS author_login, u.display_name AS author_display_name, v.at, v.checkin_id, c.comment,
                   v.sha256, v.size, v.rel_path, v.cad_version, v.cad_version_major,
                   v.configurations::text AS configurations, v.properties::text AS properties
            FROM versions v
            JOIN users u ON u.id = v.author_id
            JOIN checkins c ON c.id = v.checkin_id
            WHERE v.document_id = @id
            ORDER BY v.number DESC
            """, new { id })).ToList();
        var versionIds = versions.Select(v => v.Id).ToArray();

        var refs = (await connection.QueryAsync<RefRow>("""
            SELECT r.version_id, r.ref_path, r.child_document_id, r.child_version, cv.rel_path AS child_rel_path,
                   (SELECT max(x.number) FROM versions x WHERE x.document_id = r.child_document_id) AS child_latest_version
            FROM version_refs r
            LEFT JOIN versions cv ON cv.document_id = r.child_document_id AND cv.number = r.child_version
            WHERE r.version_id = ANY(@versionIds)
            ORDER BY r.id
            """, new { versionIds })).ToLookup(r => r.VersionId);

        var derived = (await connection.QueryAsync<DerivedRow>("""
            SELECT version_id, file_name, sha256, size FROM derived_files WHERE version_id = ANY(@versionIds) ORDER BY file_name
            """, new { versionIds })).ToLookup(d => d.VersionId);

        return new DocumentDetailDto
        {
            Document = summary,
            Versions = versions.Select(v => new VersionDto
            {
                DocumentId = id,
                Number = v.Number,
                AuthorLogin = v.AuthorLogin,
                AuthorDisplayName = v.AuthorDisplayName,
                At = DbText.Time(v.At),
                CheckinId = v.CheckinId,
                Comment = v.Comment,
                Sha256 = v.Sha256,
                Size = v.Size,
                RelPath = v.RelPath,
                Cad = summary.Cad,
                Kind = summary.Kind,
                CadVersion = v.CadVersion,
                CadVersionMajor = v.CadVersionMajor,
                Configurations = DbText.FromJson<List<string>>(v.Configurations),
                Properties = DbText.FromJson<Dictionary<string, Dictionary<string, string>>>(v.Properties),
                References = refs[v.Id].Select(r => new VersionRefDto
                {
                    RefPath = r.RefPath,
                    ChildDocumentId = r.ChildDocumentId,
                    ChildVersion = r.ChildVersion,
                    ChildRelPath = r.ChildRelPath,
                    ChildLatestVersion = r.ChildLatestVersion,
                }).ToList(),
                Derived = derived[v.Id].Select(d => d.ToDto()).ToList(),
            }).ToList(),
        };
    }

    public static async Task<List<LockDto>> GetLocksAsync(IDbConnection connection, Guid[]? documentIds = null, IDbTransaction? tx = null, bool forUpdate = false)
    {
        var rows = await connection.QueryAsync<LockRow>($"""
            SELECT l.document_id, l.user_id, u.login, u.display_name, l.at
            FROM locks l JOIN users u ON u.id = l.user_id
            WHERE (CAST(@all AS boolean) OR l.document_id = ANY(@documentIds))
            ORDER BY l.at
            {(forUpdate ? "FOR UPDATE OF l" : "")}
            """, new { all = documentIds is null, documentIds = documentIds ?? [] }, tx);
        return rows.Select(r => r.ToDto()).ToList();
    }

    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    internal sealed class SummaryRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = "";
        public string ProjectKind { get; set; } = "";
        public string Cad { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Status { get; set; } = "";
        public string RelPath { get; set; } = "";
        public int LatestVersion { get; set; }
        public string? Designation { get; set; }
        public string? Name { get; set; }
        public string LatestAuthorLogin { get; set; } = "";
        public string LatestAuthorDisplayName { get; set; } = "";
        public DateTime LatestAt { get; set; }
        public Guid? LockUserId { get; set; }
        public string? LockLogin { get; set; }
        public string? LockDisplayName { get; set; }
        public DateTime? LockAt { get; set; }

        public DocumentSummaryDto ToDto() => new()
        {
            Id = Id,
            ProjectId = ProjectId,
            ProjectName = ProjectName,
            ProjectKind = DbText.ProjectKind(ProjectKind),
            Cad = Cad,
            Kind = DbText.Kind(Kind),
            Status = DbText.Status(Status),
            RelPath = RelPath,
            FileName = RelPath.Length == 0 ? "" : Core.Paths.RelPath.FileName(RelPath),
            LatestVersion = LatestVersion,
            Designation = Designation,
            Name = Name,
            LatestAuthorLogin = LatestAuthorLogin,
            LatestAuthorDisplayName = LatestAuthorDisplayName,
            LatestAt = DbText.Time(LatestAt),
            Lock = LockUserId is { } userId
                ? new LockDto { DocumentId = Id, UserId = userId, UserLogin = LockLogin ?? "", UserDisplayName = LockDisplayName ?? "", At = DbText.Time(LockAt!.Value) }
                : null,
        };
    }

    private sealed class VersionRow
    {
        public long Id { get; set; }
        public int Number { get; set; }
        public string AuthorLogin { get; set; } = "";
        public string AuthorDisplayName { get; set; } = "";
        public DateTime At { get; set; }
        public long CheckinId { get; set; }
        public string? Comment { get; set; }
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string RelPath { get; set; } = "";
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public string? Configurations { get; set; }
        public string? Properties { get; set; }
    }

    private sealed class RefRow
    {
        public long VersionId { get; set; }
        public string RefPath { get; set; } = "";
        public Guid? ChildDocumentId { get; set; }
        public int? ChildVersion { get; set; }
        public string? ChildRelPath { get; set; }
        public int? ChildLatestVersion { get; set; }
    }

    internal sealed class DerivedRow
    {
        public long VersionId { get; set; }
        public string FileName { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }

        public DerivedFileDto ToDto() => new() { FileName = FileName, Sha256 = Sha256, Size = Size };
    }

    internal sealed class LockRow
    {
        public Guid DocumentId { get; set; }
        public Guid UserId { get; set; }
        public string Login { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public DateTime At { get; set; }

        public LockDto ToDto() => new() { DocumentId = DocumentId, UserId = UserId, UserLogin = Login, UserDisplayName = DisplayName, At = DbText.Time(At) };
    }
}
