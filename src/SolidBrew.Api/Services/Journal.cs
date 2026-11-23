using System.Data;
using Dapper;
using SolidBrew.Api.Db;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Services;

/// <summary>Журнал действий (только добавление; изменение и удаление запрещены триггером).</summary>
internal static class Journal
{
    public static Task WriteAsync(IDbConnection connection, Guid? userId, string action, Guid? documentId = null, int? version = null,
        object? details = null, IDbTransaction? tx = null) =>
        connection.ExecuteAsync("""
            INSERT INTO journal (user_id, action, document_id, version, details)
            VALUES (@userId, @action, @documentId, @version, CAST(@details AS jsonb))
            """,
            new { userId, action, documentId, version, details = details is null ? null : DbText.ToJson(details) }, tx);

    public static async Task<List<JournalEntryDto>> ReadAsync(IDbConnection connection, Guid? documentId, int limit)
    {
        var rows = await connection.QueryAsync<Row>("""
            SELECT j.id, j.at, coalesce(u.login, '') AS login, j.action, j.document_id, j.version, j.details::text AS details
            FROM journal j LEFT JOIN users u ON u.id = j.user_id
            WHERE (CAST(@documentId AS uuid) IS NULL OR j.document_id = @documentId)
            ORDER BY j.id DESC
            LIMIT @limit
            """, new { documentId, limit });
        return rows.Select(r => new JournalEntryDto
        {
            Id = r.Id,
            At = DbText.Time(r.At),
            UserLogin = r.Login,
            Action = r.Action,
            DocumentId = r.DocumentId,
            Version = r.Version,
            Details = r.Details,
        }).ToList();
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public DateTime At { get; set; }
        public string Login { get; set; } = "";
        public string Action { get; set; } = "";
        public Guid? DocumentId { get; set; }
        public int? Version { get; set; }
        public string? Details { get; set; }
    }
}
