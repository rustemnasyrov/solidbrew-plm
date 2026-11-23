namespace SolidBrew.Api.Blobs;

/// <summary>Открытый объект архива: поток для чтения и его длина.</summary>
public sealed record BlobContent(Stream Stream, long Length) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Архив содержимого по SHA-256 (hex в нижнем регистре). Объекты только добавляются;
/// содержимое уже проверено вызывающим (хеш совпадает с ключом).
/// </summary>
public interface IBlobStore
{
    Task<bool> ExistsAsync(string sha256, CancellationToken ct);

    /// <summary>Сохранить объект из потока (поток с начала, длина <paramref name="length"/>). Если объект уже есть — ничего не делать.</summary>
    Task PutAsync(string sha256, Stream content, long length, CancellationToken ct);

    /// <summary>Открыть объект для чтения; null — объекта нет.</summary>
    Task<BlobContent?> OpenReadAsync(string sha256, CancellationToken ct);
}

public static class BlobStoreExtensions
{
    /// <summary>Каких объектов из списка нет в архиве (проверка параллельно, порядок сохраняется).</summary>
    public static async Task<List<string>> FindMissingAsync(this IBlobStore store, IEnumerable<string> hashes, CancellationToken ct)
    {
        var distinct = hashes.Distinct(StringComparer.Ordinal).ToList();
        var exists = new bool[distinct.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, distinct.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (i, token) => exists[i] = await store.ExistsAsync(distinct[i], token));
        return distinct.Where((_, i) => !exists[i]).ToList();
    }
}
