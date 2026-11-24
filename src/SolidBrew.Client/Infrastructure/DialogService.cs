using System.Windows;
using Microsoft.Win32;
using SolidBrew.Client.ViewModels;
using SolidBrew.Client.Views;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Окна WPF для <see cref="IDialogService"/>.</summary>
public sealed class DialogService : IDialogService
{
    private static Window? Owner()
    {
        var app = Application.Current;
        if (app == null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? (app.MainWindow is { IsVisible: true } m ? m : null);
    }

    public void ShowInfo(string title, string message, IEnumerable<string>? details = null) =>
        MessageWindow.Show(Owner(), title, message, details, MessageKind.Info, new[] { "OK" });

    public void ShowError(string title, ErrorInfo error) =>
        MessageWindow.Show(Owner(), title, error.Message, error.Details, MessageKind.Error, new[] { "OK" });

    public bool Confirm(string title, string message, string yesText = "Да", string noText = "Отмена", IEnumerable<string>? details = null) =>
        MessageWindow.Show(Owner(), title, message, details, MessageKind.Question, new[] { yesText, noText }) == 0;

    public int Choose(string title, string message, IReadOnlyList<string> options, IEnumerable<string>? details = null) =>
        MessageWindow.Show(Owner(), title, message, details, MessageKind.Warning, options);

    public string? PickFile(string title, string filter, string? initialFolder = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (initialFolder != null && Directory.Exists(initialFolder)) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(Owner()) == true ? dlg.FileName : null;
    }

    public string? PickFolder(string title, string? initialFolder = null)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (initialFolder != null && Directory.Exists(initialFolder)) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(Owner()) == true ? dlg.FolderName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialFolder = null)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = false };
        if (initialFolder != null && Directory.Exists(initialFolder)) dlg.InitialDirectory = initialFolder;
        return dlg.ShowDialog(Owner()) == true ? dlg.FileName : null;
    }

    public bool? ShowDialog(DialogViewModel viewModel)
    {
        Window window = viewModel switch
        {
            LoginViewModel => new LoginWindow(),
            SettingsViewModel => new SettingsWindow(),
            CheckinViewModel => new CheckinWindow(),
            ImportViewModel => new ImportWindow(),
            OpenViewModel => new OpenWindow(),
            ExportViewModel => new ExportWindow(),
            RenameViewModel => new RenameWindow(),
            StartViewModel => new StartWindow(),
            AdminViewModel => new AdminWindow(),
            _ => throw new NotSupportedException($"Нет окна для {viewModel.GetType().Name}."),
        };
        window.DataContext = viewModel;
        window.Title = viewModel.Title;
        var owner = Owner();
        if (owner != null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        var closingByVm = false;
        void OnClose(object? sender, bool? result)
        {
            closingByVm = true;
            try
            {
                window.DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                window.Close();
            }
        }

        viewModel.CloseRequested += OnClose;
        window.Loaded += async (_, _) => await viewModel.InitializeAsync();
        window.Closing += (_, e) =>
        {
            if (viewModel.CanClose) return;
            e.Cancel = true;
            closingByVm = false;
            if (Confirm(viewModel.Title, "Операция ещё выполняется. Прервать её?", "Прервать", "Продолжить"))
                viewModel.Busy.CancelCommand.Execute(null);
        };
        window.Closed += (_, _) =>
        {
            viewModel.CloseRequested -= OnClose;
            viewModel.OnClosed();
        };
        var r = window.ShowDialog();
        return closingByVm ? viewModel.DialogResult : r;
    }
}
