namespace SolidBrew.Client.Infrastructure;

/// <summary>
/// Наблюдение за рабочей папкой: сохранили файл в CAD, создали или удалили — через паузу (CAD пишет файл в несколько
/// приёмов) вызывается <see cref="Changed"/>. Служебная папка <c>.solidbrew</c> и временные файлы CAD (<c>~$…</c>,
/// <c>*.tmp</c>) не учитываются. Событие приходит в контексте синхронизации, в котором создан наблюдатель (поток окна).
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly TimeSpan _delay;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private string? _root;

    public WorkspaceWatcher(TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(1200);
    }

    /// <summary>В рабочей папке изменились файлы.</summary>
    public event EventHandler? Changed;

    /// <summary>Наблюдаемая папка; null — не наблюдать.</summary>
    public string? Root
    {
        get => _root;
        set
        {
            if (string.Equals(_root, value, StringComparison.OrdinalIgnoreCase)) return;
            _root = value;
            Restart();
        }
    }

    private void Restart()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root)) return;
            try
            {
                var w = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.Attributes,
                    InternalBufferSize = 64 * 1024,
                };
                w.Changed += OnEvent;
                w.Created += OnEvent;
                w.Deleted += OnEvent;
                w.Renamed += OnEvent;
                w.Error += (_, _) => Schedule(); // буфер переполнен — просто обновиться
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _watcher = null; // без наблюдения — обновление по F5 и после операций
            }
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath) && (e is not RenamedEventArgs r || IsIgnored(r.OldFullPath))) return;
        Schedule();
    }

    /// <summary>Служебные и временные файлы не считаются изменением документов.</summary>
    internal static bool IsIgnored(string path)
    {
        if (path.Contains(Path.DirectorySeparatorChar + SolidBrew.Workspace.WorkspaceState.FolderName, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(path);
        return name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith(".~", StringComparison.Ordinal)
               || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    }

    private void Schedule()
    {
        lock (_sync)
        {
            _timer ??= new Timer(_ => Fire());
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        if (_context != null) _context.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
