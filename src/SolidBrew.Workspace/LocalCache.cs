using System.Security.Cryptography;
using SolidBrew.Core.Api;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Workspace;

/// <summary>Объект архива: хеш и размер.</summary>
public readonly record struct CacheObject(string Sha256, long Size);

/// <summary>
/// Локальный кэш неизменяемых объектов: <c>objects/&lt;2 символа&gt;/&lt;sha256&gt;</c>, атрибут «только для чтения».
/// Запись — через временный файл и переименование; хеш скачанного проверяется.
/// </summary>
public sealed class LocalCache
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Корень по умолчанию: <c>%LOCALAPPDATA%\SolidBrew\cache</c>.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidBrew", "cache");

    public LocalCache(string? root = null)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(root) ? DefaultRoot : root);
        Directory.CreateDirectory(ObjectsRoot);
    }

    public string Root { get; }

    public string ObjectsRoot => Path.Combine(Root, "objects");

    private string TempRoot => Path.Combine(Root, "tmp");

    /// <summary>Папка временных копий (постановка на учёт, сдача).</summary>
    public string StagingRoot => Path.Combine(Root, "staging");

    /// <summary>Путь объекта в кэше (файл может отсутствовать).</summary>
    public string PathOf(string sha256)
    {
        if (!Sha256Hex.IsValid(sha256)) throw new ArgumentException($"Неверный хеш объекта «{sha256}».", nameof(sha256));
        return Path.Combine(ObjectsRoot, sha256.Substring(0, 2), sha256);
    }

    public bool Contains(string sha256) => File.Exists(PathOf(sha256));

    /// <summary>Новая пустая временная папка <c>staging/&lt;guid&gt;</c>.</summary>
    public string CreateStagingFolder()
    {
        var path = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Какие из объектов отсутствуют в кэше.</summary>
    public IReadOnlyList<CacheObject> Missing(IEnumerable<CacheObject> objects)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<CacheObject>();
        foreach (var o in objects)
        {
            if (seen.Add(o.Sha256) && !Contains(o.Sha256)) list.Add(o);
        }
        return list;
    }

    /// <summary>
    /// Докачать недостающие объекты с сервера. Уже имеющиеся не скачиваются. Возвращает число скачанных.
    /// Повреждённое скачивание (хеш не совпал) — <see cref="WorkspaceException"/>, временный файл удаляется.
    /// </summary>
    public async Task<int> EnsureAsync(IPdmApi api, IEnumerable<CacheObject> objects, IProgress<WorkProgress>? progress = null, CancellationToken ct = default)
    {
        if (api == null) throw new ArgumentNullException(nameof(api));
        var missing = Missing(objects);
        if (missing.Count == 0) return 0;

        long total = missing.Sum(m => m.Size);
        long done = 0;
        var index = 0;
        foreach (var obj in missing)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var baseDone = done;
            var msg = $"Скачивание {index} из {missing.Count} ({Texts.Bytes(obj.Size)})";
            progress?.Report(new WorkProgress(WorkStage.Downloading, done, total, msg));
            var inner = progress == null ? null : new SyncProgress<long>(bytes =>
                progress.Report(new WorkProgress(WorkStage.Downloading, baseDone + bytes, total, msg)));
            await DownloadOneAsync(api, obj, inner, ct).ConfigureAwait(false);
            done += obj.Size;
        }
        progress?.Report(new WorkProgress(WorkStage.Downloading, total, total, $"Скачано объектов: {missing.Count}"));
        return missing.Count;
    }

    private async Task DownloadOneAsync(IPdmApi api, CacheObject obj, IProgress<long>? progress, CancellationToken ct)
    {
        var final = PathOf(obj.Sha256);
        Directory.CreateDirectory(TempRoot);
        var temp = Path.Combine(TempRoot, obj.Sha256 + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            string actual;
            long size;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            using (var hashing = new HashingWriteStream(file))
            {
                await api.DownloadObjectAsync(obj.Sha256, hashing, progress, ct).ConfigureAwait(false);
                await hashing.FlushAsync(ct).ConfigureAwait(false);
                actual = hashing.FinishHash();
                size = hashing.Length;
            }

            if (!string.Equals(actual, obj.Sha256, StringComparison.Ordinal) || (obj.Size > 0 && size != obj.Size))
            {
                throw new WorkspaceException(
                    $"Скачанный объект {obj.Sha256.Substring(0, 12)}… повреждён: хеш или размер не совпадает с ожидаемым. " +
                    "Повторите операцию; если ошибка повторяется — сообщите администратору PDM.");
            }

            await CommitAsync(temp, final).ConfigureAwait(false);
        }
        finally
        {
            FileOps.TryDelete(temp);
        }
    }

    /// <summary>
    /// Положить локальный файл в кэш под известным хешем (после сдачи или постановки на учёт).
    /// Хеш перепроверяется; если не совпал — <see cref="WorkspaceException"/>.
    /// </summary>
    public async Task AddFileAsync(string sourceFile, string sha256, CancellationToken ct = default)
    {
        if (Contains(sha256)) return;
        Directory.CreateDirectory(TempRoot);
        var temp = Path.Combine(TempRoot, sha256 + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var (actual, _) = await FileOps.CopyForReadingAsync(sourceFile, temp, ct).ConfigureAwait(false);
            if (!string.Equals(actual, sha256, StringComparison.Ordinal))
                throw new WorkspaceException($"Файл «{Path.GetFileName(sourceFile)}» изменился во время операции: хеш не совпадает. Повторите операцию.");
            await CommitAsync(temp, PathOf(sha256)).ConfigureAwait(false);
        }
        finally
        {
            FileOps.TryDelete(temp);
        }
    }

    private static Task CommitAsync(string temp, string final)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        File.SetAttributes(temp, FileAttributes.ReadOnly);
        try
        {
            if (!File.Exists(final)) File.Move(temp, final, overwrite: false);
        }
        catch (IOException) when (File.Exists(final))
        {
            // Тот же объект параллельно положил другой процесс — содержимое одинаково.
        }
        return Task.CompletedTask;
    }

    /// <summary>Проверить хеш объекта в кэше; повреждённый удаляется. Возвращает true, если объект цел.</summary>
    public async Task<bool> VerifyAsync(string sha256, CancellationToken ct = default)
    {
        var path = PathOf(sha256);
        if (!File.Exists(path)) return false;
        var actual = await Sha256Hex.ComputeFileAsync(path, ct).ConfigureAwait(false);
        if (actual == sha256) return true;
        FileOps.TryDelete(path);
        return false;
    }

    /// <summary>Поток-обёртка, считающий SHA-256 и размер записываемого.</summary>
    private sealed class HashingWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public HashingWriteStream(Stream inner) => _inner = inner;

        public string FinishHash() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            _inner.Write(buffer, offset, count);
            _length += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _length += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
