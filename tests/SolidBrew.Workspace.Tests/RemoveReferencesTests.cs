using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Cad;

namespace SolidBrew.Workspace.Tests;

/// <summary>«Удалить ссылку» на файл, не учтённый в PDM: сборка — компонент удаляется, деталь — ссылки разрываются и скрываются.</summary>
public class RemoveReferencesTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private TestCadAdapter Cad => (TestCadAdapter)_env.Registry.Get(TestCadAdapter.Id)!;

    /// <summary>Поставить на учёт файл с отсутствующими ссылками (с разрешением) и открыть его.</summary>
    private async Task<(Client C, Guid Id, string File)> ImportAsync(string name, TestCadDocument doc)
    {
        var src = TestCadWriter.Write(_env.Path("src", name), doc);
        var c = _env.For("admin");
        using (var report = await c.Service.PrepareImportAsync(src))
        {
            var r = await c.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка", allowMissing: true);
            Assert.True(r.Success, r.Message);
            await c.OpenAsync(r.HeadDocumentId);
            return (c, r.HeadDocumentId, c.File(name));
        }
    }

    [Fact]
    public async Task Assembly_ComponentRemoved_NewVersionWithoutReference()
    {
        TestCadWriter.Create(_env.Path("src", "Вал.tcprt"));
        var (c, id, file) = await ImportAsync("Узел.tcasm", new TestCadDocument().Ref(_env.Path("src", "Вал.tcprt"), @"Q:\Старый\Лишний.tcprt"));
        Assert.Contains(_env.Server.Doc(id).Latest.Refs, r => r.Child == null);

        var r = await c.Service.RemoveReferencesAsync(c.WorkRoot, id, new[] { @"Q:\Старый\Лишний.tcprt" });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.Equal(2, r.NewVersion);
        var latest = _env.Server.Doc(id).Latest;
        Assert.DoesNotContain(latest.Refs, x => x.RefPath.EndsWith("Лишний.tcprt"));
        Assert.Contains(latest.Refs, x => x.Child != null); // Вал остался
        Assert.DoesNotContain("Лишний.tcprt", File.ReadAllText(file));
        Assert.Null(_env.Server.Doc(id).Lock);
        Assert.Contains("Лишний.tcprt", latest.Comment);
    }

    [Fact]
    public async Task Part_ReferencesBrokenAndHidden_StayHiddenOnNextCheckin()
    {
        var (c, id, file) = await ImportAsync("Уголок.tcprt", new TestCadDocument().Prop("Обозначение", "RM.001")
            .Ref(@"\\192.168.0.104\data\Упор.tcasm", @"C:\Downloads\Монитор.tcasm"));
        Assert.Equal(2, _env.Server.Doc(id).Latest.Refs.Count);

        var r = await c.Service.RemoveReferencesAsync(c.WorkRoot, id, new[] { @"\\192.168.0.104\data\Упор.tcasm", @"C:\Downloads\Монитор.tcasm" });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.Empty(_env.Server.Doc(id).Latest.Refs);
        var text = File.ReadAllText(file);
        Assert.Contains("ref: C:\\Downloads\\Монитор.tcasm", text); // в файле ссылка осталась «разорванной»
        Assert.Contains(DocumentProperties.RemovedReferencesKey + "=Упор.tcasm|Монитор.tcasm", text);

        // Обычная правка и сдача — скрытые ссылки не возвращаются.
        await c.CheckOutAsync(id);
        TestCadWriter.AppendGeometry(file, "правка");
        await c.CheckinAllAsync();
        Assert.Equal(3, _env.Server.Doc(id).Latest.Number);
        Assert.Empty(_env.Server.Doc(id).Latest.Refs);
    }

    [Fact]
    public async Task Part_WithOtherReferences_Refused_FileUntouched()
    {
        TestCadWriter.Create(_env.Path("src", "Основа.tcprt"));
        var (c, id, file) = await ImportAsync("Деталь.tcprt", new TestCadDocument().Ref(_env.Path("src", "Основа.tcprt"), @"Q:\Нет\Сборка.tcasm"));
        var before = Client.Hash(file);

        var r = await c.Service.RemoveReferencesAsync(c.WorkRoot, id, new[] { @"Q:\Нет\Сборка.tcasm" });

        Assert.False(r.Success);
        Assert.Contains("Основа.tcprt", r.Message);
        Assert.Contains("все сразу", r.Message);
        Assert.Equal(before, Client.Hash(file));
        Assert.Null(_env.Server.Doc(id).Lock);
    }

    [Fact]
    public async Task CadFails_FileRestored_LockReleased()
    {
        var (c, id, file) = await ImportAsync("Узел.tcasm", new TestCadDocument().Ref(@"Q:\Старый\Лишний.tcprt"));
        var before = Client.Hash(file);
        Cad.FailWrite = "SOLIDWORKS не ответил.";

        var r = await c.Service.RemoveReferencesAsync(c.WorkRoot, id, new[] { @"Q:\Старый\Лишний.tcprt" });

        Assert.False(r.Success);
        Assert.StartsWith("Ссылки не убраны", r.Message);
        Assert.Equal(before, Client.Hash(file));
        Assert.True(Client.IsReadOnly(file));
        Assert.Null(_env.Server.Doc(id).Lock);
        Assert.Equal(1, _env.Server.Doc(id).Latest.Number);
    }

    [Fact]
    public async Task Drawing_Refused()
    {
        var (c, id, _) = await ImportAsync("Чертёж.tcdrw", new TestCadDocument().Ref(@"Q:\Нет\Модель.tcprt"));

        var r = await c.Service.RemoveReferencesAsync(c.WorkRoot, id, new[] { @"Q:\Нет\Модель.tcprt" });

        Assert.False(r.Success);
        Assert.Contains("виды", r.Message);
    }
}
