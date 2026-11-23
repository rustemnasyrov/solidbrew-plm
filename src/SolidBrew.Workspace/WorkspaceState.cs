using System.Text.Json;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Состояние рабочей папки: <c>&lt;папка&gt;\.solidbrew\state.json</c>. Одна папка — один проект.</summary>
public sealed class WorkspaceState
{
    public const string FolderName = ".solidbrew";
    public const string FileName = "state.json";

    public int FormatVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public string? ServerUrl { get; set; }
    /// <summary>Последний открытый головной документ (для удобства клиента).</summary>
    public Guid? HeadDocumentId { get; set; }
    public int? HeadVersion { get; set; }
    public List<WorkspaceEntry> Entries { get; set; } = new();

    public WorkspaceEntry? FindByPath(string relPath)
    {
        var key = RelPath.Key(relPath);
        return Entries.FirstOrDefault(e => RelPath.Key(e.RelPath) == key);
    }

    public WorkspaceEntry? FindByDocument(Guid documentId) => Entries.FirstOrDefault(e => e.DocumentId == documentId);

    public static string StatePath(string workRoot) => Path.Combine(Path.GetFullPath(workRoot), FolderName, FileName);

    public static bool Exists(string workRoot) => File.Exists(StatePath(workRoot));

    /// <summary>Прочитать состояние; null — папка не является рабочей папкой SolidBrew.</summary>
    public static WorkspaceState? TryLoad(string workRoot)
    {
        var path = StatePath(workRoot);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkspaceState>(json, PdmJson.Options)
                   ?? throw new WorkspaceException($"Файл состояния рабочей папки пуст: {path}.");
        }
        catch (JsonException ex)
        {
            throw new WorkspaceException($"Файл состояния рабочей папки повреждён: {path}. Переоткройте комплект в новую папку. ({ex.Message})", ex);
        }
    }

    public static WorkspaceState Load(string workRoot) =>
        TryLoad(workRoot) ?? throw new WorkspaceException(
            $"Папка «{workRoot}» не является рабочей папкой SolidBrew. Откройте в неё комплект из хранилища.");

    public void Save(string workRoot)
    {
        var path = StatePath(workRoot);
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            var info = Directory.CreateDirectory(dir);
            info.Attributes |= FileAttributes.Hidden;
        }
        Entries.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        FileOps.WriteAllTextAtomic(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(PdmJson.Options) { WriteIndented = true }));
    }
}

/// <summary>Учтённый файл рабочей папки: какая версия какого документа разложена.</summary>
public sealed class WorkspaceEntry
{
    public string RelPath { get; set; } = string.Empty;
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    /// <summary>Хеш содержимого, как разложено (или сдано).</summary>
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Cad { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; }
    public string? Designation { get; set; }
    public string? Name { get; set; }
    /// <summary>Зависимости этой версии (как опубликованы).</summary>
    public List<WorkspaceDependency> Deps { get; set; } = new();
    /// <summary>Документ взят в работу мной (по последним сведениям).</summary>
    public bool LockedByMe { get; set; }
    /// <summary>Отметка файла после раскладки — чтобы не хешировать неизменённые файлы.</summary>
    public FileStamp? Stamp { get; set; }
}

public sealed class WorkspaceDependency
{
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    public string RefPath { get; set; } = string.Empty;
}
