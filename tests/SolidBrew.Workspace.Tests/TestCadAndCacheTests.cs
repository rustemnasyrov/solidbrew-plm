using System.Text;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Workspace.Tests;

public class TestCadTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Read_ParsesVersionConfigsPropertiesAndReferences()
    {
        var path = TestCadWriter.Write(_env.Path("a", "Сборка.tcasm"), new TestCadDocument { CadVersion = 2022 }
            .Config("Исп1", "Исп2").Prop("Обозначение", "АБВ.000").Prop("Масса", "5", "Исп2").Ref(@"C:\x\Деталь.tcprt", @"sub\Вал.tcprt").Geom("line 1"));
        var info = await new TestCadAdapter().ReadAsync(path);

        Assert.Equal(DocumentKind.Assembly, info.Kind);
        Assert.Equal("TestCad 2022", info.CadVersion);
        Assert.Equal(2022, info.CadVersionMajor);
        Assert.Equal(new[] { "Исп1", "Исп2" }, info.Configurations);
        Assert.Equal("АБВ.000", info.Properties[""]["Обозначение"]);
        Assert.Equal("5", info.Properties["Исп2"]["Масса"]);
        Assert.Equal(new[] { "Деталь.tcprt", "Вал.tcprt" }, info.References.Select(r => r.FileName));
        Assert.Equal(new[] { "Исп1", "Исп2" }, info.References[0].Configurations);
    }

    [Fact]
    public async Task ReplaceReferences_RewritesOnlyRefLines()
    {
        var path = _env.Path("Сборка.tcasm");
        var original = "cadver: 2018\r\nprop: Обозначение=А\nref: Q:\\old\\Деталь.tcprt\r\n  геометрия ref: не ссылка\r\nref: другая.tcprt\r\nконец";
        File.WriteAllText(path, original, new UTF8Encoding(false));

        await new TestCadAdapter().ReplaceReferencesAsync(path, new Dictionary<string, string> { [@"Q:\old\Деталь.tcprt"] = @"C:\new\Деталь2.tcprt" });

        var expected = "cadver: 2018\r\nprop: Обозначение=А\nref: C:\\new\\Деталь2.tcprt\r\n  геометрия ref: не ссылка\r\nref: другая.tcprt\r\nконец";
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ReplaceReferences_RelativeStaysRelative()
    {
        var path = TestCadWriter.Write(_env.Path("ws", "Сборки", "Узел.tcasm"), new TestCadDocument().Ref(@"..\Детали\Болт.tcprt"));
        await new TestCadAdapter().ReplaceReferencesAsync(path, new Dictionary<string, string> { [@"..\Детали\Болт.tcprt"] = _env.Path("ws", "Детали", "Крепёж", "Болт.tcprt") });
        var info = await new TestCadAdapter().ReadAsync(path);
        Assert.Equal(@"..\Детали\Крепёж\Болт.tcprt", info.References.Single().SavedPath);
    }

    [Fact]
    public async Task OpenAndVerify_PrefersSameFolderThenRelativeThenAbsolute()
    {
        var root = _env.Path("ws");
        var elsewhere = _env.Path("elsewhere");
        TestCadWriter.Create(System.IO.Path.Combine(elsewhere, "Деталь.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(elsewhere, "Вал.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(root, "Деталь.tcprt"));           // то же имя в папке сборки — побеждает
        TestCadWriter.Create(System.IO.Path.Combine(root, "sub", "Ось.tcprt"));      // по относительному пути
        var head = TestCadWriter.Create(System.IO.Path.Combine(root, "Сборка.tcasm"),
            new[] { System.IO.Path.Combine(elsewhere, "Деталь.tcprt"), @"sub\Ось.tcprt", System.IO.Path.Combine(elsewhere, "Вал.tcprt"), @"Q:\нет\Нет.tcprt" },
            configurations: new[] { "A", "B" });

        var report = await new TestCadAdapter().OpenAndVerifyAsync(head, root);

        Assert.Equal(2, report.Configurations.Count);
        var comps = report.Configurations[0].Components.ToDictionary(c => c.Name);
        Assert.Equal(System.IO.Path.Combine(root, "Деталь.tcprt"), comps["Деталь.tcprt"].LoadedPath);
        Assert.True(comps["Деталь.tcprt"].InsideWorkRoot);
        Assert.Equal(System.IO.Path.Combine(root, "sub", "Ось.tcprt"), comps["Ось.tcprt"].LoadedPath);
        Assert.False(comps["Вал.tcprt"].InsideWorkRoot);
        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains("Нет.tcprt"));
        Assert.Contains(report.Problems, p => p.Contains("Вал.tcprt") && p.Contains("не из рабочей папки"));
    }
}

public class CacheTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private (string Sha, long Size) Put(byte[] bytes)
    {
        var sha = Sha256Hex.Compute(new MemoryStream(bytes));
        _env.Server.Objects[sha] = bytes;
        return (sha, bytes.Length);
    }

    [Fact]
    public async Task Ensure_DownloadsOnlyMissing_ObjectsReadOnly()
    {
        var a = Put(Encoding.UTF8.GetBytes("объект A"));
        var b = Put(Encoding.UTF8.GetBytes("объект B"));
        var c = _env.For("ivanov");

        Assert.Equal(1, await c.Cache.EnsureAsync(c.Api, new[] { new CacheObject(a.Sha, a.Size) }));
        Assert.Equal(1, await c.Cache.EnsureAsync(c.Api, new[] { new CacheObject(a.Sha, a.Size), new CacheObject(b.Sha, b.Size) }));
        Assert.Equal(0, await c.Cache.EnsureAsync(c.Api, new[] { new CacheObject(a.Sha, a.Size), new CacheObject(b.Sha, b.Size) }));
        Assert.Equal(2, _env.Server.DownloadCount);

        var path = c.Cache.PathOf(a.Sha);
        Assert.Equal(System.IO.Path.Combine(c.Cache.Root, "objects", a.Sha.Substring(0, 2), a.Sha), path);
        Assert.True(Client.IsReadOnly(path));
        Assert.Equal("объект A", File.ReadAllText(path));
    }

    [Fact]
    public async Task Ensure_HashMismatch_FailsAndLeavesNoGarbage()
    {
        var a = Put(Encoding.UTF8.GetBytes("правильное содержимое"));
        _env.Server.DownloadTamper = (_, bytes) => Encoding.UTF8.GetBytes("подменённое содержимое!");
        var c = _env.For("ivanov");

        var ex = await Assert.ThrowsAsync<WorkspaceException>(() => c.Cache.EnsureAsync(c.Api, new[] { new CacheObject(a.Sha, a.Size) }));
        Assert.Contains("повреждён", ex.Message);
        Assert.False(c.Cache.Contains(a.Sha));
        var tmp = System.IO.Path.Combine(c.Cache.Root, "tmp");
        Assert.True(!Directory.Exists(tmp) || !Directory.EnumerateFiles(tmp).Any());
    }

    [Fact]
    public async Task AddFile_VerifiesHash()
    {
        var c = _env.For("ivanov");
        var f = _env.Path("x.bin");
        File.WriteAllText(f, "содержимое");
        await Assert.ThrowsAsync<WorkspaceException>(() => c.Cache.AddFileAsync(f, new string('0', 64)));
        var sha = Client.Hash(f);
        await c.Cache.AddFileAsync(f, sha);
        Assert.True(c.Cache.Contains(sha));
    }
}
