namespace SolidBrew.Api.Blobs;

/// <summary>Архив в каталоге на диске: <c>&lt;root&gt;/objects/&lt;2 символа&gt;/&lt;sha256&gt;</c>. Для тестов и установки без MinIO.</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;

    public FileSystemBlobStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct) => Task.FromResult(File.Exists(PathOf(sha256)));

    public async Task PutAsync(string sha256, Stream content, long length, CancellationToken ct)
    {
        var target = PathOf(sha256);
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, 1 << 20, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            MakeReadOnly(temp);
            try
            {
                File.Move(temp, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                // Тот же объект параллельно записал другой запрос — содержимое одинаковое.
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.SetAttributes(temp, FileAttributes.Normal);
                File.Delete(temp);
            }
        }
    }

    /// <summary>Записанный объект только для чтения: случайная перезапись невозможна.</summary>
    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        else
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public Task<BlobContent?> OpenReadAsync(string sha256, CancellationToken ct)
    {
        var path = PathOf(sha256);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<BlobContent?>(new BlobContent(stream, stream.Length));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<BlobContent?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<BlobContent?>(null);
        }
    }

    private string PathOf(string sha256) => Path.Combine(_root, "objects", sha256[..2], sha256);
}
