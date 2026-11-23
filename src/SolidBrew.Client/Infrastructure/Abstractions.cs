using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Api;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Окна сообщений, вопросов и выбора файлов. Подменяется в тестах.</summary>
public interface IDialogService
{
    void ShowInfo(string title, string message, IEnumerable<string>? details = null);

    void ShowError(string title, ErrorInfo error);

    /// <summary>Вопрос «да/нет». Возвращает true, если нажата первая кнопка.</summary>
    bool Confirm(string title, string message, string yesText = "Да", string noText = "Отмена", IEnumerable<string>? details = null);

    /// <summary>Выбор одного из вариантов. Возвращает номер варианта или -1 (окно закрыто).</summary>
    int Choose(string title, string message, IReadOnlyList<string> options, IEnumerable<string>? details = null);

    string? PickFile(string title, string filter, string? initialFolder = null);

    string? PickFolder(string title, string? initialFolder = null);

    string? SaveFile(string title, string filter, string defaultFileName, string? initialFolder = null);

    /// <summary>Показать модальное окно экрана (сдача, постановка на учёт, открытие…).</summary>
    bool? ShowDialog(DialogViewModel viewModel);
}

/// <summary>Открытие файлов и папок средствами Windows.</summary>
public interface IShellService
{
    void OpenFile(string path);

    void ShowInExplorer(string path);
}

/// <summary>Хранилище профиля клиента.</summary>
public interface IProfileStore
{
    ClientProfile Load();

    void Save(ClientProfile profile);
}

/// <summary>Создание клиента API и службы рабочих папок.</summary>
public interface IClientFactory
{
    IPdmApi CreateApi(string serverUrl, string? token);

    WorkspaceService CreateWorkspace(IPdmApi api, ClientProfile profile);
}

public sealed class FileProfileStore : IProfileStore
{
    public ClientProfile Load() => ClientProfileStore.Load();

    public void Save(ClientProfile profile) => ClientProfileStore.Save(profile);
}

/// <summary>Общие службы приложения.</summary>
public sealed class AppServices
{
    public AppServices(IDialogService dialogs, IShellService shell, IProfileStore profiles, IClientFactory factory)
    {
        Dialogs = dialogs;
        Shell = shell;
        Profiles = profiles;
        Factory = factory;
    }

    public IDialogService Dialogs { get; }
    public IShellService Shell { get; }
    public IProfileStore Profiles { get; }
    public IClientFactory Factory { get; }
}
