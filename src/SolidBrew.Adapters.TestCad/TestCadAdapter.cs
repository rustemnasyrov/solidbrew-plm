using System.Text;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Adapters.TestCad;

/// <summary>
/// Тестовый CAD: текстовые файлы <c>.tcprt</c> (деталь), <c>.tcasm</c> (сборка), <c>.tcdrw</c> (чертёж).
/// Формат — <see cref="TestCadFormat"/>. Открытие имитирует поиск компонентов, как в SOLIDWORKS.
/// </summary>
public sealed class TestCadAdapter : ICadAdapter
{
    public const string Id = "testcad";

    private static readonly string[] Exts = { TestCadFormat.PartExtension, TestCadFormat.AssemblyExtension, TestCadFormat.DrawingExtension };

    public string CadId => Id;

    public string DisplayName => "TestCad";

    public IReadOnlyCollection<string> Extensions => Exts;

    /// <summary>
    /// Имитация документов, открытых в CAD (полные пути). Как в SOLIDWORKS: ссылки в открытом документе
    /// <see cref="ReplaceReferencesAsync"/> не меняет (<see cref="CadException"/>), а при открытии сборки
    /// одноимённый открытый документ берётся из памяти вместо файла, найденного по ссылке.
    /// </summary>
    public ISet<string> OpenDocuments { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsDocument(string path) => TestCadFormat.KindOf(path) != DocumentKind.Other;

    public DocumentKind KindOf(string path) => TestCadFormat.KindOf(path);

    public async Task<CadDocumentInfo> ReadAsync(string path, CancellationToken ct = default)
    {
        var text = await ReadTextAsync(path, ct).ConfigureAwait(false);
        return TestCadFormat.Parse(path, text);
    }

    public async Task ReplaceReferencesAsync(string documentPath, IReadOnlyDictionary<string, string> oldToNew, CancellationToken ct = default)
    {
        if (oldToNew == null) throw new ArgumentNullException(nameof(oldToNew));
        if (OpenDocuments.Contains(Path.GetFullPath(documentPath)))
            throw new CadException($"Документ «{Path.GetFileName(documentPath)}» открыт в TestCad. Ссылки меняются только в закрытом файле: закройте его и повторите.");
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(documentPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CadException($"Не удалось прочитать «{Path.GetFileName(documentPath)}»: {ex.Message}", ex);
        }

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var sb = new StringBuilder(text.Length + 64);
        var changed = false;
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            var eolEnd = end;
            if (eolEnd < text.Length && text[eolEnd] == '\r') eolEnd++;
            if (eolEnd < text.Length && text[eolEnd] == '\n') eolEnd++;

            var line = text.Substring(start, end - start);
            var eol = text.Substring(end, eolEnd - end);
            var trimmed = line.Trim();
            if (TestCadFormat.TryValue(trimmed, TestCadFormat.RefPrefix, out var saved) && TryMap(oldToNew, saved, out var replacement))
            {
                var indent = line.Substring(0, line.Length - line.TrimStart().Length);
                sb.Append(indent).Append("ref: ").Append(replacement);
                changed = true;
            }
            else
            {
                sb.Append(line);
            }
            sb.Append(eol);
            start = eolEnd;
        }

        if (!changed) return;

        var outBytes = new List<byte>();
        if (hasBom) outBytes.AddRange(new byte[] { 0xEF, 0xBB, 0xBF });
        outBytes.AddRange(new UTF8Encoding(false).GetBytes(sb.ToString()));

        var full = Path.GetFullPath(documentPath);
        if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new CadException($"Файл «{Path.GetFileName(full)}» только для чтения: возьмите документ в работу, чтобы изменить ссылки.");

        var temp = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, outBytes.ToArray(), ct).ConfigureAwait(false);
            File.Move(temp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new CadException($"Не удалось записать «{Path.GetFileName(full)}»: {ex.Message}", ex);
        }
    }

    public async Task<OpenReport> OpenAndVerifyAsync(string headPath, string workRoot, CancellationToken ct = default)
    {
        var report = new OpenReport { HeadPath = Path.GetFullPath(headPath) };
        if (!File.Exists(report.HeadPath))
        {
            report.Problems.Add($"Головной документ «{Path.GetFileName(headPath)}» не найден: {report.HeadPath}.");
            return report;
        }

        var head = await ReadAsync(report.HeadPath, ct).ConfigureAwait(false);
        var components = new List<LoadedComponent>();
        var loadedByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { report.HeadPath };
        loadedByName[Path.GetFileName(report.HeadPath)] = report.HeadPath;
        var openInCad = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var open in OpenDocuments)
            openInCad.TryAdd(Path.GetFileName(open), Path.GetFullPath(open));
        await LoadChildrenAsync(report.HeadPath, head, workRoot, components, loadedByName, openInCad, visited, report.Problems, ct).ConfigureAwait(false);

        var configs = head.Configurations.Count > 0 ? head.Configurations : new List<string> { "По умолчанию" };
        foreach (var name in configs)
        {
            report.Configurations.Add(new OpenConfigurationReport
            {
                Name = name,
                Components = components.Select(c => new LoadedComponent { Name = c.Name, LoadedPath = c.LoadedPath, InsideWorkRoot = c.InsideWorkRoot }).ToList(),
            });
        }

        foreach (var c in components.Where(c => !c.InsideWorkRoot))
            report.Problems.Add($"Компонент «{c.Name}» загружен не из рабочей папки: {c.LoadedPath}.");

        report.Ok = report.Problems.Count == 0;
        return report;
    }

    private async Task LoadChildrenAsync(string docPath, CadDocumentInfo info, string workRoot, List<LoadedComponent> components,
        Dictionary<string, string> loadedByName, Dictionary<string, string> openInCad, HashSet<string> visited, List<string> problems, CancellationToken ct)
    {
        var docDir = Path.GetDirectoryName(docPath)!;
        foreach (var reference in info.References)
        {
            ct.ThrowIfCancellationRequested();
            // Как в SOLIDWORKS: сначала документ в памяти с тем же именем, затем поиск файла.
            var located = Locate(docDir, reference.SavedPath);
            var name = located != null ? Path.GetFileName(located) : reference.FileName;
            if (loadedByName.TryGetValue(name, out var already))
            {
                if (located != null && !string.Equals(already, located, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Одноимённые компоненты «{name}»: уже загружен {already}, ссылка из «{Path.GetFileName(docPath)}» нашла {located}. CAD использует первый — возможна подмена.");
                continue;
            }

            string found;
            if (openInCad.TryGetValue(name, out var inMemory))
            {
                found = inMemory; // уже открыт в CAD — берётся из памяти, файл по ссылке не читается
            }
            else if (located == null)
            {
                problems.Add($"Компонент «{reference.FileName}» (ссылка из «{Path.GetFileName(docPath)}»: {reference.SavedPath}) не найден.");
                continue;
            }
            else
            {
                found = located;
            }

            loadedByName[name] = found;
            components.Add(new LoadedComponent { Name = name, LoadedPath = found, InsideWorkRoot = IsInside(workRoot, found) });

            if (visited.Add(found) && IsDocument(found))
            {
                CadDocumentInfo child;
                try
                {
                    child = await ReadAsync(found, ct).ConfigureAwait(false);
                }
                catch (CadException ex)
                {
                    problems.Add(ex.Message);
                    continue;
                }
                await LoadChildrenAsync(found, child, workRoot, components, loadedByName, openInCad, visited, problems, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Поиск файла ссылки, как в SOLIDWORKS (проверено на 2018): (1) файл с тем же именем в папке ссылающегося документа;
    /// (2) сохранённый путь, если он абсолютный и файл существует. Относительные пути («..\») SOLIDWORKS не использует —
    /// они не разрешаются (кроме случая «то же имя в той же папке»). Поиск «в памяти по имени» — в <see cref="OpenAndVerifyAsync"/>.
    /// </summary>
    public static string? Locate(string documentFolder, string savedPath)
    {
        var fileName = CadReference.FileNameOf(savedPath);
        if (fileName.Length > 0)
        {
            var sameFolder = Path.Combine(documentFolder, fileName);
            if (File.Exists(sameFolder)) return Path.GetFullPath(sameFolder);
        }

        var normalized = savedPath.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            if (Path.IsPathFullyQualified(normalized) && File.Exists(normalized)) return Path.GetFullPath(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        return null;
    }

    private static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMap(IReadOnlyDictionary<string, string> map, string saved, out string replacement)
    {
        if (map.TryGetValue(saved, out replacement!)) return true;
        foreach (var pair in map)
        {
            if (string.Equals(pair.Key, saved, StringComparison.OrdinalIgnoreCase))
            {
                replacement = pair.Value;
                return true;
            }
        }
        replacement = string.Empty;
        return false;
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CadException($"Не удалось прочитать файл «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
