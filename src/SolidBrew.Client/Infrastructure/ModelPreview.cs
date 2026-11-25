using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SolidBrew.Client.Infrastructure;

/// <summary>
/// Элемент просмотра eDrawings (ActiveX, не привязанный к версии), который ставится вместе с SOLIDWORKS
/// или как бесплатный eDrawings Viewer. Вызовы — поздним связыванием, сборки eDrawings программе не нужны.
/// </summary>
public sealed class EDrawingsAxHost : System.Windows.Forms.AxHost
{
    public const string ClsId = "22945a69-1191-4dcf-9e6f-409bde94d101";

    private ConnectionPointCookie? _cookie;

    /// <summary>Почему не подключились события (диагностика); null — подключились или ещё не пробовали.</summary>
    public static string? SinkError { get; private set; }

    /// <summary>События подключены.</summary>
    public bool HasEvents => _cookie != null;

    public EDrawingsAxHost() : base(ClsId)
    {
    }

    public object? Control => GetOcx();

    /// <summary>Документ догружен (OpenDoc возвращается сразу, загрузка идёт дальше).</summary>
    public event Action<string>? FinishedLoading;

    /// <summary>Документ не открылся: файл, код, текст ошибки eDrawings.</summary>
    public event Action<string, int, string>? FailedLoading;

    protected override void CreateSink()
    {
        try
        {
            if (Control is { } ocx) _cookie = new ConnectionPointCookie(ocx, new EModelViewEventSink(this), typeof(IEModelViewEvents));
        }
        catch (Exception ex) when (ex is InvalidCastException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _cookie = null; // без событий просмотр работает, только без автоматической изометрии
            SinkError = ex.Message;
        }
    }

    protected override void DetachSink()
    {
        _cookie?.Disconnect();
        _cookie = null;
    }

    internal void RaiseFinished(string fileName) => FinishedLoading?.Invoke(fileName);

    internal void RaiseFailed(string fileName, int errorCode, string errorString) => FailedLoading?.Invoke(fileName, errorCode, errorString);

    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{ClsId}}}\InprocServer32");
                return key != null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void Call(string method, params object[] args) =>
        Control?.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, Control, args);

    public void Set(string property, object value) =>
        Control?.GetType().InvokeMember(property, BindingFlags.SetProperty, null, Control, new[] { value });
}

/// <summary>
/// Журнал просмотра: <c>%LOCALAPPDATA%\SolidBrew\logs\preview.log</c>, каждая строка сразу на диск —
/// чтобы после аварийного завершения в элементе eDrawings было видно последний шаг.
/// </summary>
internal static class PreviewLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidBrew", "logs", "preview.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000) File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Исходящий интерфейс событий элемента eDrawings (_IEModelViewControlEvents); нужны только события загрузки.
/// Публичный и видимый для COM — иначе элемент не может подписать приёмник.
/// </summary>
[System.Runtime.InteropServices.ComVisible(true)]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIDispatch)]
[System.Runtime.InteropServices.Guid("faf8ba5f-b123-4a1d-bf3a-237038bb35e7")]
public interface IEModelViewEvents
{
    [System.Runtime.InteropServices.DispId(3)]
    void OnFinishedLoadingDocument(string fileName);

    [System.Runtime.InteropServices.DispId(5)]
    void OnFailedLoadingDocument(string fileName, int errorCode, string errorString);
}

/// <summary>Приёмник событий eDrawings (передаёт их в <see cref="EDrawingsAxHost"/>).</summary>
[System.Runtime.InteropServices.ComVisible(true)]
[System.Runtime.InteropServices.ClassInterface(System.Runtime.InteropServices.ClassInterfaceType.None)]
public sealed class EModelViewEventSink : IEModelViewEvents
{
    private readonly EDrawingsAxHost _owner;

    internal EModelViewEventSink(EDrawingsAxHost owner) => _owner = owner;

    public void OnFinishedLoadingDocument(string fileName) => _owner.RaiseFinished(fileName);

    public void OnFailedLoadingDocument(string fileName, int errorCode, string errorString) => _owner.RaiseFailed(fileName, errorCode, errorString);
}

