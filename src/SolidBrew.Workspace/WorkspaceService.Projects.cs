using System.Text;
using SolidBrew.Core.Hashing;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Копия документа в другой рабочей папке пользователя (предупреждение о правке одного документа в двух папках).</summary>
public sealed class WorkspaceCopy
{
    /// <summary>Рабочая папка, где лежит копия.</summary>
    public string WorkRoot { get; init; } = string.Empty;
    /// <summary>Путь файла в той рабочей папке.</summary>
    public string RelPath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    /// <summary>Разложенная там версия.</summary>
    public int Version { get; init; }
    /// <summary>Файл есть на диске.</summary>
    public bool Exists { get; init; }
    /// <summary>Файл изменён относительно разложенной версии (несданные правки).</summary>
    public bool IsModified { get; init; }
    /// <summary>По состоянию той папки документ взят в работу (мной).</summary>
    public bool LockedByMe { get; init; }

    /// <summary>Описание для предупреждения.</summary>
    public string Message =>
        $"Документ лежит и в рабочей папке «{WorkRoot}» ({RelPath}, v{Version})" +
        (!Exists ? ", но файла там нет." : IsModified ? " и там есть несданные правки. Правки в двух папках не сливаются: сдайте или отмените одну из них." : ", без правок.");
}

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Имя подпапки для документов проекта, отличного от проекта рабочей папки: имя проекта, недопустимые для пути символы
    /// заменены на «_», без пробелов и точек в конце.
    /// </summary>
    public static string ProjectFolderName(string projectName)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '|', '?', '*', '/', '\\' };
        var sb = new StringBuilder(projectName?.Length ?? 0);
        foreach (var c in (projectName ?? string.Empty).Normalize(NormalizationForm.FormC))
            sb.Append(invalid.Contains(c) || c < ' ' ? '_' : c);
        var name = sb.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name == "." || name == "..") name = "Проект";
        var upper = name.ToUpperInvariant();
        var stem = upper.Contains('.') ? upper.Substring(0, upper.IndexOf('.')) : upper;
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            name = "_" + name;
        return name;
    }

    /// <summary>Путь в рабочей папке для документа: <c>&lt;подпапка проекта&gt;/&lt;путь документа&gt;</c> или путь документа.</summary>
    public static string LocalRelPath(string? projectFolder, string documentRelPath) =>
        string.IsNullOrEmpty(projectFolder) ? documentRelPath : projectFolder + "/" + documentRelPath;

    /// <summary>Собственный путь документа: путь в рабочей папке без подпапки проекта.</summary>
    public static string StripProjectFolder(string? projectFolder, string relPath)
    {
        if (string.IsNullOrEmpty(projectFolder)) return relPath;
        var prefix = projectFolder + "/";
        return relPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && relPath.Length > prefix.Length ? relPath.Substring(prefix.Length) : relPath;
    }

    /// <summary>
    /// Подпапки проектов: функция «проект документа → подпапка» (null — документ проекта рабочей папки).
    /// Имена проектов запрашиваются у сервера, только если есть документы чужих проектов.
    /// </summary>
    internal async Task<Func<Guid, string?>> ProjectFoldersAsync(Guid workspaceProjectId, IEnumerable<Guid> projectIds, CancellationToken ct)
    {
        var foreign = projectIds.Where(id => id != Guid.Empty && id != workspaceProjectId).Distinct().ToList();
        if (foreign.Count == 0 || workspaceProjectId == Guid.Empty) return _ => null;
        var projects = await Api.GetProjectsAsync(ct).ConfigureAwait(false);
        var names = new Dictionary<Guid, string>();
        foreach (var p in projects) names[p.Id] = ProjectFolderName(p.Name);
        return id => id == Guid.Empty || id == workspaceProjectId ? null
            : names.TryGetValue(id, out var name) ? name : ProjectFolderName(id.ToString("D"));
    }

    /// <summary>
    /// Где ещё (кроме <paramref name="currentRoot"/>) среди известных рабочих папок лежит документ и изменён ли он там
    /// (по state.json папки и хешу файла). Нужен для предупреждения при «Взять в работу» и «Сдать»: правки одного документа
    /// в двух моих рабочих папках не сливаются. Папки, которые не являются рабочими или не читаются, пропускаются.
    /// Хеширует файлы — для больших файлов вызывать не из потока UI.
    /// </summary>
    public static List<WorkspaceCopy> FindOtherWorkspaceCopies(Guid documentId, string? currentRoot, IEnumerable<string> knownWorkspaces)
    {
        var result = new List<WorkspaceCopy>();
        string? current = null;
        if (!string.IsNullOrWhiteSpace(currentRoot))
        {
            try
            {
                current = Root(currentRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in knownWorkspaces ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(ws)) continue;
            string root;
            WorkspaceState? state;
            try
            {
                root = Root(ws);
                if ((current != null && string.Equals(root, current, StringComparison.OrdinalIgnoreCase)) || !seen.Add(root)) continue;
                state = WorkspaceState.TryLoad(root);
            }
            catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }
            var entry = state?.FindByDocument(documentId);
            if (entry == null) continue;

            string full;
            try
            {
                full = RelPath.Combine(root, entry.RelPath);
            }
            catch (ArgumentException)
            {
                continue;
            }
            var exists = File.Exists(full);
            var modified = false;
            if (exists)
            {
                var stamp = FileOps.StampOf(full);
                if (entry.Stamp == null || !entry.Stamp.SameAs(stamp))
                {
                    try
                    {
                        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024);
                        modified = !string.Equals(Sha256Hex.Compute(stream), entry.LocalSha, StringComparison.Ordinal);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        modified = true; // не прочитать (открыт с запретом чтения) — считаем, что там идёт правка
                    }
                }
            }
            result.Add(new WorkspaceCopy
            {
                WorkRoot = root,
                RelPath = entry.RelPath,
                FullPath = full,
                Version = entry.Version,
                Exists = exists,
                IsModified = modified,
                LockedByMe = entry.LockedByMe,
            });
        }
        return result;
    }
}
