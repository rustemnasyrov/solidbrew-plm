using System.Security.Cryptography;
using System.Text;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Tests;

/// <summary>Построение сдач: загрузка «файлов» и элементы сдачи.</summary>
internal static class TestCad
{
    public const string Cad = "testcad";

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Загружает содержимое (если его нет) и возвращает (sha, size).</summary>
    public static async Task<(string Sha, long Size)> PutAsync(PdmApiClient client, string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var sha = Sha(data);
        var missing = await client.GetMissingObjectsAsync([sha]);
        if (missing.Count > 0)
        {
            using var stream = new MemoryStream(data);
            await client.UploadObjectAsync(sha, stream, data.Length, null);
        }
        return (sha, data.Length);
    }

    public static async Task<CheckinItem> NewAsync(PdmApiClient client, string tempKey, string relPath, DocumentKind kind, string content,
        params CheckinReference[] refs)
    {
        var (sha, size) = await PutAsync(client, content);
        return new CheckinItem
        {
            TempKey = tempKey,
            RelPath = relPath,
            Sha256 = sha,
            Size = size,
            Cad = Cad,
            Kind = kind,
            Configurations = ["По умолчанию"],
            Properties = new() { [""] = new() { ["Обозначение"] = Path.GetFileNameWithoutExtension(relPath), ["Наименование"] = "Изделие " + tempKey } },
            References = refs.ToList(),
        };
    }

    public static async Task<CheckinItem> EditAsync(PdmApiClient client, Guid id, int baseVersion, string relPath, DocumentKind kind, string content,
        params CheckinReference[] refs)
    {
        var item = await NewAsync(client, "", relPath, kind, content, refs);
        item.TempKey = null;
        item.DocumentId = id;
        item.BaseVersion = baseVersion;
        return item;
    }

    public static CheckinReference RefTemp(string refPath, string tempKey) => new() { RefPath = refPath, TempKey = tempKey };

    public static CheckinReference RefPublished(string refPath, Guid id, int version) => new() { RefPath = refPath, DocumentId = id, Version = version };

    public static CheckinReference RefInCheckin(string refPath, Guid id) => new() { RefPath = refPath, DocumentId = id };

    public static CheckinReference RefMissing(string refPath) => new() { RefPath = refPath };

    public static CheckinRequest Request(Guid projectId, params CheckinItem[] items) =>
        new() { ProjectId = projectId, Comment = "тест", Items = items.ToList() };
}