/// <summary>
/// Просмотр модели или чертежа в eDrawings: вращение, масштаб, сечения — средствами eDrawings.
/// Файл открывается только для чтения — так eDrawings его не блокирует (проверено на eDrawings 2026: запись и смена
/// атрибутов при открытом просмотре проходят). На время операций (<see cref="Suspended"/>) документ всё равно закрывается,
/// а после открывается заново — чтобы показать новое содержимое.
/// </summary>
public sealed class ModelPreview : UserControl
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sldprt", ".sldasm", ".slddrw", ".eprt", ".easm", ".edrw", ".edw", ".dxf", ".dwg",
    };

    public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
        nameof(FilePath), typeof(string), typeof(ModelPreview), new PropertyMetadata(null, (d, _) => ((ModelPreview)d).Schedule()));

    public static readonly DependencyProperty SuspendedProperty = DependencyProperty.Register(
        nameof(Suspended), typeof(bool), typeof(ModelPreview), new PropertyMetadata(false, (d, _) => ((ModelPreview)d).Schedule(immediateRelease: true)));

    /// <summary>Текст, когда файла нет (например, документа нет в рабочей папке).</summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(ModelPreview), new PropertyMetadata("Нет файла для просмотра.", (d, _) => ((ModelPreview)d).Schedule()));

    private readonly Grid _surface = new();
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16),
    };
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private WindowsFormsHost? _host;
    private EDrawingsAxHost? _ax;
    private Window? _window;
    private string? _shown;
    private DateTime _guardUntil;
    private WindowState _stateBeforeOpen = WindowState.Normal;
    private int _waitForControl;

    public ModelPreview()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Apply();
        };
        foreach (var (text, orientation, tip) in new[]
                 {
                     ("Изометрия", 6, "Изометрический вид"), ("Спереди", 0, "Вид спереди"), ("Сверху", 2, "Вид сверху"),
                     ("Справа", 5, "Вид справа"), ("Вписать", 7, "Вписать в окно"),
                 })
        {
            var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0) };
            button.Click += (_, _) => Orient(orientation);
            _toolbar.Children.Add(button);
        }
        _toolbar.Children.Add(new TextBlock
        {
            Text = "Мышь: левая — вращать, колесо — масштаб, средняя — сдвиг",
            Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12,
        });
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _surface.Children.Add(_message);

        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_toolbar);
        root.Children.Add(new Border { BorderThickness = new Thickness(1), Child = _surface, BorderBrush = Brushes.Gainsboro });
        Content = root;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => Schedule(immediateRelease: !IsVisible);
    }

    /// <summary>Полный путь показываемого файла; null — показать <see cref="EmptyText"/>.</summary>
    public string? FilePath
    {
        get => (string?)GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    /// <summary>Идёт операция над файлами — отпустить файл, после — показать снова.</summary>
    public bool Suspended
    {
        get => (bool)GetValue(SuspendedProperty);
        set => SetValue(SuspendedProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>Отпустить файл сейчас (перед «Открыть в CAD» и т. п.); снова откроется при следующем показе.</summary>
    public void Release()
    {
        if (_shown == null || _ax == null) return;
        try
        {
            PreviewLog.Write("CloseActiveDoc " + _shown);
            _ax.Call("CloseActiveDoc", string.Empty);
            PreviewLog.Write("CloseActiveDoc ok");
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
        _shown = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += OnWindowStateChanged;
        Schedule();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.StateChanged -= OnWindowStateChanged;
        _debounce.Stop();
        Release();
    }

    /// <summary>eDrawings при загрузке иногда сворачивает окно-владельца — вернуть, как было.</summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        PreviewLog.Write("окно: " + _window?.WindowState);
        if (_window == null || _window.WindowState != WindowState.Minimized) return;
        if (DateTime.UtcNow > _guardUntil || _stateBeforeOpen == WindowState.Minimized) return;
        RestoreWindow();
    }

    private void RestoreWindow()
    {
        var restore = _stateBeforeOpen;
        Dispatcher.BeginInvoke(() =>
        {
            if (_window?.WindowState == WindowState.Minimized) _window.WindowState = restore;
        }, DispatcherPriority.Background);
    }

    /// <summary>Начало работы с документом eDrawings: окно свёрнуто не пользователем, если это случится в ближайшие секунды.</summary>
    private void GuardWindowState()
    {
        if (_window != null && _window.WindowState != WindowState.Minimized) _stateBeforeOpen = _window.WindowState;
        _guardUntil = DateTime.UtcNow.AddSeconds(8);
    }

    private void Schedule(bool immediateRelease = false)
    {
        if (immediateRelease && Desired() == null)
        {
            _debounce.Stop();
            Apply();
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private string? Desired() => !Suspended && IsVisible && IsLoaded ? FilePath : null;

    private void Apply()
    {
        var path = FilePath;
        var desired = Desired();
        PreviewLog.Write($"Apply: путь={path ?? "-"} показать={desired ?? "-"} сейчас={_shown ?? "-"} visible={IsVisible} suspended={Suspended}");
        if (desired == null)
        {
            Release();
            if (path == null) ShowMessage(EmptyText);
            else if (Suspended) ShowMessage(_host != null ? null : "Просмотр появится после операции.", keepHost: _host != null);
            return;
        }
        if (!Supported.Contains(Path.GetExtension(desired)))
        {
            Release();
            ShowMessage($"Просмотр файлов «{Path.GetExtension(desired)}» не поддерживается.");
            return;
        }
        if (!EDrawingsAxHost.IsInstalled)
        {
            ShowMessage("Для просмотра нужен eDrawings: он ставится вместе с SOLIDWORKS, или установите бесплатный eDrawings Viewer.");
            return;
        }
        if (!File.Exists(desired))
        {
            Release();
            ShowMessage($"Файла нет: {desired}");
            return;
        }
        if (string.Equals(_shown, desired, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            EnsureHost();
            PreviewLog.Write("элемент: " + (_ax!.Control == null ? "ещё не создан" : "есть"));
            if (_ax!.Control == null)
            {
                // Элемент ActiveX создаётся, когда окно-хозяин получит дескриптор, — повторить чуть позже.
                ShowHost();
                if (++_waitForControl <= 40) Dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);
                else ShowMessage("Не удалось запустить eDrawings в окне просмотра.");
                return;
            }
            _waitForControl = 0;
            GuardWindowState();
            Release();
            PreviewLog.Write("OpenDoc " + desired);
            _ax!.Call("OpenDoc", desired, false, false, true, string.Empty);
            PreviewLog.Write("OpenDoc вернулся");
            _shown = desired;
            ShowHost();
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _shown = null;
            ShowMessage("eDrawings не открыл файл: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    /// <summary>Догружен: модель — в изометрию и вписать; чертёж — вписать лист.</summary>
    private void OnLoadedDocument(string file)
    {
        PreviewLog.Write("загружен " + file);
        if (!string.Equals(file, _shown, StringComparison.OrdinalIgnoreCase)) return;
        if (_window?.WindowState == WindowState.Minimized && _stateBeforeOpen != WindowState.Minimized) RestoreWindow();
        // Загрузка закончилась: дальше сворачивание — уже решение пользователя.
        _guardUntil = DateTime.UtcNow.AddSeconds(1);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".sldprt" or ".sldasm" or ".eprt" or ".easm") Orient(6);
        Orient(7);
        // Поворот анимируется: вписать ещё раз, когда он закончится.
        var shown = _shown;
        var again = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        again.Tick += (_, _) =>
        {
            again.Stop();
            if (string.Equals(shown, _shown, StringComparison.OrdinalIgnoreCase)) Orient(7);
        };
        again.Start();
    }

    private void Orient(int orientation)
    {
        if (_shown == null || _ax == null) return;
        try
        {
            PreviewLog.Write("ViewOrientation " + orientation);
            _ax.Set("ViewOrientation", orientation);
        }
        catch (Exception ex) when (ex is TargetInvocationException or System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void EnsureHost()
    {
        if (_host != null) return;
        PreviewLog.Write("создаю элемент eDrawings");
        _ax = new EDrawingsAxHost { Dock = System.Windows.Forms.DockStyle.Fill };
        _ax.FinishedLoading += file => Dispatcher.BeginInvoke(() => OnLoadedDocument(file));
        _ax.FailedLoading += (file, _, error) => Dispatcher.BeginInvoke(() =>
        {
            if (!string.Equals(file, _shown, StringComparison.OrdinalIgnoreCase)) return;
            _shown = null;
            ShowMessage("eDrawings не открыл файл: " + (string.IsNullOrWhiteSpace(error) ? file : error));
        });
        _host = new WindowsFormsHost { Child = _ax };
        _surface.Children.Add(_host);
    }

    private void ShowHost()
    {
        _message.Visibility = Visibility.Collapsed;
        if (_host != null) _host.Visibility = Visibility.Visible;
        _toolbar.IsEnabled = true;
    }

    private void ShowMessage(string? text, bool keepHost = false)
    {
        _message.Text = text ?? string.Empty;
        _message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        // WindowsFormsHost рисуется поверх WPF — прячем его, чтобы было видно сообщение.
        if (_host != null && !keepHost) _host.Visibility = Visibility.Collapsed;
        _toolbar.IsEnabled = false;
    }
}
