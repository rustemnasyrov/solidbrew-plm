using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Сеанс работы: профиль, клиент API, текущий пользователь, служба рабочих папок.</summary>
public sealed class AppSession
{
    public AppSession(ClientProfile profile, IPdmApi api, UserDto me, WorkspaceService workspace, AppServices services)
    {
        Profile = profile;
        Api = api;
        Me = me;
        Workspace = workspace;
        Services = services;
    }

    public ClientProfile Profile { get; }
    public IPdmApi Api { get; }
    public UserDto Me { get; }
    public WorkspaceService Workspace { get; private set; }
    public AppServices Services { get; }

    public bool IsAdmin => Me.Role == UserRole.Admin;

    /// <summary>Может менять хранилище (не читатель).</summary>
    public bool CanModify => Me.Role != UserRole.Reader;

    public string UserText => $"{(string.IsNullOrWhiteSpace(Me.DisplayName) ? Me.Login : Me.DisplayName)} — {Format.Role(Me.Role)}";

    /// <summary>Пересоздать службу рабочих папок после изменения настроек (кэш, тестовый CAD).</summary>
    public void RebuildWorkspace() => Workspace = Services.Factory.CreateWorkspace(Api, Profile);

    public void SaveProfile() => Services.Profiles.Save(Profile);
}
