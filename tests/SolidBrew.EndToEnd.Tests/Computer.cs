using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;
using SolidBrew.Workspace;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>
/// «Компьютер» конструктора: свой вход (настоящий <see cref="PdmApiClient"/> через HTTP), свой кэш и своя рабочая папка,
/// <see cref="WorkspaceService"/> с тестовым CAD.
/// </summary>
public sealed class Computer
{
    private Computer(UserDto user, PdmApiClient api, RequestCounter counter, LocalCache cache, WorkspaceService service, string workRoot)
    {
        User = user;
        Api = api;
        Counter = counter;
        Cache = cache;
        Service = service;
        WorkRoot = workRoot;
    }

    public UserDto User { get; }
    public PdmApiClient Api { get; }
    public RequestCounter Counter { get; }
    public LocalCache Cache { get; }
    public WorkspaceService Service { get; }
    public string WorkRoot { get; }

    /// <summary>Новый пользователь-конструктор на отдельном компьютере (кэш и рабочая папка — в <paramref name="dir"/>).</summary>
    public static async Task<Computer> NewAsync(ServerFixture fx, TempDir dir, string name)
    {
        var user = await fx.NewUserAsync(name);
        return await LoginAsync(fx, dir, user, name);
    }

    /// <summary>Тот же пользователь на другом компьютере.</summary>
    public static async Task<Computer> LoginAsync(ServerFixture fx, TempDir dir, UserDto user, string computerName)
    {
        var counter = new RequestCounter();
        var api = fx.NewClient(counter);
        await api.LoginAsync(user.Login, ServerFixture.UserPassword);
        var registry = new CadAdapterRegistry();
        registry.Register(new TestCadAdapter());
        var cache = new LocalCache(dir.Path(computerName, "cache"));
        var service = new WorkspaceService(api, registry, cache, new WorkspaceServiceOptions { ServerUrl = "http://localhost/" });
        return new Computer(user, api, counter, cache, service, dir.Path(computerName, "work"));
    }

    /// <summary>Та же учётная запись и кэш, другая рабочая папка.</summary>
    public Computer WithFolder(string workRoot) => new(User, Api, Counter, Cache, Service, workRoot);

    public string File(string relPath) => Path.Combine(WorkRoot, relPath.Replace('/', '\\'));

    public WorkspaceState State => WorkspaceState.Load(WorkRoot);

    public Guid Id(string relPath) => State.FindByPath(relPath)?.DocumentId ?? throw new InvalidOperationException($"«{relPath}» нет в рабочей папке.");

    public async Task<OpenResult> OpenAsync(Guid documentId, int? version = null, bool verify = false, bool replaceAll = false)
    {
        var plan = await Service.PrepareOpenAsync(WorkRoot, documentId, version);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        if (replaceAll)
        {
            foreach (var c in plan.Conflicts) c.Resolution = ConflictResolution.Replace;
        }
        var result = await Service.ExecuteOpenAsync(plan, verify);
        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Problems));
        return result;
    }

    public async Task CheckOutAsync(params string[] relPaths)
    {
        var r = await Service.CheckOutAsync(WorkRoot, relPaths.Select(Id));
        Assert.True(r.Success, string.Join("\n", r.Items.Select(i => i.Message)));
    }

    public async Task<CheckinResult> CheckinAsync(string comment = "правка", bool keepLocks = false)
    {
        var plan = await Service.PrepareCheckinAsync(WorkRoot);
        Assert.True(plan.CanExecute, plan.Summary + "\n" + string.Join("\n", plan.Problems));
        var result = await Service.ExecuteCheckinAsync(plan, comment, keepLocks);
        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Problems));
        return result;
    }

    /// <summary>Правка модели в CAD: дописать «геометрию».</summary>
    public void Edit(string relPath, string line = "edit") => TestCadWriter.AppendGeometry(File(relPath), line + " " + Guid.NewGuid().ToString("N")[..6]);

    public async Task<DocumentSummaryDto> DocAsync(Guid id) => (await Api.GetDocumentAsync(id)).Document;

    public static string Hash(string path) => Sha256Hex.ComputeFileAsync(path).GetAwaiter().GetResult();

    public static bool IsReadOnly(string path) => (System.IO.File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
}
