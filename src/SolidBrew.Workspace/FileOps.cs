using System.Security.Cryptography;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Workspace;

/// <summary>Операции с файлами: атрибут «только для чтения», копирование через временный файл, хеширование копий.</summary>
public static class FileOps
{
    private const int BufferSize = 1024 * 1024;

    public static bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    public static void SetReadOnly(string path, bool readOnly)
    {
        var attrs = File.GetAttributes(path);
        var updated = readOnly ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly;
        if (updated != attrs) File.SetAttributes(path, updated);
    }

    /// <summary>
    /// Положить копию <paramref name="source"/> в <paramref name="destination"/>: запись во временный файл рядом и замена.
    /// Существующий файл заменяется (атрибут «только для чтения» снимается). Затем ставится нужный атрибут.
    /// </summary>
    public static void PlaceCopy(string source, string destination, bool readOnly)
    {
        var dir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sbtmp");
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.SetAttributes(temp, FileAttributes.Normal);
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow);
            if (File.Exists(destination)) SetReadOnly(destination, false);
            File.Move(temp, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new WorkspaceException(
                $"Не удалось записать файл «{Path.GetFileName(destination)}» ({destination}): {ex.Message} " +
                "Возможно, он открыт в CAD или другой программе — закройте его и повторите.", ex);
        }
        if (readOnly) SetReadOnly(destination, true);
    }

    /// <summary>Удалить файл, сняв «только для чтения». Ошибки не скрываются.</summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path)) return;
        SetReadOnly(path, false);
        File.Delete(path);
    }

    public static void TryDelete(string path)
    {
        try
        {
            Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                TryDelete(f);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Скопировать файл только чтением исходника (FileShare.ReadWrite — файл может быть открыт в CAD) и посчитать хеш копии.
    /// Исходник и его атрибуты не меняются.
    /// </summary>
    public static async Task<(string Sha256, long Size)> CopyForReadingAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var sha = SHA256.Create();
        long size = 0;
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                size += read;
            }
            sha.TransformFinalBlock(buffer, 0, 0);
        }
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), size);
    }

    public static Task<string> HashAsync(string path, CancellationToken ct) => Sha256Hex.ComputeFileAsync(path, ct);

    /// <summary>Отметка файла (размер + время изменения) для быстрой проверки «не изменён» без хеширования.</summary>
    public static FileStamp? StampOf(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        return new FileStamp { Length = info.Length, LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks };
    }

    /// <summary>Записать текст атомарно: временный файл + замена.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>Размер и время изменения файла.</summary>
public sealed class FileStamp
{
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }

    public bool SameAs(FileStamp? other) => other != null && other.Length == Length && other.LastWriteUtcTicks == LastWriteUtcTicks;
}
