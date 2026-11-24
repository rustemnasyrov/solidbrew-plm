using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Экран «Администрирование» (6.1 п. 8, 7.11): пользователи и роли, текущие блокировки с принудительным снятием,
/// проекты и библиотека, рабочие версии CAD, журнал.
/// </summary>
public sealed partial class AdminViewModel : DialogViewModel
{
    private static readonly (string CadId, string Name)[] KnownCads =
    {
        ("solidworks", "SOLIDWORKS"),
        ("kompas", "КОМПАС-3D"),
        ("kicad", "KiCad"),
        ("testcad", "Тестовый CAD"),
    };

    private bool _loaded;

    public AdminViewModel(AppSession session) : base(session.Services.Dialogs) => Session = session;

    private AppSession Session { get; }

    public override string Title => "Администрирование";

    public override Task InitializeAsync() => EnsureLoadedAsync();

    [RelayCommand]
    private void CloseWindow() => Close(true);

    public ObservableCollection<UserRow> Users { get; } = new();
    public ObservableCollection<LockRow> Locks { get; } = new();
    public ObservableCollection<ProjectDto> Projects { get; } = new();
    public ObservableCollection<SettingRow> Settings { get; } = new();
    public ObservableCollection<JournalRow> Journal { get; } = new();

    public static IReadOnlyList<RoleOption> Roles { get; } = new[]
    {
        new RoleOption(UserRole.Designer, "конструктор"),
        new RoleOption(UserRole.Reader, "читатель"),
        new RoleOption(UserRole.Admin, "администратор"),
    };

