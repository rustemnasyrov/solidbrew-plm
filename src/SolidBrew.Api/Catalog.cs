using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

internal sealed record Item(Guid Id, string Designation, string Name, string Kind);
internal sealed record SnapshotInfo(int Version, string Assembly, string Drawing, long ArchiveBytes, string Sha256, DateTime CreatedAt);
internal sealed record Snapshot(SnapshotInfo Info, string ObjectKey, IReadOnlyList<CadFile> Files)
{
    internal int Version => Info.Version;
    internal object PublicDetail() => new { Info.Version, Info.Assembly, Info.Drawing, Info.ArchiveBytes, Info.Sha256, Info.CreatedAt, status = "working", Files };
}

internal sealed class Catalog(NpgsqlDataSource db)
{
    internal async Task InitializeAsync()
    {
        await using var command = db.CreateCommand("""
            CREATE TABLE IF NOT EXISTS pdm_item (
                id uuid PRIMARY KEY, designation text NOT NULL, designation_key text NOT NULL UNIQUE,
                name text NOT NULL, kind text NOT NULL CHECK (kind IN ('part','assembly','complex','kit','undetermined'))
            );
            CREATE TABLE IF NOT EXISTS pdm_snapshot (
                item_id uuid NOT NULL REFERENCES pdm_item(id),
                version integer NOT NULL CHECK (version > 0),
                assembly text NOT NULL, drawing text NOT NULL, archive_bytes bigint NOT NULL CHECK (archive_bytes > 0),
                sha256 text NOT NULL CHECK (length(sha256) = 64),
                created_at timestamptz NOT NULL DEFAULT now(), object_key text NOT NULL UNIQUE, files jsonb NOT NULL,
                PRIMARY KEY (item_id, version)
            );
            """);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(ct);
    }

    internal async Task<IReadOnlyList<Item>> ItemsAsync(CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT id,designation,name,kind FROM pdm_item ORDER BY designation,id");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<Item>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadItem(reader));
        return rows;
    }

    internal async Task<Item?> GetItemAsync(Guid id, CancellationToken ct)
    {
        await using var command = db.CreateCommand("SELECT id,designation,name,kind FROM pdm_item WHERE id=$1");
        SetParameters(command, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    internal async Task<bool> CreateItemAsync(Item item, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            INSERT INTO pdm_item(id,designation,designation_key,name,kind) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT (designation_key) DO NOTHING
            """);
        SetParameters(command, item.Id, item.Designation, item.Designation.ToUpperInvariant(), item.Name, item.Kind);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    internal async Task<IReadOnlyList<SnapshotInfo>> SnapshotsAsync(Guid id, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT version,assembly,drawing,archive_bytes,sha256,created_at FROM pdm_snapshot WHERE item_id=$1 ORDER BY version DESC
            """);
        SetParameters(command, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<SnapshotInfo>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadInfo(reader));
        return rows;
    }

    internal async Task<Snapshot?> GetSnapshotAsync(Guid id, int version, CancellationToken ct)
    {
        await using var command = db.CreateCommand("""
            SELECT version,assembly,drawing,archive_bytes,sha256,created_at,object_key,files
            FROM pdm_snapshot WHERE item_id=$1 AND version=$2
            """);
        SetParameters(command, id, version);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new Snapshot(ReadInfo(reader), reader.GetString(6), JsonSerializer.Deserialize<CadFile[]>(reader.GetString(7))!) : null;
    }

    internal async Task<Snapshot> AddSnapshotAsync(Guid id, string assembly, string drawing, long bytes, string hash, string key, IReadOnlyList<CadFile> files, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var rowLock = new NpgsqlCommand("SELECT id FROM pdm_item WHERE id=$1 FOR UPDATE", conn, tx))
        {
            SetParameters(rowLock, id);
            if (await rowLock.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Item no longer exists.");
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO pdm_snapshot(item_id,version,assembly,drawing,archive_bytes,sha256,object_key,files)
            SELECT $1,COALESCE(MAX(version),0)+1,$2,$3,$4,$5,$6,$7 FROM pdm_snapshot WHERE item_id=$1
            RETURNING version,assembly,drawing,archive_bytes,sha256,created_at
            """, conn, tx);
        SetParameters(insert, id, assembly, drawing, bytes, hash, key);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(files) });
        SnapshotInfo info;
        await using (var reader = await insert.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Snapshot insertion did not return a row.");
            info = ReadInfo(reader);
        }
        await tx.CommitAsync(ct);
        return new Snapshot(info, key, files);
    }

    private static void SetParameters(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
    private static Item ReadItem(NpgsqlDataReader row) => new(row.GetGuid(0), row.GetString(1), row.GetString(2), row.GetString(3));
    private static SnapshotInfo ReadInfo(NpgsqlDataReader row) => new(row.GetInt32(0), row.GetString(1), row.GetString(2), row.GetInt64(3), row.GetString(4), row.GetDateTime(5));
}
