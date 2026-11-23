using System.Text;
using System.Text.Json;
using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using SolidBrew.Core.Paths;
using SolidBrew.Workspace;

namespace SolidBrew.Cli;

/// <summary>
/// Командная строка SolidBrew PDM — тонкая оболочка над <see cref="WorkspaceService"/>.
/// Коды выхода: 0 — выполнено, 1 — ошибка, 2 — нужен ответ пользователя (повторить с --yes или решениями).
/// </summary>
public sealed partial class CliApp
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NeedsAnswer = 2;

    private static readonly string[] GlobalFlags = { "json", "verbose", "testcad", "help" };
    private static readonly string[] GlobalValues = { "dir" };

    private static readonly JsonSerializerOptions JsonOut = new(PdmJson.Options) { WriteIndented = true };

    private readonly CliEnvironment _env;
    private readonly List<IDisposable> _disposables = new();
    private readonly Dictionary<string, CommandSpec> _commands;
    private CliArgs _args = new();
    private CancellationToken _ct;
    private ClientProfile? _profile;
    private IPdmApi? _api;
    private WorkspaceService? _service;

    public CliApp(CliEnvironment environment)
    {
        _env = environment ?? throw new ArgumentNullException(nameof(environment));
        _commands = BuildCommands().ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CommandSpec(string Name, string Usage, string Summary, string[] Flags, string[] Values, Func<Task<int>> Run);

    private TextWriter Out => _env.Out;
    private TextWriter Err => _env.Err;
    private bool Json => _args.Flag("json");
    private bool Verbose => _args.Flag("verbose");

    /// <summary>Выполнить команду. Исключения не выходят наружу: ошибка печатается одной строкой (стек — с --verbose).</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        _ct = ct;
        var verbose = args.Contains("--verbose") || args.Contains("-v");
        var json = args.Contains("--json");
        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            return Fail(ex.Message, ex.ExitCode, ex, verbose, json);
        }
        catch (PdmApiException ex)
        {
            var message = ex.Message;
            if (ex.StatusCode == 401) message += " Выполните: sbpdm login";
            return Fail(message, Failed, ex, verbose, json);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            return Fail("Операция отменена.", Failed, ex, verbose, json);
        }
        catch (Exception ex) when (ex is WorkspaceException or CadException or IOException or UnauthorizedAccessException)
        {
            return Fail(ex.Message, Failed, ex, verbose, json);
        }
        catch (Exception ex)
        {
            return Fail($"Внутренняя ошибка sbpdm: {ex.Message} Повторите с --verbose и сообщите администратору.", Failed, ex, verbose, json);
        }
        finally
        {
            foreach (var d in _disposables)
            {
                try
                {
                    d.Dispose();
                }
                catch (Exception)
                {
                    // Освобождение адаптеров CAD не должно менять итог команды.
                }
            }
            _disposables.Clear();
        }
    }

    private int Fail(string message, int code, Exception ex, bool verbose, bool json)
    {
        Err.WriteLine((code == NeedsAnswer ? "" : "Ошибка: ") + message);
        if (verbose) Err.WriteLine(ex.ToString());
        if (json) Out.WriteLine(JsonSerializer.Serialize(new { error = message, exitCode = code }, JsonOut));
        return code;
    }

    private async Task<int> DispatchAsync(string[] args)
    {
        // Команда — первый токен, не являющийся параметром (глобальные параметры можно ставить до неё).
        var tokens = args.ToList();
        string? name = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t == "--dir") { i++; continue; }
            if (t.StartsWith('-')) continue;
            name = t;
            tokens.RemoveAt(i);
            break;
        }

        if (name == null)
        {
            if (tokens.Contains("--version"))
            {
                Out.WriteLine("sbpdm " + typeof(CliApp).Assembly.GetName().Version);
                return Ok;
            }
            _args = CliArgs.Parse(tokens, GlobalFlags, GlobalValues);
            PrintHelp(null);
            return _args.Flag("help") ? Ok : Failed;
        }

        if (string.Equals(name, "help", StringComparison.OrdinalIgnoreCase))
        {
            _args = CliArgs.Parse(tokens, GlobalFlags, GlobalValues);
            PrintHelp(_args.At(0));
            return Ok;
        }

        if (!_commands.TryGetValue(name, out var command))
            throw new CliException($"Неизвестная команда «{name}». Список команд: sbpdm help.");

        _args = CliArgs.Parse(tokens, command.Flags.Concat(GlobalFlags).ToHashSet(), command.Values.Concat(GlobalValues).ToHashSet());
        if (_args.Flag("help"))
        {
            PrintHelp(command.Name);
            return Ok;
        }
        return await command.Run().ConfigureAwait(false);
    }

    private void PrintHelp(string? command)
    {
        if (command != null && _commands.TryGetValue(command, out var c))
        {
            Out.WriteLine("Использование: sbpdm " + c.Usage);
            Out.WriteLine(c.Summary);
            Out.WriteLine();
            Out.WriteLine("Общие параметры: --dir <папка> (рабочая папка вместо текущей), --json (вывод в JSON), --verbose (подробности ошибок), --testcad (тестовый CAD).");
            return;
        }
        Out.WriteLine("sbpdm — командная строка SolidBrew PDM.");
        Out.WriteLine();
        Out.WriteLine("Команды:");
        foreach (var spec in _commands.Values)
            Out.WriteLine($"  {spec.Usage}");
        Out.WriteLine();
        Out.WriteLine("«Документ» — ID, обозначение или путь к файлу в рабочей папке. Рабочая папка — текущая или --dir.");
        Out.WriteLine("Общие параметры: --json, --verbose, --testcad, --dir <папка>. Подробно: sbpdm help <команда>.");
        Out.WriteLine("Коды выхода: 0 — выполнено, 1 — ошибка, 2 — нужен ответ (проверьте вывод и повторите с --yes или решениями).");
    }

    // ------------------------------------------------------------------ профиль, клиент, служба

    private ClientProfile Profile => _profile ??= ClientProfileStore.Load(_env.ProfilePath);

    private void SaveProfile() => ClientProfileStore.Save(Profile, _env.ProfilePath);

    /// <summary>Клиент API с сохранённым входом; без входа — ошибка с подсказкой.</summary>
    private IPdmApi Api
    {
        get
        {
            if (_api != null) return _api;
            if (!Profile.HasValidToken)
                throw new CliException($"Не выполнен вход в PDM ({Profile.ServerUrl}). Выполните: sbpdm login" +
                                       (Profile.Login != null ? $" --user {Profile.Login}" : string.Empty));
            _api = _env.ApiFactory(Profile.ServerUrl, Profile.Token);
            return _api;
        }
    }

    private WorkspaceService Service
    {
        get
        {
            if (_service != null) return _service;
            var registry = new CadAdapterRegistry();
            if (_env.UseSolidWorks)
            {
                var sw = new SolidWorksAdapter();
                _disposables.Add(sw);
                registry.Register(sw);
            }
            if (Profile.EnableTestCad || _args.Flag("testcad")) registry.Register(new TestCadAdapter());
            var cache = new LocalCache(Profile.CacheRoot);
            _service = new WorkspaceService(Api, registry, cache, new WorkspaceServiceOptions { ServerUrl = Profile.ServerUrl });
            return _service;
        }
    }

    private IProgress<WorkProgress> Progress => new ErrProgress(Err);

    /// <summary>Ход выполнения — в stderr, по строке на смену сообщения.</summary>
    private sealed class ErrProgress(TextWriter err) : IProgress<WorkProgress>
    {
        private string? _last;

        public void Report(WorkProgress value)
        {
            lock (this)
            {
                if (value.Message == _last || string.IsNullOrEmpty(value.Message)) return;
                _last = value.Message;
                err.WriteLine(value.Message);
            }
        }
    }

    // ------------------------------------------------------------------ папки и пути

    /// <summary>Папка из --dir или текущая.</summary>
    private string BaseDir => _args.Value("dir") is { } d ? Path.GetFullPath(d, _env.CurrentDirectory) : _env.CurrentDirectory;

    private string FullPath(string path) => Path.GetFullPath(path, _env.CurrentDirectory);

    /// <summary>Ближайшая рабочая папка SolidBrew, содержащая <paramref name="start"/>, или null.</summary>
    private static string? FindWorkspace(string start)
    {
        for (var d = Path.GetFullPath(start); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            if (WorkspaceService.IsWorkspace(d)) return d.TrimEnd('\\');
        }
        return null;
    }

    /// <summary>Рабочая папка для команд над файлами: --dir или текущая (или их родитель с состоянием SolidBrew).</summary>
    private string WorkRoot() => FindWorkspace(BaseDir) ?? throw new CliException(
        $"Папка «{BaseDir}» не является рабочей папкой SolidBrew. Откройте в неё комплект: sbpdm open <документ> --to <папка>, или укажите --dir.");

    /// <summary>Относительный путь файла в рабочей папке (путь — от текущей папки или от корня рабочей папки).</summary>
    private string RelOf(string root, string file)
    {
        var candidates = new[] { FullPath(file), Path.GetFullPath(file, root) };
        foreach (var full in candidates)
        {
            try
            {
                var rel = RelPath.GetRelative(root, full);
                if (File.Exists(full) || WorkspaceState.Load(root).FindByPath(rel) != null) return rel;
            }
            catch (ArgumentException)
            {
            }
        }
        try
        {
            return RelPath.GetRelative(root, candidates[0]);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    /// <summary>Документы рабочей папки по аргументам: путь к файлу (или ID, обозначение — если документ лежит в папке).</summary>
    private async Task<List<(Guid Id, string RelPath)>> WorkspaceDocsAsync(string root, IReadOnlyList<string> files)
    {
        if (files.Count == 0) throw new CliException("Укажите хотя бы один файл рабочей папки.");
        var state = WorkspaceState.Load(root);
        var result = new List<(Guid, string)>();
        foreach (var f in files)
        {
            WorkspaceEntry? entry = null;
            try
            {
                entry = state.FindByPath(RelOf(root, f));
            }
            catch (CliException)
            {
            }
            if (entry == null)
            {
                var doc = await ResolveDocumentAsync(f).ConfigureAwait(false);
                entry = state.FindByDocument(doc.Id) ?? throw new CliException(
                    $"Документа {Label(doc.RelPath, doc.Designation)} нет в рабочей папке «{root}». Сначала откройте его: sbpdm open {doc.Id}");
            }
            result.Add((entry.DocumentId, entry.RelPath));
        }
        return result;
    }

    /// <summary>
    /// Документ по ID, пути к файлу рабочей папки или обозначению / пути / имени файла (точное совпадение в поиске).
    /// </summary>
    private async Task<DocumentSummaryDto> ResolveDocumentAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new CliException("Не указан документ.");
        if (Guid.TryParse(text, out var id))
            return (await Api.GetDocumentAsync(id, _ct).ConfigureAwait(false)).Document;

        // Файл рабочей папки.
        string? full = null;
        try
        {
            full = FullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        if (full != null)
        {
            var root = FindWorkspace(Path.GetDirectoryName(full) ?? full) ?? FindWorkspace(BaseDir);
            if (root != null)
            {
                foreach (var candidate in new[] { full, Path.GetFullPath(text, root) })
                {
                    try
                    {
                        var entry = WorkspaceState.Load(root).FindByPath(RelPath.GetRelative(root, candidate));
                        if (entry != null) return (await Api.GetDocumentAsync(entry.DocumentId, _ct).ConfigureAwait(false)).Document;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }

        var found = await Api.SearchDocumentsAsync(q: text, includeUnused: true, limit: 200, ct: _ct).ConfigureAwait(false);
        var norm = text.Replace('\\', '/');
        var exact = found.Where(d => string.Equals(d.Designation, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) exact = found.Where(d => string.Equals(d.RelPath, norm, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0) exact = found.Where(d => string.Equals(d.FileName, text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 1 && FindWorkspace(BaseDir) is { } ws)
        {
            // В рабочей папке предпочитаем документы её проекта.
            var projectId = WorkspaceState.Load(ws).ProjectId;
            var inProject = exact.Where(d => d.ProjectId == projectId).ToList();
            if (inProject.Count > 0) exact = inProject;
        }
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1)
            throw new CliException($"«{text}» неоднозначно — подходят документы: " +
                                   string.Join("; ", exact.Take(10).Select(d => $"{Label(d.RelPath, d.Designation)} [{d.ProjectName}] ID {d.Id}")) + ". Укажите ID.");
        var hint = found.Count > 0
            ? " Похожие: " + string.Join("; ", found.Take(5).Select(d => $"{Label(d.RelPath, d.Designation)} ID {d.Id}")) + "."
            : string.Empty;
        throw new CliException($"Документ «{text}» не найден (ни ID, ни файл рабочей папки, ни точное обозначение, путь или имя файла).{hint}");
    }

    private async Task<ProjectDto> ResolveProjectAsync(string text)
    {
        var projects = await Api.GetProjectsAsync(_ct).ConfigureAwait(false);
        var p = Guid.TryParse(text, out var id)
            ? projects.FirstOrDefault(x => x.Id == id)
            : projects.FirstOrDefault(x => string.Equals(x.Name, text.Trim(), StringComparison.OrdinalIgnoreCase));
        return p ?? throw new CliException($"Проект «{text}» не найден. Проекты: " +
                                           (projects.Count == 0 ? "нет." : string.Join("; ", projects.Select(x => x.Name)) + "."));
    }

    private string Need(int index, string what) => _args.At(index) ?? throw new CliException($"Не указан {what}. Справка: sbpdm help.");

    // ------------------------------------------------------------------ вывод

    /// <summary>Вывести результат: JSON (--json) или текст.</summary>
    private void Emit(object data, Action text)
    {
        if (Json) Out.WriteLine(JsonSerializer.Serialize(data, data.GetType(), JsonOut));
        else text();
    }

    private void PrintProblems(IEnumerable<PlanProblem> problems)
    {
        foreach (var p in problems)
            Out.WriteLine("  " + p);
    }

    internal static string Label(string relPath, string? designation)
    {
        var name = relPath;
        return string.IsNullOrWhiteSpace(designation) ? $"«{name}»" : $"«{designation}» ({name})";
    }

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    internal static string KindText(DocumentKind kind) => kind switch
    {
        DocumentKind.Part => "деталь",
        DocumentKind.Assembly => "сборка",
        DocumentKind.Drawing => "чертёж",
        _ => "прочее",
    };

    internal static string LockText(LockDto? l) => l == null ? "свободен" : $"взят: {l.UserDisplayName} ({l.UserLogin}), с {Time(l.At)}";

    internal static string Short(string sha) => sha.Length > 12 ? sha.Substring(0, 12) : sha;

    /// <summary>Таблица с выравниванием колонок.</summary>
    internal void Table(IReadOnlyList<string> header, IEnumerable<string?[]> rows)
    {
        var all = new List<string[]> { header.ToArray() };
        all.AddRange(rows.Select(r => r.Select(c => c ?? string.Empty).ToArray()));
        var widths = new int[header.Count];
        foreach (var r in all)
        {
            for (var i = 0; i < widths.Length && i < r.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);
        }
        foreach (var r in all)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < widths.Length; i++)
            {
                var cell = i < r.Length ? r[i] : string.Empty;
                sb.Append(i == widths.Length - 1 ? cell : cell.PadRight(widths[i] + 2));
            }
            Out.WriteLine(sb.ToString().TrimEnd());
        }
    }
}
