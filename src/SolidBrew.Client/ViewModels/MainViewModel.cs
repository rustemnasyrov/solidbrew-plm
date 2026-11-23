using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client.ViewModels;

/// <summary>Главное окно: рабочая папка, вкладки «Обзор», «Комплект», «Рабочая папка», «Документ», «Администрирование».</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const int OverviewTab = 0;
    public const int KitTab = 1;
    public const int WorkFolderTab = 2;
    public const int DocumentTab = 3;
    public const int AdminTab = 4;

    private bool _changingRoot;

    public MainViewModel(AppSession session, IEnumerable<NotificationDto>? notifications = null)
    {
        Session = session;
        Busy.ErrorHandler = (title, error) => Dialogs.ShowError(title, error);
        Busy.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BusyState.IsBusy)) NotifyAll();
        };
        Flow = new WorkflowController(session, Busy);
        Flow.Changed += async (_, _) => await OnFlowChangedAsync();
        Flow.Notices += (_, items) => AddNotices(items);
        Flow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowController.WorkRoot))
            {
                OnPropertyChanged(nameof(WorkRootText));
                _changingRoot = true;
                SelectedWorkRoot = Flow.WorkRoot;
                _changingRoot = false;
            }
        };
        _selectedWorkRoot = Flow.WorkRoot;
        Browser = new BrowserViewModel(this);
        Kit = new KitViewModel(this);
        WorkFolder = new WorkFolderViewModel(this);
        Document = new DocumentViewModel(this);
        Admin = session.IsAdmin ? new AdminViewModel(this) : null;
        if (notifications != null)
        {
            _unseenNotifications = notifications.Any();
            foreach (var n in notifications) Notices.Add($"{Format.Time(n.At)}: {n.Text}");
        }
    }

    public AppSession Session { get; }
    public BusyState Busy { get; } = new();
    public WorkflowController Flow { get; }
    public BrowserViewModel Browser { get; }
    public KitViewModel Kit { get; }
    public WorkFolderViewModel WorkFolder { get; }
    public DocumentViewModel Document { get; }
    public AdminViewModel? Admin { get; }

    private IDialogService Dialogs => Session.Services.Dialogs;

    public string UserText => Session.UserText;
    public string ServerText => Session.Profile.ServerUrl;
    public bool IsAdmin => Session.IsAdmin;
    public bool CanModify => Session.CanModify;
    public bool IsReader => !Session.CanModify;
    public string Title => $"SolidBrew PDM — {UserText}";

    public string WorkRootText => Flow.WorkRoot ?? "не выбрана";

    /// <summary>Уведомления: принудительно снятые блокировки и т. п.</summary>
    public ObservableCollection<string> Notices { get; } = new();

    private bool _unseenNotifications;

    public bool HasNotices => Notices.Count > 0;

    [ObservableProperty]
    private int _selectedTab;

    /// <summary>Выбранная в списке рабочая папка (недавние).</summary>
    [ObservableProperty]
    private string? _selectedWorkRoot;

    /// <summary>Выход: окно закрывается, показывается вход.</summary>
    public event EventHandler? LogoutRequested;

    public async Task InitializeAsync()
    {
        Notices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotices));
        OnPropertyChanged(nameof(HasNotices));
        await Browser.LoadProjectsAsync();
        if (Flow.WorkRoot != null) await WorkFolder.RefreshAsync();
    }

    public void AddNotices(IEnumerable<string> items)
    {
        foreach (var i in items.Where(i => !Notices.Contains(i))) Notices.Add(i);
    }

    [RelayCommand]
    private async Task DismissNoticesAsync()
    {
        Notices.Clear();
        if (_unseenNotifications)
        {
            _unseenNotifications = false;
            await Busy.RunAsync("Уведомления", (_, ct) => Session.Api.MarkNotificationsSeenAsync(ct), reportErrors: false);
        }
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == AdminTab && Admin != null) _ = Admin.EnsureLoadedAsync();
        if (value == WorkFolderTab) _ = WorkFolder.RefreshAsync();
        if (value == KitTab && _kitStale && Kit.HasKit)
        {
            _kitStale = false;
            _ = Kit.RefreshAsync();
        }
        if (value == OverviewTab && _browserStale)
        {
            _browserStale = false;
            _ = Browser.SearchAsync();
        }
    }

    partial void OnSelectedWorkRootChanged(string? value)
    {
        if (_changingRoot || value == null || string.Equals(value, Flow.WorkRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(value))
        {
            Dialogs.ShowError("Рабочая папка", new ErrorInfo($"Папки «{value}» нет. Выберите другую."));
            _changingRoot = true;
            SelectedWorkRoot = Flow.WorkRoot;
            _changingRoot = false;
            return;
        }
        Flow.SetWorkRoot(value);
    }

    /// <summary>После команды: обновить текущую вкладку, остальные — при переходе на них.</summary>
    private async Task OnFlowChangedAsync()
    {
        OnPropertyChanged(nameof(WorkRootText));
        _kitStale = SelectedTab != KitTab;
        _browserStale = SelectedTab != OverviewTab;
        if (SelectedTab == WorkFolderTab) await WorkFolder.RefreshAsync();
        else if (SelectedTab == KitTab && Kit.HasKit) await Kit.RefreshAsync();
        else if (SelectedTab == OverviewTab) await Browser.SearchAsync();
    }

    private bool _kitStale;
    private bool _browserStale;

    public async Task ShowKitAsync(Guid documentId, int? version)
    {
        SelectedTab = KitTab;
        await Kit.LoadAsync(documentId, version);
    }

    public async Task ShowDocumentAsync(Guid documentId, int? version, DocumentSection section)
    {
        SelectedTab = DocumentTab;
        await Document.LoadAsync(documentId, version, section);
    }

    [RelayCommand]
    private void ChooseWorkRoot() => Flow.ChooseWorkRoot();

    [RelayCommand]
    private void ShowWorkRoot()
    {
        if (Flow.WorkRoot != null) Flow.ShowInExplorer(Flow.WorkRoot);
    }

    private bool CanRunModify() => Session.CanModify && !Busy.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunModify))]
    private Task ImportAsync() => Flow.ImportAsync(Browser.SelectedProject?.Project is { Kind: ProjectKind.Project } p ? p.Id : null);

    [RelayCommand(CanExecute = nameof(CanRunModify))]
    private Task CheckinAsync() => Flow.CheckinAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        switch (SelectedTab)
        {
            case KitTab:
                await Kit.RefreshAsync();
                break;
            case WorkFolderTab:
                await WorkFolder.RefreshAsync();
                break;
            case DocumentTab:
                await Document.RefreshCommand.ExecuteAsync(null);
                break;
            case AdminTab when Admin != null:
                await Admin.RefreshAllAsync();
                break;
            default:
                await Browser.LoadProjectsAsync();
                break;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var vm = new SettingsViewModel(Session.Services, Session.Profile);
        if (Dialogs.ShowDialog(vm) != true) return;
        if (vm.WorkspaceChanged) Session.RebuildWorkspace();
        if (vm.ServerChanged && Dialogs.Confirm("Адрес сервера изменён", "Чтобы подключиться к новому серверу, нужно войти заново. Выйти сейчас?", "Выйти", "Позже"))
            _ = LogoutAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (Busy.IsBusy && !Dialogs.Confirm("Выход", "Идёт операция. Отменить её и выйти?", "Выйти", "Остаться")) return;
        Busy.CancelCommand.Execute(null);
        try
        {
            await Session.Api.LogoutAsync();
        }
        catch (Core.Api.PdmApiException)
        {
        }
        Session.Profile.Token = null;
        Session.Profile.TokenExpiresAt = null;
        try
        {
            Session.SaveProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        LogoutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAll()
    {
        this.NotifyCommands();
        Browser.NotifyCommands();
        Kit.NotifyCommands();
        WorkFolder.NotifyCommands();
        Document.NotifyCommands();
        Admin?.NotifyCommands();
    }
}
