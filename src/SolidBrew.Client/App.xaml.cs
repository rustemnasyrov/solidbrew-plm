using System.Windows;
using System.Windows.Threading;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Client.ViewModels;
using SolidBrew.Client.Views;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client;

/// <summary>
/// Точка входа: вход → главное окно (все изделия в дереве, последнее открытое раскрыто) → «Выйти» возвращает ко входу.
/// Окон изделий может быть несколько («Открыть в новом окне»); приложение закрывается с последним.
/// </summary>
public partial class App : Application
{
    private ClientFactory? _factory;
    private DialogService? _dialogs;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        _factory = new ClientFactory();
        _dialogs = new DialogService();
        var services = new AppServices(_dialogs, new ShellService(), new FileProfileStore(), _factory);
        try
        {
            await RunAsync(services);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ErrorText.Describe(ex).ToString(), "SolidBrew PDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunAsync(AppServices services)
    {
        var profile = services.Profiles.Load();
        var tryResume = true;
        while (true)
        {
            var login = new LoginViewModel(services, profile);
            var resumed = tryResume && await login.TryResumeAsync();
            tryResume = false;
            if (!resumed && services.Dialogs.ShowDialog(login) != true)
            {
                Shutdown(0);
                return;
            }
            var windows = new ProductWindows(login.Session!);
            if (!await windows.RunAsync(login.Notifications))
            {
                Shutdown(0);
                return;
            }
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var error = ErrorText.Describe(e.Exception);
        if (_dialogs != null) _dialogs.ShowError("SolidBrew PDM", error);
        else MessageBox.Show(error.ToString(), "SolidBrew PDM", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _factory?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Окна изделий одного сеанса.</summary>
    private sealed class ProductWindows
    {
        private readonly AppSession _session;
        private readonly List<ProductWindow> _windows = new();
        private readonly TaskCompletionSource<bool> _done = new();
        private bool _logout;

        public ProductWindows(AppSession session) => _session = session;

        /// <summary>true — выход (показать вход снова), false — все окна закрыты.</summary>
        /// <summary>
        /// Сразу главное окно: в дереве все изделия, последнее открытое (если его папка на месте) — раскрыто.
        /// Отдельного окна выбора изделия нет.
        /// </summary>
        public async Task<bool> RunAsync(IEnumerable<NotificationDto>? notifications)
        {
            await OpenWindowAsync(ProductFolders.LastProduct(_session.Profile), notifications);
            return await _done.Task;
        }

        private async Task OpenWindowAsync(ProductRef? product, IEnumerable<NotificationDto>? notifications)
        {
            var vm = new ProductWindowViewModel(_session, NodeTabRegistry.CreateDefault(), notifications);
            var window = new ProductWindow { DataContext = vm };
            vm.NewWindowRequested += async (_, p) => await OpenWindowAsync(p, null);
            vm.LogoutRequested += (_, _) =>
            {
                _logout = true;
                foreach (var w in _windows.ToList()) w.Close();
                _done.TrySetResult(true);
            };
            window.Closed += (_, _) =>
            {
                vm.Dispose();
                _windows.Remove(window);
                if (_windows.Count == 0) _done.TrySetResult(_logout);
                else if (ReferenceEquals(Current.MainWindow, window)) Current.MainWindow = _windows[0];
            };
            _windows.Add(window);
            if (Current.MainWindow == null || !Current.MainWindow.IsVisible) Current.MainWindow = window;
            if (_windows.Count > 1)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                var prev = _windows[^2];
                window.Left = prev.Left + 40;
                window.Top = prev.Top + 40;
            }
            window.Show();
            await vm.StartAsync(product);
        }
    }
}
