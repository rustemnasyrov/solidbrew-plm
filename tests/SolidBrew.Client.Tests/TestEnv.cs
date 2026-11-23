using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

/// <summary>Временная папка, сервер в памяти, пользователи и проект, подменённые диалоги.</summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sbclient-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Server.AddUser("ivanov", "Иванов И. И.");
        Server.AddUser("petrov", "Петров П. П.");
        Server.AddUser("admin", "Администратор", UserRole.Admin);
        Project = Server.AddProject("Изделие АБВ");
        Registry.Register(new TestCadAdapter());
        Factory = new FakeClientFactory(this);
        Services = new AppServices(Dialogs, Shell, Profiles, Factory);
    }

    public string Root { get; }
    public FakePdmServer Server { get; } = new();
    public ProjectDto Project { get; }
    public CadAdapterRegistry Registry { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeShell Shell { get; } = new();
    public MemoryProfileStore Profiles { get; } = new();
    public FakeClientFactory Factory { get; }
    public AppServices Services { get; }

    public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>Сеанс пользователя с рабочей папкой <c>ws-login</c>.</summary>
    public AppSession Session(string login)
    {
        var api = Server.Connect(login);
        var profile = new ClientProfile { ServerUrl = "http://fake/", Login = login, CacheRoot = Path("cache-" + login), EnableTestCad = true };
        return new AppSession(profile, api, api.User, Factory.CreateWorkspace(api, profile), Services);
    }

    public string WorkRoot(string login) => Path("ws-" + login);

    public WorkflowController Flow(AppSession session)
    {
        var busy = new BusyState { ErrorHandler = (title, e) => Dialogs.ShowError(title, e) };
        var flow = new WorkflowController(session, busy);
        flow.SetWorkRoot(WorkRoot(session.Me.Login));
        return flow;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Исходный проект: Изделие.tcasm → Детали/Корпус, Детали/Вал; чертёж Чертежи/Корпус.tcdrw → Корпус.
    /// </summary>
    public string CreateSourceProject(string folder = "src")
    {
        var src = Path(folder);
        string P(string rel) => System.IO.Path.Combine(src, rel);
        TestCadWriter.Write(P(@"Детали\Корпус.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.001").Prop("Наименование", "Корпус").Geom("box 10 20 30"));
        TestCadWriter.Write(P(@"Детали\Вал.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.002").Prop("Наименование", "Вал").Geom("cyl 5 100"));
        TestCadWriter.Write(P("Изделие.tcasm"), new TestCadDocument().Config("Исп1", "Исп2")
            .Prop("Обозначение", "АБВ.000").Prop("Наименование", "Изделие")
            .Ref(@"Детали\Корпус.tcprt", @"Детали\Вал.tcprt"));
        TestCadWriter.Write(P(@"Чертежи\Корпус.tcdrw"), new TestCadDocument().Prop("Обозначение", "АБВ.001 СБ").Ref(@"..\Детали\Корпус.tcprt"));
        return src;
    }

    /// <summary>Поставить проект на учёт напрямую через службу. Возвращает головной документ.</summary>
    public async Task<Guid> ImportAsync(AppSession session)
    {
        var src = CreateSourceProject();
        using var report = await session.Workspace.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"), new[] { System.IO.Path.Combine(src, "Чертежи") });
        var result = await session.Workspace.ExecuteImportAsync(report, Project.Id, "Постановка");
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        return result.HeadDocumentId;
    }

    /// <summary>Открыть комплект в рабочую папку напрямую через службу.</summary>
    public async Task OpenDirectAsync(AppSession session, Guid documentId)
    {
        var plan = await session.Workspace.PrepareOpenAsync(WorkRoot(session.Me.Login), documentId);
        var result = await session.Workspace.ExecuteOpenAsync(plan);
        Assert.True(result.Success, result.Message);
    }

    public Guid DocId(string relPath) => Server.DocByPath(relPath).Id;

    public static string Hash(string path) => Sha256Hex.ComputeFileAsync(path).GetAwaiter().GetResult();

    public static bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
}

/// <summary>Клиент API и служба рабочих папок над сервером в памяти.</summary>
public sealed class FakeClientFactory : IClientFactory
{
    private readonly TestEnv _env;

    public FakeClientFactory(TestEnv env) => _env = env;

    /// <summary>Кто войдёт при следующем CreateApi (логин берётся из LoginAsync, но API создаётся раньше).</summary>
    public string NextLogin { get; set; } = "ivanov";

    public FakePdmApi? LastApi { get; private set; }

    /// <summary>Настройка созданного API (например, отказ входа).</summary>
    public Action<FakePdmApi>? Configure { get; set; }

    public IPdmApi CreateApi(string serverUrl, string? token)
    {
        var api = _env.Server.Connect(NextLogin);
        api.Token = token;
        Configure?.Invoke(api);
        LastApi = api;
        return api;
    }

    public WorkspaceService CreateWorkspace(IPdmApi api, ClientProfile profile) =>
        new(api, _env.Registry, new LocalCache(profile.CacheRoot ?? _env.Path("cache")), new WorkspaceServiceOptions { ServerUrl = profile.ServerUrl });
}

public sealed class MemoryProfileStore : IProfileStore
{
    public ClientProfile Stored { get; private set; } = new();
    public int Saves;

    public ClientProfile Load() => Stored;

    public void Save(ClientProfile profile)
    {
        Saves++;
        Stored = profile;
        SavedToken = profile.Token;
    }

    /// <summary>Токен на момент последнего сохранения.</summary>
    public string? SavedToken { get; private set; }
}

public sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = new();

    public void OpenFile(string path) => Opened.Add(path);

    public void ShowInExplorer(string path) => Opened.Add("explorer:" + path);
}

