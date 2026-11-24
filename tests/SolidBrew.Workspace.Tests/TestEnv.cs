using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Workspace.Tests;

/// <summary>Временная папка теста, сервер в памяти, два пользователя, проект.</summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sbws-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Server.AddUser("ivanov", "Иванов И. И.");
        Server.AddUser("petrov", "Петров П. П.");
        Server.AddUser("admin", "Администратор", UserRole.Admin);
        Project = Server.AddProject("Изделие АБВ");
        Registry.Register(new TestCadAdapter());
    }

    public string Root { get; }
    public FakePdmServer Server { get; } = new();
    public ProjectDto Project { get; }
    public CadAdapterRegistry Registry { get; } = new();

    public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>Рабочее место пользователя: свой кэш и своя рабочая папка.</summary>
    public Client For(string login, string? workspace = null, string? cacheName = null)
    {
        var api = Server.Connect(login);
        var cache = new LocalCache(Path("cache-" + (cacheName ?? login)));
        return new Client(api, cache, new WorkspaceService(api, Registry, cache), Path(workspace ?? "ws-" + login));
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

    // ------------------------------------------------------------------ исходный проект

    /// <summary>
    /// Готовый проект «на сетевом диске»: сборка с подсборкой, общей деталью, двумя конфигурациями и чертежами.
    /// src/Изделие.tcasm → Сборки/Узел.tcasm, Детали/Корпус.tcprt, Детали/Болт.tcprt; Узел → Детали/Вал.tcprt, Детали/Болт.tcprt;
    /// Чертежи/Корпус.tcdrw → Детали/Корпус.tcprt, Чертежи/Изделие.tcdrw → Изделие.tcasm.
    /// Как в SOLIDWORKS, все ссылки записаны <b>абсолютными путями исходной папки</b>: после раскладки в рабочую папку
    /// компоненты из подпапок (детали для сборки из корня, модели для чертежей) находятся только по этим путям —
    /// пока клиент не переписал ссылки, тестовый CAD грузит их из исходной папки или не находит вовсе.
    /// </summary>
    public string CreateSourceProject(string folder = "src")
    {
        var src = Path(folder);
        string P(string rel) => System.IO.Path.Combine(src, rel);
        TestCadWriter.Write(P(@"Детали\Корпус.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.001").Prop("Наименование", "Корпус").Geom("box 10 20 30"));
        TestCadWriter.Write(P(@"Детали\Вал.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.002").Prop("Наименование", "Вал").Geom("cyl 5 100"));
        TestCadWriter.Write(P(@"Детали\Болт.tcprt"), new TestCadDocument().Prop("Обозначение", "ГОСТ 7798 М8").Geom("bolt M8"));
        TestCadWriter.Write(P(@"Сборки\Узел.tcasm"), new TestCadDocument().Prop("Обозначение", "АБВ.100").Prop("Наименование", "Узел")
            .Ref(P(@"Детали\Вал.tcprt"), P(@"Детали\Болт.tcprt")));
        TestCadWriter.Write(P("Изделие.tcasm"), new TestCadDocument().Config("Исп1", "Исп2")
            .Prop("Обозначение", "АБВ.000").Prop("Наименование", "Изделие").Prop("Масса", "12", "Исп2")
            .Ref(P(@"Сборки\Узел.tcasm"), P(@"Детали\Корпус.tcprt"), P(@"Детали\Болт.tcprt")));
        TestCadWriter.Write(P(@"Чертежи\Корпус.tcdrw"), new TestCadDocument().Prop("Обозначение", "АБВ.001 СБ").Ref(P(@"Детали\Корпус.tcprt")));
        TestCadWriter.Write(P(@"Чертежи\Изделие.tcdrw"), new TestCadDocument().Prop("Обозначение", "АБВ.000 СБ").Ref(P("Изделие.tcasm")));
        // Чертёж, не относящийся к комплекту, — не должен попасть в постановку.
        TestCadWriter.Write(P(@"Чертежи\Чужой.tcdrw"), new TestCadDocument().Ref(@"Q:\Другой\Чужая.tcprt"));
        return src;
    }

    /// <summary>Поставить исходный проект на учёт от имени пользователя. Возвращает головной документ.</summary>
    public async Task<(Guid HeadId, ImportResult Result)> ImportSourceAsync(Client c, string? src = null)
    {
        src ??= CreateSourceProject();
        using var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"), new[] { System.IO.Path.Combine(src, "Чертежи") });
        var result = await c.Service.ExecuteImportAsync(report, Project.Id, "Постановка");
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        return (result.HeadDocumentId, result);
    }

    public Guid DocId(string relPath) => Server.DocByPath(relPath).Id;
}

public sealed record Client(FakePdmApi Api, LocalCache Cache, WorkspaceService Service, string WorkRoot)
{
    public string File(string relPath) => System.IO.Path.Combine(WorkRoot, relPath.Replace('/', '\\'));

    public async Task<OpenResult> OpenAsync(Guid documentId, int? version = null, bool verify = false)
    {
        var plan = await Service.PrepareOpenAsync(WorkRoot, documentId, version);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        var result = await Service.ExecuteOpenAsync(plan, verify);
        Assert.True(result.Success, result.Message);
        return result;
    }

    public async Task<CheckinResult> CheckinAllAsync(string comment = "правка", bool keepLocks = false)
    {
        var plan = await Service.PrepareCheckinAsync(WorkRoot);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems) + plan.Summary);
        var result = await Service.ExecuteCheckinAsync(plan, comment, keepLocks);
        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Problems));
        return result;
    }

    public async Task CheckOutAsync(params Guid[] ids)
    {
        var r = await Service.CheckOutAsync(WorkRoot, ids);
        Assert.True(r.Success, string.Join("\n", r.Items.Select(i => i.Message)));
    }

    public WorkspaceState State => WorkspaceState.Load(WorkRoot);

    public static bool IsReadOnly(string path) => (System.IO.File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    public static string Hash(string path) => Sha256Hex.ComputeFileAsync(path).GetAwaiter().GetResult();
}
