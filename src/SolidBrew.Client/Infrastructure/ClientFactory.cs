using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Infrastructure;

/// <summary>
/// Настоящие API и адаптеры CAD. SOLIDWORKS подключается при первом обращении адаптера,
/// поэтому без установленного SOLIDWORKS ошибка будет только у операций, где он нужен.
/// </summary>
public sealed class ClientFactory : IClientFactory, IDisposable
{
    private SolidWorksAdapter? _solidWorks;

    public IPdmApi CreateApi(string serverUrl, string? token, string? trustedThumbprint) =>
        PdmClientFactory.Create(serverUrl, token, trustedThumbprint);

    public WorkspaceService CreateWorkspace(IPdmApi api, ClientProfile profile)
    {
        var registry = new CadAdapterRegistry();
        _solidWorks ??= new SolidWorksAdapter();
        registry.Register(_solidWorks);
        if (profile.EnableTestCad) registry.Register(new TestCadAdapter());
        var cache = new LocalCache(string.IsNullOrWhiteSpace(profile.CacheRoot) ? null : profile.CacheRoot);
        return new WorkspaceService(api, registry, cache, new WorkspaceServiceOptions { ServerUrl = profile.ServerUrl });
    }

    public void Dispose()
    {
        _solidWorks?.Dispose();
        _solidWorks = null;
    }
}
