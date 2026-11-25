using System.Diagnostics;
using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Core.Cad;

namespace SwProbe;

internal sealed class CommandResult
{
    public int ExitCode { get; set; }
    public object? Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>Команды прототипа. Общие для запуска из командной строки и для исполнителя заданий.</summary>
internal static class Commands
{
    /// <summary>Журнал хода длинных команд (исполнитель пишет его в serve.log).</summary>
    public static Action<string>? Progress { get; set; }

    public static async Task<CommandResult> ExecuteAsync(SolidWorksAdapter adapter, ProbeArgs a, CancellationToken ct)
    {
        try
        {
            adapter.Options.AssemblyReadMode = a.AsmMode;
            adapter.Options.SearchDependencies = a.Search;
            adapter.Options.StartIfNotRunning = a.Start;
            adapter.Options.SkipPropertiesIfBlocked = a.Command == "scan";
            object output = a.Command switch
            {
                "info" => await InfoAsync(adapter, ct),
                "read" => await adapter.ReadDetailedAsync(Arg(a, 0, "файл"), ct),
                "deps" => await DepsAsync(adapter, Arg(a, 0, "файл"), a.Traverse, a.Search, ct),
                "scan" => await ScanAsync(adapter, Arg(a, 0, "папка"), a.Recursive, ct),
                "make-testkit" => await adapter.RunAsync(s => TestKit.Make(s, WriteGuard.Check(Arg(a, 0, "папка"), a)), ct),
                "open-verify" => await adapter.OpenAndVerifyDetailedAsync(Arg(a, 0, "головной файл"), Arg(a, 1, "workRoot"), ct),
                "close" => await CloseAsync(adapter, Arg(a, 0, "файл"), ct),
                "replace-ref" => await ReplaceRefAsync(adapter, a, ct),
                "import-bench" => await ImportBench.RunAsync(adapter, Arg(a, 0, "головной файл"), a.Positional.Skip(1).ToList(), ct),
                "" => throw new ArgumentException("Не указана команда."),
                _ => throw new ArgumentException($"Неизвестная команда «{a.Command}»."),
            };
            return new CommandResult { ExitCode = 0, Output = output };
        }
        catch (ArgumentException ex)
        {
            return new CommandResult { ExitCode = 2, Error = ex.Message };
        }
        catch (CadException ex)
        {
            return new CommandResult { ExitCode = 1, Error = ex.Message };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new CommandResult { ExitCode = 1, Error = ex.Message };
        }
    }

    private static string Arg(ProbeArgs a, int index, string what) =>
        index < a.Positional.Count ? a.Positional[index] : throw new ArgumentException($"Команде «{a.Command}» нужен аргумент: {what}.");

    private static Task<object> InfoAsync(SolidWorksAdapter adapter, CancellationToken ct) => adapter.RunAsync<object>(s =>
    {
        string dynamicCheck;
        try
        {
            dynamic app = s.App.Raw;
            string rev = app.RevisionNumber();
            dynamicCheck = "ok: " + rev;
        }
        catch (Exception ex)
        {
            dynamicCheck = "ошибка: " + ex.GetType().Name + ": " + ex.Message;
        }

        object? searchFolders = null;
        try { searchFolders = s.App.CallString("GetSearchFolders", 0); }
        catch (CadException ex) { searchFolders = "ошибка: " + ex.Message; }

        return new
        {
            revision = s.Revision,
            revisionYear = s.RevisionYear,
            executablePath = Safe(() => s.App.CallString("GetExecutablePath")),
            processId = Safe(() => s.App.CallInt("GetProcessID")),
            templates = new
            {
                part = Safe(() => s.App.CallString("GetUserPreferenceStringValue", SwConst.PrefDefaultTemplatePart)),
                assembly = Safe(() => s.App.CallString("GetUserPreferenceStringValue", SwConst.PrefDefaultTemplateAssembly)),
                drawing = Safe(() => s.App.CallString("GetUserPreferenceStringValue", SwConst.PrefDefaultTemplateDrawing)),
            },
            searchFolders,
            dynamicCheck,
            probe = new { pid = Environment.ProcessId, sessionId = Process.GetCurrentProcess().SessionId, user = Environment.UserName, Environment.CurrentDirectory },
            openDocuments = s.GetOpenDocuments().Select(d => new { d.Path, d.Title, d.Type, d.Visible }).ToList(),
        };
    }, ct);

    private static object? Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch (CadException ex) { return "ошибка: " + ex.Message; }
    }

    private static Task<object> DepsAsync(SolidWorksAdapter adapter, string path, bool traverse, bool search, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        return adapter.RunAsync<object>(s =>
        {
            var sw = Stopwatch.StartNew();
            var raw = ComObj.AsStrings(s.App.Call("GetDocumentDependencies2", full, traverse, search, false));
            var pairs = new List<object>();
            for (var i = 0; i + 1 < raw.Count; i += 2) pairs.Add(new { name = raw[i], path = raw[i + 1], exists = File.Exists(raw[i + 1]) });
            return new { path = full, traverse, search, ms = sw.Elapsed.TotalMilliseconds, count = pairs.Count, pairs };
        }, ct);
    }