    public static IReadOnlyList<KindOption> ProjectKinds { get; } = new[]
    {
        new KindOption(ProjectKind.Project, "проект"),
        new KindOption(ProjectKind.Library, "библиотека"),
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChangeRoleCommand), nameof(SetPasswordCommand), nameof(ToggleActiveCommand))]
    private UserRow? _selectedUser;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceUnlockCommand))]
    private LockRow? _selectedLock;

    [ObservableProperty]
    private string _newLogin = string.Empty;

    [ObservableProperty]
    private string _newDisplayName = string.Empty;

    [ObservableProperty]
    private string _newPassword = string.Empty;

    [ObservableProperty]
    private RoleOption _newRole = Roles[0];

    [ObservableProperty]
    private RoleOption? _selectedUserRole;

    [ObservableProperty]
    private string _resetPassword = string.Empty;

    [ObservableProperty]
    private string _newProjectName = string.Empty;

    [ObservableProperty]
    private KindOption _newProjectKind = ProjectKinds[0];

    [ObservableProperty]
    private int _journalLimit = 500;

    partial void OnSelectedUserChanged(UserRow? value) => SelectedUserRole = value == null ? null : Roles.First(r => r.Role == value.User.Role);

    /// <summary>Загрузить при первом показе.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAllAsync();
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        await LoadUsersAsync();
        await LoadLocksAsync();
        await LoadProjectsAsync();
        await LoadSettingsAsync();
        await LoadJournalAsync();
    }

    // ------------------------------------------------------------------ пользователи

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        var outcome = await Busy.RunAsync("Пользователи", (_, ct) => Session.Api.GetUsersAsync(ct));
        if (!outcome.Ok) return;
        var keep = SelectedUser?.User.Id;
        Users.ReplaceWith(outcome.Value!.OrderBy(u => u.Login, StringComparer.OrdinalIgnoreCase).Select(u => new UserRow(u)));
        SelectedUser = Users.FirstOrDefault(u => u.User.Id == keep);
    }

    [RelayCommand]
    private async Task CreateUserAsync()
    {
        if (string.IsNullOrWhiteSpace(NewLogin) || string.IsNullOrWhiteSpace(NewPassword))
        {
            Dialogs.ShowError("Новый пользователь", new ErrorInfo("Укажите логин и пароль нового пользователя."));
            return;
        }
        var request = new CreateUserRequest
        {
            Login = NewLogin.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(NewDisplayName) ? NewLogin.Trim() : NewDisplayName.Trim(),
            Password = NewPassword,
            Role = NewRole.Role,
        };
        var outcome = await Busy.RunAsync("Создание пользователя", (_, ct) => Session.Api.CreateUserAsync(request, ct));
        if (!outcome.Ok) return;
        Busy.Done($"Пользователь «{outcome.Value!.Login}» создан.");
        NewLogin = NewDisplayName = NewPassword = string.Empty;
        await LoadUsersAsync();
    }

    private bool HasUser() => SelectedUser != null;

    [RelayCommand(CanExecute = nameof(HasUser))]
    private Task ChangeRoleAsync()
    {
        if (SelectedUserRole == null || SelectedUser == null) return Task.CompletedTask;
        return UpdateUserAsync(new UpdateUserRequest { Role = SelectedUserRole.Role }, $"Роль изменена: {SelectedUserRole.Text}.");
    }

    [RelayCommand(CanExecute = nameof(HasUser))]
    private Task SetPasswordAsync()
    {
        if (string.IsNullOrEmpty(ResetPassword))
        {
            Dialogs.ShowError("Пароль", new ErrorInfo("Введите новый пароль."));
            return Task.CompletedTask;
        }
        var pwd = ResetPassword;
        ResetPassword = string.Empty;
        return UpdateUserAsync(new UpdateUserRequest { Password = pwd }, "Пароль изменён.");
    }

    [RelayCommand(CanExecute = nameof(HasUser))]
    private Task ToggleActiveAsync()
    {
        var u = SelectedUser!.User;
        if (u.Id == Session.Me.Id && u.Active)
        {
            Dialogs.ShowError("Отключить", new ErrorInfo("Нельзя отключить собственную учётную запись."));
            return Task.CompletedTask;
        }
        if (u.Active && !Dialogs.Confirm("Отключить пользователя", $"Отключить «{u.Login}»? Он не сможет войти; его блокировки и история сохраняются.", "Отключить", "Отмена"))
            return Task.CompletedTask;
        return UpdateUserAsync(new UpdateUserRequest { Active = !u.Active }, u.Active ? "Пользователь отключён." : "Пользователь включён.");
    }

    private async Task UpdateUserAsync(UpdateUserRequest request, string done)
    {
        var id = SelectedUser!.User.Id;
        var outcome = await Busy.RunAsync("Изменение пользователя", (_, ct) => Session.Api.UpdateUserAsync(id, request, ct));
        if (!outcome.Ok) return;
        Busy.Done(done);
        await LoadUsersAsync();
    }

    // ------------------------------------------------------------------ блокировки

    [RelayCommand]
    private async Task LoadLocksAsync()
    {
        var api = Session.Api;
        var outcome = await Busy.RunAsync("Блокировки", async (_, ct) =>
        {
            var locks = await api.GetLocksAsync(ct);
            var rows = new List<LockRow>();
            foreach (var l in locks)
            {
                DocumentSummaryDto? doc = null;
                try
                {
                    doc = (await api.GetDocumentAsync(l.DocumentId, ct)).Document;
                }
                catch (Core.Api.PdmApiException ex) when (!ex.IsNetworkError)
                {
                }
                rows.Add(new LockRow(l, doc));
            }
            return rows;
        });
        if (!outcome.Ok) return;
        Locks.ReplaceWith(outcome.Value!.OrderBy(l => l.Lock.At));
    }

    [RelayCommand(CanExecute = nameof(HasLock))]
    private async Task ForceUnlockAsync()
    {
        var row = SelectedLock!;
        if (!Dialogs.Confirm("Снять блокировку",
                $"Принудительно снять блокировку {row.DocumentText}, которую держит {row.UserText} с {row.SinceText}?\n" +
                "Владелец получит уведомление при следующем подключении; его локальные правки не удаляются, но сдать их он сможет только снова взяв документ в работу.",
                "Снять блокировку", "Отмена"))
            return;
        var id = row.Lock.DocumentId;
        var ok = await Busy.RunAsync("Снять блокировку", (_, ct) => Session.Api.ForceUnlockAsync(id, ct));
        if (!ok) return;
        Busy.Done($"Блокировка {row.DocumentText} снята.");
        await LoadLocksAsync();
    }

    private bool HasLock() => SelectedLock != null;

    // ------------------------------------------------------------------ проекты

    [RelayCommand]
    private async Task LoadProjectsAsync()
    {
        var outcome = await Busy.RunAsync("Проекты", (_, ct) => Session.Api.GetProjectsAsync(ct));
        if (!outcome.Ok) return;
        Projects.ReplaceWith(outcome.Value!.OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            Dialogs.ShowError("Новый проект", new ErrorInfo("Укажите название проекта или библиотеки."));
            return;
        }
        var request = new CreateProjectRequest { Name = NewProjectName.Trim(), Kind = NewProjectKind.Kind };
        var outcome = await Busy.RunAsync("Создание проекта", (_, ct) => Session.Api.CreateProjectAsync(request, ct));
        if (!outcome.Ok) return;
        Busy.Done($"Создан{(request.Kind == ProjectKind.Library ? "а библиотека" : " проект")} «{outcome.Value!.Name}».");
        NewProjectName = string.Empty;
        await LoadProjectsAsync();
    }

    // ------------------------------------------------------------------ настройки сервера

    [RelayCommand]
    private async Task LoadSettingsAsync()
    {
        var outcome = await Busy.RunAsync("Настройки сервера", (_, ct) => Session.Api.GetSettingsAsync(ct));
        if (!outcome.Ok) return;
        var values = outcome.Value!.Values;
        var rows = new List<SettingRow>();
        foreach (var (cad, name) in KnownCads)
        {
            var key = SettingKeys.WorkingVersion(cad);
            values.TryGetValue(key, out var v);
            rows.Add(new SettingRow(key, $"Рабочая версия {name}", v ?? string.Empty));
        }
        foreach (var p in values.Where(p => rows.All(r => r.Key != p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            rows.Add(new SettingRow(p.Key, p.Key, p.Value));
        Settings.ReplaceWith(rows);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var dto = new SettingsDto();
        foreach (var s in Settings.Where(s => s.IsChanged)) dto.Values[s.Key] = s.Value.Trim();
        if (dto.Values.Count == 0)
        {
            Busy.Done("Настройки не изменились.");
            return;
        }
        var outcome = await Busy.RunAsync("Сохранение настроек", (_, ct) => Session.Api.UpdateSettingsAsync(dto, ct));
        if (!outcome.Ok) return;
        Busy.Done("Настройки сервера сохранены.");
        await LoadSettingsAsync();
    }

    // ------------------------------------------------------------------ журнал

    [RelayCommand]
    private async Task LoadJournalAsync()
    {
        var limit = JournalLimit is > 0 and <= 100000 ? JournalLimit : 500;
        var outcome = await Busy.RunAsync("Журнал", (_, ct) => Session.Api.GetJournalAsync(limit, ct));
        if (!outcome.Ok) return;
        Journal.ReplaceWith(outcome.Value!.OrderByDescending(j => j.At).Select(j => new JournalRow(j)));
    }
}

public sealed record RoleOption(UserRole Role, string Text);

public sealed record KindOption(ProjectKind Kind, string Text);

public sealed class UserRow
{
    public UserRow(UserDto u) => User = u;

    public UserDto User { get; }
    public string Login => User.Login;
    public string DisplayName => User.DisplayName;
    public string RoleText => Format.Role(User.Role);
    public string ActiveText => User.Active ? "активен" : "отключён";
    public string ToggleText => User.Active ? "Отключить" : "Включить";
}

public sealed class LockRow
{
    public LockRow(LockDto l, DocumentSummaryDto? doc)
    {
        Lock = l;
        DocumentText = doc == null ? l.DocumentId.ToString() : Format.Doc(doc.RelPath, doc.Designation);
        ProjectName = doc?.ProjectName ?? string.Empty;
        Name = doc?.Name;
    }

    public LockDto Lock { get; }
    public string DocumentText { get; }
    public string? Name { get; }
    public string ProjectName { get; }
    public string UserText => string.IsNullOrWhiteSpace(Lock.UserDisplayName) ? Lock.UserLogin : $"{Lock.UserDisplayName} ({Lock.UserLogin})";
    public string SinceText => Format.Time(Lock.At);
}

public sealed partial class SettingRow : ObservableObject
{
    private readonly string _original;

    public SettingRow(string key, string title, string value)
    {
        Key = key;
        Title = title;
        _original = value;
        _value = value;
    }

    public string Key { get; }
    public string Title { get; }

    [ObservableProperty]
    private string _value;

    public bool IsChanged => !string.Equals(_original, Value, StringComparison.Ordinal);
}
