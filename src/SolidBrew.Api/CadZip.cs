using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

internal sealed record CadFile(string Path, long Bytes, string Sha256);
internal sealed class ArchiveLimitException : Exception;

internal static class CadZip
{
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxFiles = 10_000;

    internal static async Task<IReadOnlyList<CadFile>> ReadAsync(Stream stream, string assembly, string drawing, CancellationToken ct)
    {
        if (!assembly.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ||
            !drawing.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Supply the .SLDASM and .SLDDRW paths.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxFiles) throw new ArchiveLimitException();
        var files = new Dictionary<string, CadFile>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            ValidatePath(path);
            var key = path.Normalize();
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & 0x410) != 0)
                throw new InvalidDataException("ZIP may contain only regular files.");
            if (files.ContainsKey(key) || directories.ContainsKey(key))
                throw new InvalidDataException("ZIP has conflicting file paths.");
            for (var index = path.LastIndexOf('/'); index >= 0; index = path.LastIndexOf('/', index - 1))
            {
                var dir = path[..index];
                var dirKey = dir.Normalize();
                if (files.ContainsKey(dirKey) || (directories.TryGetValue(dirKey, out var previous) && previous != dir))
                    throw new InvalidDataException("ZIP has conflicting directory paths.");
                directories.TryAdd(dirKey, dir);
            }
            if (entry.Length > MaxExpandedBytes - expanded) throw new ArchiveLimitException();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = entry.Open();
            var size = await CopyBoundedAsync(source, Stream.Null, MaxExpandedBytes - expanded, hash, ct);
            if (size != entry.Length) throw new InvalidDataException("ZIP entry size mismatch.");
            expanded += size;
            files.Add(key, new CadFile(path, size, Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        if (!files.Values.Any(f => f.Path == assembly && f.Bytes > 0) ||
            !files.Values.Any(f => f.Path == drawing && f.Bytes > 0))
            throw new InvalidDataException("Both entry files must be non-empty and named exactly as inside the ZIP.");
        return files.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidatePath(string path)
    {
        if (path.Length is 0 or > 1000) throw new InvalidDataException("Invalid path length inside ZIP.");
        foreach (var part in path.Split('/'))
        {
            if (part.Length is 0 or > 255 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => char.IsControl(c) || "<>:\\|?*\"".Contains(c)))
                throw new InvalidDataException("ZIP path is not safe on Windows.");
            var stem = part.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("ZIP contains a reserved Windows name.");
        }
    }

    internal static async Task<long> CopyBoundedAsync(Stream source, Stream target, long limit, IncrementalHash? hash, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                if (read > limit - written) throw new ArchiveLimitException();
                hash?.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
            }
            return written;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