    private static async Task<object> ScanAsync(SolidWorksAdapter adapter, string folder, bool recursive, CancellationToken ct)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new ArgumentException($"Папка «{root}» не найдена.");
        var files = Directory.EnumerateFiles(root, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => adapter.IsDocument(f) && !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wall = Stopwatch.StartNew();
        var items = new List<object>();
        var byVersion = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byKind = new Dictionary<string, (int Count, double Ms)>();
        var errors = new List<object>();
        double sumMs = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file);
            Progress?.Invoke($"scan {items.Count + 1}/{files.Count}: {rel}");
            var kind = adapter.KindOf(file).ToString();
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await adapter.ReadDetailedAsync(file, ct);
                var ms = sw.Elapsed.TotalMilliseconds;
                sumMs += ms;
                if (r.PropertiesSkipped != null) skipped++;
                var v = r.Info.CadVersion ?? "не определена";
                byVersion[v] = byVersion.TryGetValue(v, out var n) ? n + 1 : 1;
                var k = byKind.TryGetValue(kind, out var kk) ? kk : (0, 0);
                byKind[kind] = (k.Count + 1, k.Ms + ms);
                items.Add(new
                {
                    file = rel,
                    kind,
                    cadVersion = r.Info.CadVersion,
                    versionHistory = r.VersionHistory,
                    ms = Math.Round(ms, 1),
                    depsMs = Math.Round(r.DependenciesMs, 1),
                    openMs = Math.Round(r.OpenMs, 1),
                    propsMs = Math.Round(r.PropertiesMs, 1),
                    r.OpenMode,
                    r.PropertiesSkipped,
                    configurations = r.Info.Configurations,
                    references = r.Info.References.Select(x => x.SavedPath).ToList(),
                    virtualSkipped = r.VirtualSkipped.Count,
                    properties = r.Info.Properties,
                });
            }
            catch (CadException ex)
            {
                sumMs += sw.Elapsed.TotalMilliseconds;
                errors.Add(new { file = rel, error = ex.Message });
                items.Add(new { file = rel, kind, error = ex.Message, ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
            }
        }

        return new
        {
            folder = root,
            recursive,
            asmMode = adapter.Options.AssemblyReadMode.ToString(),
            summary = new
            {
                files = files.Count,
                ok = files.Count - errors.Count,
                propertiesSkipped = skipped,
                errors = errors.Count,
                byVersion,
                byKind = byKind.ToDictionary(p => p.Key, p => new { count = p.Value.Count, avgMs = Math.Round(p.Value.Ms / p.Value.Count, 1) }),
                totalSeconds = Math.Round(wall.Elapsed.TotalSeconds, 1),
                avgMs = files.Count == 0 ? 0 : Math.Round(sumMs / files.Count, 1),
            },
            errors,
            files = items,
        };
    }

    private static Task<object> CloseAsync(SolidWorksAdapter adapter, string path, CancellationToken ct)
    {
        var full = Path.GetFullPath(path);
        return adapter.RunAsync<object>(s =>
        {
            var doc = s.FindOpenByPath(full);
            if (doc == null) return new { path = full, wasOpen = false, closed = false };
            s.CloseDoc(full, doc.Title);
            return new { path = full, wasOpen = true, closed = s.FindOpenByPath(full) == null };
        }, ct);
    }

    private static async Task<object> ReplaceRefAsync(SolidWorksAdapter adapter, ProbeArgs a, CancellationToken ct)
    {
        var doc = WriteGuard.Check(Arg(a, 0, "документ"), a);
        var oldRef = Arg(a, 1, "старая ссылка");
        var newRef = Arg(a, 2, "новая ссылка");
        var before = await adapter.ReadDetailedAsync(doc, ct);
        await adapter.ReplaceReferencesAsync(doc, new Dictionary<string, string> { [oldRef] = newRef }, ct);
        var after = await adapter.ReadDetailedAsync(doc, ct);
        return new
        {
            document = doc,
            oldRef,
            newRef,
            before = before.Info.References.Select(r => r.SavedPath).ToList(),
            after = after.Info.References.Select(r => r.SavedPath).ToList(),
        };
    }
}

/// <summary>Запись разрешена только в своих папках прототипа; сетевой диск S: — никогда.</summary>
internal static class WriteGuard
{
    /// <summary>Папка прототипа: %LOCALAPPDATA%\Temp\solidbrew-probe (не %TEMP%: в сеансе RDP это бывает Temp\2).</summary>
    public static readonly string ProbeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "solidbrew-probe");

    public static string Check(string path, ProbeArgs a)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Запись в «{full}» запрещена: сетевые папки и диск S: только для чтения.");
        var roots = new List<string> { ProbeRoot };
        roots.AddRange(a.AllowWrite);
        foreach (var root in roots)
        {
            if (SwSession.IsInside(full, root)) return full;
        }
        throw new ArgumentException($"Запись в «{full}» запрещена. Разрешены только папки: {string.Join(", ", roots)} (добавить — ключ --allow-write).");
    }
}
