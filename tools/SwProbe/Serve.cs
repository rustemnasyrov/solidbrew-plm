using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolidBrew.Adapters.SolidWorks;

namespace SwProbe;

/// <summary>
/// Исполнитель заданий: берёт <c>&lt;jobs&gt;/inbox/*.json</c> (<c>{"id","command","args":[...]}</c>),
/// выполняет, пишет <c>&lt;jobs&gt;/outbox/&lt;id&gt;.json</c>, переносит задание в <c>done/</c>.
/// Раз в 5 с — <c>heartbeat.json</c>. Задание <c>stop</c> завершает работу.
/// </summary>
internal static class Serve
{
    private sealed class Job
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("command")] public string? Command { get; set; }
        [JsonPropertyName("args")] public List<string>? Args { get; set; }
    }

    private static readonly object LogLock = new object();
    private static string _logPath = string.Empty;

    public static async Task<int> RunAsync(ProbeArgs options)
    {
        if (string.IsNullOrWhiteSpace(options.Jobs))
        {
            Console.Error.WriteLine("Нужен ключ --jobs <папка>.");
            return 2;
        }
        var root = Path.GetFullPath(options.Jobs);
        var inbox = Directory.CreateDirectory(Path.Combine(root, "inbox")).FullName;
        var outbox = Directory.CreateDirectory(Path.Combine(root, "outbox")).FullName;
        var done = Directory.CreateDirectory(Path.Combine(root, "done")).FullName;
        _logPath = Path.Combine(root, "serve.log");
        var heartbeatPath = Path.Combine(root, "heartbeat.json");

        using var adapter = new SolidWorksAdapter();
        Commands.Progress = Log;
        var state = new State { StartedAt = DateTimeOffset.Now };
        Log($"Старт: pid {Environment.ProcessId}, сеанс {Process.GetCurrentProcess().SessionId}, папка {root}, программа {AppContext.BaseDirectory}");

        using var stopping = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try { WriteAtomic(heartbeatPath, JsonSerializer.Serialize(state.Snapshot(), Program.Json)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* следующий раз */ }
                try { await Task.Delay(5000, stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });

        var lastSwCheck = DateTime.MinValue;
        var stop = false;
        while (!stop)
        {
            if (DateTime.UtcNow - lastSwCheck > TimeSpan.FromSeconds(5))
            {
                lastSwCheck = DateTime.UtcNow;
                await CheckSolidWorksAsync(adapter, state).ConfigureAwait(false);
            }

            var files = SafeList(inbox);
            foreach (var file in files)
            {
                Job? job;
                try
                {
                    job = JsonSerializer.Deserialize<Job>(File.ReadAllText(file, Encoding.UTF8), Program.Json);
                }
                catch (IOException) { continue; } // ещё пишется — в следующий раз
                catch (JsonException ex)
                {
                    var badId = Path.GetFileNameWithoutExtension(file);
                    Log($"Задание {badId}: неверный JSON: {ex.Message}");
                    WriteResult(outbox, badId, 2, null, "Неверный JSON задания: " + ex.Message, DateTimeOffset.Now, 0);
                    MoveToDone(file, done);
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(job?.Id) ? Path.GetFileNameWithoutExtension(file) : job!.Id!;
                var command = (job?.Command ?? string.Empty).Trim().ToLowerInvariant();
                var args = job?.Args ?? new List<string>();
                var startedAt = DateTimeOffset.Now;
                var sw = Stopwatch.StartNew();
                state.Busy = $"{id}: {command}";
                state.BusySince = startedAt;
                Log($"Задание {id}: {command} {string.Join(" ", args.Select(Quote))}");

                CommandResult result;
                if (command == "stop")
                {
                    stop = true;
                    result = new CommandResult { ExitCode = 0, Output = new { stopped = true } };
                }
                else if (command == "serve")
                {
                    result = new CommandResult { ExitCode = 2, Error = "Команда serve в задании недопустима." };
                }
                else
                {
                    ProbeArgs parsed;
                    try
                    {
                        parsed = ProbeArgs.Parse(new[] { command }.Concat(args).ToList());
                        result = await Commands.ExecuteAsync(adapter, parsed, CancellationToken.None).ConfigureAwait(false);
                        if (result.ExitCode == 0 && parsed.Out != null)
                        {
                            var dir = Path.GetDirectoryName(Path.GetFullPath(parsed.Out));
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                            File.WriteAllText(parsed.Out, JsonSerializer.Serialize(result.Output, Program.Json), new UTF8Encoding(false));
                        }
                    }
                    catch (ArgumentException ex) { result = new CommandResult { ExitCode = 2, Error = ex.Message }; }
                    catch (Exception ex)
                    {
                        result = new CommandResult { ExitCode = 3, Error = $"Внутренняя ошибка: {ex}" };
                    }
                }

                var ms = sw.Elapsed.TotalMilliseconds;
                WriteResult(outbox, id, result.ExitCode, result.Output, result.Error, startedAt, ms);
                MoveToDone(file, done);
                state.Busy = null;
                state.BusySince = null;
                state.JobsDone++;
                Log($"Задание {id}: код {result.ExitCode}, {ms / 1000:0.0} с" + (result.Error != null ? $", ошибка: {result.Error}" : string.Empty));
                if (stop) break;
            }

            if (!stop) await Task.Delay(1500).ConfigureAwait(false);
        }

        state.Stopped = true;
        stopping.Cancel();
        await heartbeat.ConfigureAwait(false);
        try { WriteAtomic(heartbeatPath, JsonSerializer.Serialize(state.Snapshot(), Program.Json)); }
        catch (IOException) { }
        Log("Остановлен.");
        return 0;
    }

    private static async Task CheckSolidWorksAsync(SolidWorksAdapter adapter, State state)
    {
        try
        {
            var check = adapter.RunAsync(s => s.App.CallString("RevisionNumber") ?? string.Empty);
            if (await Task.WhenAny(check, Task.Delay(10000)).ConfigureAwait(false) != check)
            {
                state.SwError = "SOLIDWORKS не отвечает 10 с.";
                return;
            }
            state.SwRevision = await check.ConfigureAwait(false);
            state.SwError = null;
        }
        catch (Exception ex)
        {
            state.SwRevision = null;
            state.SwError = ex.Message;
        }
    }

    private static List<string> SafeList(string inbox)
    {
        try
        {
            return Directory.GetFiles(inbox, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (IOException) { return new List<string>(); }
    }

    private static void WriteResult(string outbox, string id, int exitCode, object? output, string? error, DateTimeOffset startedAt, double ms)
    {
        var json = JsonSerializer.Serialize(new { id, exitCode, output, error, startedAt, durationMs = Math.Round(ms) }, Program.Json);
        WriteAtomic(Path.Combine(outbox, SafeName(id) + ".json"), json);
    }

    private static void MoveToDone(string file, string done)
    {
        try { File.Move(file, Path.Combine(done, Path.GetFileName(file)), overwrite: true); }
        catch (IOException ex) { Log($"Не удалось перенести {file} в done: {ex.Message}"); }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeName(string id)
    {
        var chars = id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    private static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            try { File.AppendAllText(_logPath, line + Environment.NewLine, new UTF8Encoding(false)); }
            catch (IOException) { }
        }
    }

    private sealed class State
    {
        public DateTimeOffset StartedAt { get; set; }
        public string? SwRevision { get; set; }
        public string? SwError { get; set; }
        public string? Busy { get; set; }
        public DateTimeOffset? BusySince { get; set; }
        public int JobsDone { get; set; }
        public bool Stopped { get; set; }

        public object Snapshot() => new
        {
            time = DateTimeOffset.Now,
            pid = Environment.ProcessId,
            sessionId = Process.GetCurrentProcess().SessionId,
            startedAt = StartedAt,
            program = AppContext.BaseDirectory,
            swRevision = SwRevision,
            swError = SwError,
            busy = Busy,
            busySince = BusySince,
            jobsDone = JobsDone,
            stopped = Stopped,
        };
    }
}
