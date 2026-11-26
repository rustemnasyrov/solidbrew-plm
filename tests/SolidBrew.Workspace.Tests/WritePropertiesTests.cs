using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Cad;

namespace SolidBrew.Workspace.Tests;

/// <summary>Запись свойств CAD через PDM: в файл и новой версией, с откатом при ошибке.</summary>
public class WritePropertiesTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private TestCadAdapter Cad => (TestCadAdapter)_env.Registry.Get(TestCadAdapter.Id)!;

    private async Task<(Client C, Guid Shaft, Guid Head)> OpenAsync()
    {
        var c = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(c);
        await c.OpenAsync(head);
        return (c, _env.DocId("Детали/Вал.tcprt"), head);
    }

    [Fact]
    public async Task FreeDocument_Taken_Written_CheckedIn_Released()
    {
        var (c, shaft, _) = await OpenAsync();
        var file = c.File("Детали/Вал.tcprt");

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[]
        {
            new CadPropertyChange("", "Материал", "Сталь 45 ГОСТ 1050-2013"),
            new CadPropertyChange("", "Разработал", "Иванов"),
        });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.Equal(2, r.NewVersion);
        var doc = _env.Server.Doc(shaft);
        Assert.Null(doc.Lock); // взятие было только на время операции
        var props = doc.Latest.Properties[""];
        Assert.Equal("Сталь 45 ГОСТ 1050-2013", props["Материал"]);
        Assert.Equal("Иванов", props["Разработал"]);
        Assert.Equal("АБВ.002", props["Обозначение"]); // прежние свойства на месте
        Assert.Contains("Материал", doc.Latest.Comment);

        // Рабочая папка: файл со свойствами, только для чтения, версия 2, без правок.
        Assert.True(Client.IsReadOnly(file));
        Assert.Contains("prop: Материал=Сталь 45 ГОСТ 1050-2013", File.ReadAllText(file));
        var entry = c.State.FindByDocument(shaft)!;
        Assert.Equal(2, entry.Version);
        Assert.False(entry.LockedByMe);
        Assert.Equal(doc.Latest.Sha256, Client.Hash(file));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(file)!), f => Path.GetFileName(f).StartsWith(".~sb"));
    }

    [Fact]
    public async Task ConfigurationProperty_WrittenToThatConfiguration()
    {
        var (c, _, head) = await OpenAsync();

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, head, new[] { new CadPropertyChange("Исп1", "Масса", "11,5") });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        var props = _env.Server.Doc(head).Latest.Properties;
        Assert.Equal("11,5", props["Исп1"]["Масса"]);
        Assert.Equal("12", props["Исп2"]["Масса"]);
        Assert.False(props[""].ContainsKey("Масса"));
    }

    [Fact]
    public async Task TakenByMe_WithoutEdits_CheckedIn_StaysTaken()
    {
        var (c, shaft, _) = await OpenAsync();
        await c.CheckOutAsync(shaft);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 20") });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.Equal(2, r.NewVersion);
        Assert.NotNull(_env.Server.Doc(shaft).Lock);
        Assert.False(Client.IsReadOnly(c.File("Детали/Вал.tcprt")));
        Assert.True(c.State.FindByDocument(shaft)!.LockedByMe);
    }

    [Fact]
    public async Task TakenByMe_WithOtherEdits_OnlyWrittenToFile()
    {
        var (c, shaft, _) = await OpenAsync();
        await c.CheckOutAsync(shaft);
        var file = c.File("Детали/Вал.tcprt");
        TestCadWriter.AppendGeometry(file, "правка геометрии");

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 20") });

        Assert.True(r.Success, r.Message);
        Assert.Null(r.NewVersion);
        Assert.Equal(1, _env.Server.Doc(shaft).Latest.Number);
        var text = File.ReadAllText(file);
        Assert.Contains("prop: Материал=Сталь 20", text);
        Assert.Contains("правка геометрии", text);

        // Сдаются вместе.
        await c.CheckinAllAsync();
        Assert.Equal("Сталь 20", _env.Server.Doc(shaft).Latest.Properties[""]["Материал"]);
    }

    [Fact]
    public async Task CadFails_FileRestored_LockReleased_NothingPublished()
    {
        var (c, shaft, _) = await OpenAsync();
        var file = c.File("Детали/Вал.tcprt");
        var before = Client.Hash(file);
        Cad.FailWrite = "SOLIDWORKS не ответил.";

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 20") });

        Assert.False(r.Success);
        Assert.Contains("SOLIDWORKS не ответил", r.Message);
        Assert.Equal(before, Client.Hash(file));
        Assert.True(Client.IsReadOnly(file));
        Assert.Null(_env.Server.Doc(shaft).Lock);
        Assert.Equal(1, _env.Server.Doc(shaft).Latest.Number);
        Assert.False(c.State.FindByDocument(shaft)!.LockedByMe);
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.GetDirectoryName(file)!), f => Path.GetFileName(f).StartsWith(".~sb"));
    }

    [Fact]
    public async Task OpenInCad_Refused_FileUntouched()
    {
        var (c, shaft, _) = await OpenAsync();
        var file = c.File("Детали/Вал.tcprt");
        var before = Client.Hash(file);
        Cad.OpenDocuments.Add(file);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 20") });

        Assert.False(r.Success);
        Assert.Contains("открыт", r.Message);
        Assert.Equal(before, Client.Hash(file));
        Assert.Null(_env.Server.Doc(shaft).Lock);
    }

    [Fact]
    public async Task TakenByOther_Refused()
    {
        var (c, shaft, head) = await OpenAsync();
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        await p.CheckOutAsync(shaft);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 20") });

        Assert.False(r.Success);
        Assert.Contains("Петров", r.Message);
        Assert.DoesNotContain("Материал", File.ReadAllText(c.File("Детали/Вал.tcprt")));
    }

    [Fact]
    public async Task Batch_AssemblyPartDrawing_OneCheckin_AllReleased()
    {
        var (c, shaft, head) = await OpenAsync();
        var drawing = _env.DocId("Чертежи/Корпус.tcdrw");
        await c.OpenAsync(drawing);
        var checkinsBefore = _env.Server.Checkins.Count;

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, new[]
        {
            new PropertyWrite(head, new[] { new CadPropertyChange("", "Разработал", "Иванов") }),
            new PropertyWrite(shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 45") }),
            new PropertyWrite(drawing, new[] { new CadPropertyChange("", "Проверил", "Петров") }),
        });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.Equal(3, r.Published.Count);
        Assert.Equal(checkinsBefore + 1, _env.Server.Checkins.Count); // одной сдачей
        Assert.Equal("Иванов", _env.Server.Doc(head).Latest.Properties[""]["Разработал"]);
        Assert.Equal("Сталь 45", _env.Server.Doc(shaft).Latest.Properties[""]["Материал"]);
        Assert.Equal("Петров", _env.Server.Doc(drawing).Latest.Properties[""]["Проверил"]);
        foreach (var id in new[] { head, shaft, drawing })
        {
            Assert.Null(_env.Server.Doc(id).Lock);
            Assert.Equal(2, _env.Server.Doc(id).Latest.Number);
            var e = c.State.FindByDocument(id)!;
            Assert.Equal(2, e.Version);
            Assert.False(e.LockedByMe);
            Assert.True(Client.IsReadOnly(c.File(e.RelPath)));
        }
        Assert.Contains("3 документа", r.Message);
    }

    [Fact]
    public async Task Batch_OneFileFails_AllRestored_NothingTaken()
    {
        var (c, shaft, head) = await OpenAsync();
        var drawing = _env.DocId("Чертежи/Корпус.tcdrw");
        await c.OpenAsync(drawing);
        var files = new[] { head, shaft, drawing }.ToDictionary(id => id, id => c.File(c.State.FindByDocument(id)!.RelPath));
        var before = files.ToDictionary(p => p.Key, p => Client.Hash(p.Value));
        Cad.OpenDocuments.Add(files[drawing]); // чертёж открыт в CAD — записать нельзя

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, new[]
        {
            new PropertyWrite(head, new[] { new CadPropertyChange("", "Разработал", "Иванов") }),
            new PropertyWrite(shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 45") }),
            new PropertyWrite(drawing, new[] { new CadPropertyChange("", "Проверил", "Петров") }),
        });

        Assert.False(r.Success);
        Assert.Contains("ни в один файл", r.Message);
        Assert.Contains("Корпус.tcdrw", r.Message);
        foreach (var (id, file) in files)
        {
            Assert.Equal(before[id], Client.Hash(file)); // уже записанные — возвращены
            Assert.True(Client.IsReadOnly(file));
            Assert.Null(_env.Server.Doc(id).Lock);
            Assert.Equal(1, _env.Server.Doc(id).Latest.Number);
            Assert.False(c.State.FindByDocument(id)!.LockedByMe);
        }
    }

    [Fact]
    public async Task Batch_TakenByMeStaysTaken_OthersReleased()
    {
        var (c, shaft, head) = await OpenAsync();
        await c.CheckOutAsync(shaft);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, new[]
        {
            new PropertyWrite(head, new[] { new CadPropertyChange("", "Разработал", "Иванов") }),
            new PropertyWrite(shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 45") }),
        });

        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));
        Assert.NotNull(_env.Server.Doc(shaft).Lock);
        Assert.True(c.State.FindByDocument(shaft)!.LockedByMe);
        Assert.False(Client.IsReadOnly(c.File("Детали/Вал.tcprt")));
        Assert.Null(_env.Server.Doc(head).Lock);
        Assert.False(c.State.FindByDocument(head)!.LockedByMe);
        Assert.True(Client.IsReadOnly(c.File("Изделие.tcasm")));
    }

    [Fact]
    public async Task Batch_OneTakenByOther_NothingWritten()
    {
        var (c, shaft, head) = await OpenAsync();
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        await p.CheckOutAsync(shaft);
        var headFile = c.File("Изделие.tcasm");
        var before = Client.Hash(headFile);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, new[]
        {
            new PropertyWrite(head, new[] { new CadPropertyChange("", "Разработал", "Иванов") }),
            new PropertyWrite(shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 45") }),
        });

        Assert.False(r.Success);
        Assert.Contains("Петров", r.Message);
        Assert.Equal(before, Client.Hash(headFile));
        Assert.Null(_env.Server.Doc(head).Lock);
        Assert.Equal(0, Cad.WriteCount);
    }

    [Fact]
    public async Task NotLatestInWorkspace_Refused()
    {
        var (c, shaft, head) = await OpenAsync();
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        Assert.True((await p.Service.WritePropertiesAsync(p.WorkRoot, shaft, new[] { new CadPropertyChange("", "Материал", "Сталь 45") })).Success);

        var r = await c.Service.WritePropertiesAsync(c.WorkRoot, shaft, new[] { new CadPropertyChange("", "Разработал", "Иванов") });

        Assert.False(r.Success);
        Assert.Contains("Обновите", r.Message);
        Assert.Equal(2, _env.Server.Doc(shaft).Latest.Number);
    }
}
