using System.Collections.Concurrent;
using System.IO;

namespace SolidBrew.Client.Infrastructure;

/// <summary>
/// Поиск файла по ссылке документа CAD, которого нет в PDM. Путь в ссылке часто устаревший (папку переименовали или
/// перенесли), поэтому, как SOLIDWORKS, ищем файл с тем же именем:
/// 1) по пути из ссылки; 2) в папке документа, которому принадлежит ссылка;
/// 3) под ближайшей существующей папкой-предком пути из ссылки (ограниченно: глубина и число просмотренных папок),
///    из нескольких совпадений — то, у которого больше совпадающих имён папок с конца пути ссылки (если оно одно).
/// </summary>
public static class LinkLocator
{
    private const int MaxDepth = 4;
    private const int MaxFolders = 3000;

    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сбросить запомненные результаты (файлы могли появиться или переместиться).</summary>
    public static void Reset() => Cache.Clear();

    public static string? Find(string refPath, string? ownerFolder)
    {
        if (string.IsNullOrWhiteSpace(refPath)) return null;
        var path = refPath.Replace('/', '\\');
        return Cache.GetOrAdd(path + "|" + ownerFolder, _ => Locate(path, ownerFolder));
    }

    private static string? Locate(string path, string? ownerFolder)
    {
        try
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return null;
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;

            if (!string.IsNullOrEmpty(ownerFolder))
            {
                var near = Path.Combine(ownerFolder, name);
                if (File.Exists(near)) return near;
            }

            if (!Path.IsPathFullyQualified(path)) return null;
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            // Не обходим корень диска целиком — слишком широко и долго.
            if (string.IsNullOrEmpty(dir) || Path.GetPathRoot(dir)!.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return null;
            return Best(path, Search(dir, name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static List<string> Search(string root, string name)
    {
        var found = new List<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var seen = 0;
        while (queue.Count > 0 && seen < MaxFolders)
        {
            var (dir, depth) = queue.Dequeue();
            seen++;
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) found.Add(candidate);
                if (depth >= MaxDepth) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var leaf = Path.GetFileName(sub);
                    if (leaf.StartsWith('.') || leaf.StartsWith('$')) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found;
    }

    private static string? Best(string refPath, List<string> found)
    {
        if (found.Count <= 1) return found.FirstOrDefault();
        var want = Folders(refPath);
        int Score(string p)
        {
            var have = Folders(p);
            var n = 0;
            while (n < want.Length && n < have.Length && string.Equals(want[^(n + 1)], have[^(n + 1)], StringComparison.OrdinalIgnoreCase)) n++;
            return n;
        }
        var ranked = found.Select(p => (Path: p, Score: Score(p))).OrderByDescending(x => x.Score).ToList();
        return ranked[0].Score > ranked[1].Score ? ranked[0].Path : null;
    }

    private static string[] Folders(string path) =>
        (Path.GetDirectoryName(path) ?? string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries);
}
