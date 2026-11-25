using System.Text.Json;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Workspace;

/// <summary>
/// Кэш прочитанных из CAD-файла свойств, конфигураций и версии CAD по SHA-256 содержимого:
/// <c>&lt;кэш&gt;/cadinfo/&lt;cad&gt;/&lt;2 символа&gt;/&lt;sha256&gt;.json</c>.
/// Одинаковые байты дают одинаковые свойства, поэтому повторное чтение того же файла не открывает его в CAD.
/// Ссылки не кэшируются: CAD разрешает их по месту файла, их всегда читает адаптер (это дёшево).
/// Свойства чертежа бывают связаны со свойствами модели, а модель меняется отдельно от байтов чертежа, поэтому чертёж
/// хранится под составным ключом — хешем от хеша чертежа и хешей найденных по его ссылкам файлов (см. <c>WorkspaceService.ReadCadAsync</c>).
/// Повреждённая или старая запись просто не используется.
/// </summary>
public sealed class CadReadCache
{
    /// <summary>Версия формата записи; при изменении адаптеров, влияющем на свойства, — увеличить.</summary>
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public CadReadCache(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    /// <summary>Можно ли кэшировать сведения документа этого вида по хешу одного его файла (чертёж — по составному ключу).</summary>
    public static bool IsCacheable(DocumentKind kind) => kind is DocumentKind.Part or DocumentKind.Assembly;

    private string PathOf(string cadId, string sha256) =>
        Path.Combine(Root, cadId.ToLowerInvariant(), sha256.Substring(0, 2), sha256 + ".json");

    /// <summary>Сведения из кэша (без ссылок) или null.</summary>
    public CadDocumentInfo? TryGet(string cadId, string sha256)
    {
        if (string.IsNullOrEmpty(cadId) || !Sha256Hex.IsValid(sha256)) return null;
        var path = PathOf(cadId, sha256);
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
            if (entry == null || entry.Format != FormatVersion || entry.Sha256 != sha256) return null;
            return new CadDocumentInfo
            {
                CadVersion = entry.CadVersion,
                CadVersionMajor = entry.CadVersionMajor,
                Configurations = entry.Configurations ?? new List<string>(),
                Properties = entry.Properties ?? new Dictionary<string, Dictionary<string, string>>(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Запомнить сведения. Ошибки записи не мешают операции.</summary>
    public void Put(string cadId, string sha256, CadDocumentInfo info)
    {
        if (string.IsNullOrEmpty(cadId) || !Sha256Hex.IsValid(sha256) || info == null) return;
        var path = PathOf(cadId, sha256);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var entry = new Entry
            {
                Format = FormatVersion,
                Sha256 = sha256,
                CadVersion = info.CadVersion,
                CadVersionMajor = info.CadVersionMajor,
                Configurations = info.Configurations,
                Properties = info.Properties,
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileOps.TryDelete(temp);
        }
    }

    private sealed class Entry
    {
        public int Format { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public List<string>? Configurations { get; set; }
        public Dictionary<string, Dictionary<string, string>>? Properties { get; set; }
    }
}
