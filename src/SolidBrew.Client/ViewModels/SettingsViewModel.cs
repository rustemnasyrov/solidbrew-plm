using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Экран «Настройки»: адрес сервера, папка кэша, тестовый CAD.</summary>
public sealed partial class SettingsViewModel : DialogViewModel
{
    private readonly AppServices _services;
    private readonly ClientProfile _profile;

    public SettingsViewModel(AppServices services, ClientProfile profile) : base(services.Dialogs)
    {
        _services = services;
        _profile = profile;
        _serverUrl = profile.ServerUrl;
        _cacheRoot = string.IsNullOrWhiteSpace(profile.CacheRoot) ? LocalCache.DefaultRoot : profile.CacheRoot;
        _enableTestCad = profile.EnableTestCad;
    }

    public override string Title => "Настройки";

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _cacheRoot;

    [ObservableProperty]
    private bool _enableTestCad;

    [ObservableProperty]
    private string? _errorMessage;

    public string DefaultCacheRoot => LocalCache.DefaultRoot;

    /// <summary>Изменился адрес сервера (нужен повторный вход).</summary>
    public bool ServerChanged { get; private set; }

    /// <summary>Изменились кэш или набор CAD (пересоздать службу рабочих папок).</summary>
    public bool WorkspaceChanged { get; private set; }

    [RelayCommand]
    private void BrowseCache()
    {
        var folder = Dialogs.PickFolder("Папка кэша", Directory.Exists(CacheRoot) ? CacheRoot : null);
        if (folder != null) CacheRoot = folder;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = null;
        var url = ServerUrl.Trim();
        if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = "Неверный адрес сервера. Пример: http://10.0.1.9:8088/";
            return;
        }
        string? cache = string.IsNullOrWhiteSpace(CacheRoot) ? null : CacheRoot.Trim();
        if (cache != null)
        {
            try
            {
                cache = Path.GetFullPath(cache);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ErrorMessage = "Неверный путь к папке кэша.";
                return;
            }
            if (string.Equals(cache.TrimEnd('\\'), LocalCache.DefaultRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) cache = null;
        }

        ServerChanged = !string.Equals(_profile.ServerUrl.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        WorkspaceChanged = !string.Equals(_profile.CacheRoot ?? string.Empty, cache ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                           || _profile.EnableTestCad != EnableTestCad;
        _profile.ServerUrl = url;
        _profile.CacheRoot = cache;
        _profile.EnableTestCad = EnableTestCad;
        try
        {
            _services.Profiles.Save(_profile);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Настройки не сохранены: " + ErrorText.Describe(ex).Message;
            return;
        }
        Close(true);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
