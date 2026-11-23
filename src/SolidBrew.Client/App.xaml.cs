using System.Windows;
using System.Windows.Threading;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Client.ViewModels;
using SolidBrew.Client.Views;

namespace SolidBrew.Client;

/// <summary>Точка входа: вход → главное окно; «Выйти» возвращает ко входу.</summary>
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
            var session = login.Session!;
            var main = new MainViewModel(session, login.Notifications);
            var window = new MainWindow { DataContext = main };
            var done = new TaskCompletionSource<bool>();
            main.LogoutRequested += (_, _) =>
            {
                done.TrySetResult(true);
                window.Close();
            };
            window.Closed += (_, _) => done.TrySetResult(false);
            MainWindow = window;
            window.Show();
            await main.InitializeAsync();
            if (!await done.Task)
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
}