/// <summary>Диалоги для тестов: ответы задаются заранее, вопросы записываются.</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<(string Title, string Message)> Infos { get; } = new();
    public List<(string Title, ErrorInfo Error)> Errors { get; } = new();
    public List<(string Title, string Message, List<string> Details)> Confirms { get; } = new();
    public List<(string Title, string Message, IReadOnlyList<string> Options, List<string> Details)> Choices { get; } = new();
    public List<DialogViewModel> Shown { get; } = new();

    /// <summary>Ответы на Confirm по порядку; когда закончились — <see cref="DefaultConfirm"/>.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();
    public bool DefaultConfirm { get; set; } = true;
    public Queue<int> ChooseAnswers { get; } = new();
    public string? FolderToPick { get; set; }
    public string? FileToPick { get; set; }

    /// <summary>Что делать с модальным экраном (по умолчанию — закрыть без результата).</summary>
    public Func<DialogViewModel, bool?>? OnShowDialog { get; set; }

    public void ShowInfo(string title, string message, IEnumerable<string>? details = null) => Infos.Add((title, message));

    public void ShowError(string title, ErrorInfo error) => Errors.Add((title, error));

    public bool Confirm(string title, string message, string yesText = "Да", string noText = "Отмена", IEnumerable<string>? details = null)
    {
        Confirms.Add((title, message, details?.ToList() ?? new List<string>()));
        return ConfirmAnswers.Count > 0 ? ConfirmAnswers.Dequeue() : DefaultConfirm;
    }

    public int Choose(string title, string message, IReadOnlyList<string> options, IEnumerable<string>? details = null)
    {
        Choices.Add((title, message, options, details?.ToList() ?? new List<string>()));
        return ChooseAnswers.Count > 0 ? ChooseAnswers.Dequeue() : -1;
    }

    public string? PickFile(string title, string filter, string? initialFolder = null) => FileToPick;

    public string? PickFolder(string title, string? initialFolder = null) => FolderToPick;

    public string? SaveFile(string title, string filter, string defaultFileName, string? initialFolder = null) => FileToPick;

    public bool? ShowDialog(DialogViewModel viewModel)
    {
        Shown.Add(viewModel);
        return OnShowDialog?.Invoke(viewModel);
    }

    public string AllErrors => string.Join("\n", Errors.Select(e => e.Title + ": " + e.Error));
}
