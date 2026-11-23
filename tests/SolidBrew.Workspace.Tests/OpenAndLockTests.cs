using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

public class OpenTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Open_OnOtherComputer_PlacesKitReadOnly_ReopenDownloadsNothing()
    {
        var (head, _) = await _env.ImportSourceAsync(_env.For("ivanov"));
        Directory.Move(_env.Path("src"), _env.Path("src-unavailable")); // «нет доступа к исходной папке»
        var p = _env.For("petrov");

        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, head);
        Assert.True(plan.CreatesWorkspace);
        Assert.Equal(5, plan.Items.Count);
        Assert.Equal(5, plan.DownloadCount);
        Assert.False(plan.HasConflicts);
        var r = await p.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(r.Success);
        Assert.Equal(5, r.Downloaded);
        Assert.True(r.CadReport!.Ok, string.Join("\n", r.CadReport.Problems));
        Assert.Equal(new[] { "Исп1", "Исп2" }, r.CadReport.Configurations.Select(c => c.Name));
        Assert.All(r.CadReport.Configurations, c => Assert.Equal(4, c.Components.Count));

        foreach (var e in p.State.Entries)
        {
            Assert.True(Client.IsReadOnly(p.File(e.RelPath)), e.RelPath);
            Assert.Equal(e.Sha256, Client.Hash(p.File(e.RelPath)));
        }
        var asm = p.State.FindByPath("Изделие.tcasm")!;
        Assert.Equal(3, asm.Deps.Count);

        // Повторное открытие: ничего не скачивается и не перезаписывается.
        var downloads = _env.Server.DownloadCount;
        var plan2 = await p.Service.PrepareOpenAsync(p.WorkRoot, head);
        Assert.All(plan2.Items, i => Assert.Equal(OpenAction.AlreadyInPlace, i.Action));
        var r2 = await p.Service.ExecuteOpenAsync(plan2);
        Assert.Empty(r2.Placed);
        Assert.Equal(downloads, _env.Server.DownloadCount);

        // Открытие в другую папку с тем же кэшем — из кэша.
        var p2 = _env.For("petrov", workspace: "ws-petrov-2");
        var r3 = await p2.OpenAsync(head);
        Assert.Equal(0, r3.Downloaded);
        Assert.Equal(downloads, _env.Server.DownloadCount);
    }

    [Fact]
    public async Task Open_OtherVersion_AsksToReplace_LocalChangesAndUnknownFilesKept()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var body = _env.DocId("Детали/Корпус.tcprt");
        var shaft = _env.DocId("Детали/Вал.tcprt");
        await iv.CheckOutAsync(body, shaft);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "fillet 2");
        TestCadWriter.AppendGeometry(iv.File("Детали/Вал.tcprt"), "chamfer 1");
        await iv.CheckinAllAsync(); // Корпус v2, Вал v2
        await iv.CheckOutAsync(_env.DocId("Изделие.tcasm"), _env.DocId("Сборки/Узел.tcasm"));
        await iv.CheckinAllAsync(); // Изделие v2 и Узел v2 (новая привязка к v2 деталей)
        Assert.Equal(2, _env.Server.Doc(head).Latest.Number);

        var p = _env.For("petrov");
        await p.OpenAsync(head, version: 1);
        Assert.Equal(1, p.State.FindByPath("Детали/Корпус.tcprt")!.Version);

        // Локальная правка Вала (снят атрибут вручную) и неучтённый файл на месте будущего документа.
        var shaftPath = p.File("Детали/Вал.tcprt");
        File.SetAttributes(shaftPath, FileAttributes.Normal);
        TestCadWriter.AppendGeometry(shaftPath, "моя несданная правка");
        var editedShaft = File.ReadAllBytes(shaftPath);

        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, head, 2);
        var bodyItem = plan.Items.Single(i => i.RelPath == "Детали/Корпус.tcprt");
        var c1 = Assert.Single(bodyItem.Conflicts);
        Assert.Equal(OpenConflictKind.OtherVersion, c1.Kind);
        Assert.Equal(1, c1.LocalVersion);
        Assert.Equal(2, c1.NewVersion);
        Assert.Contains("Заменить на v2", c1.Message);
        Assert.Equal(ConflictResolution.Replace, c1.Resolution);

        var shaftItem = plan.Items.Single(i => i.RelPath == "Детали/Вал.tcprt");
        var c2 = Assert.Single(shaftItem.Conflicts);
        Assert.Equal(OpenConflictKind.LocalChanges, c2.Kind);
        Assert.Equal(ConflictResolution.Keep, c2.Resolution);

        var r = await p.Service.ExecuteOpenAsync(plan);
        Assert.Contains("Детали/Вал.tcprt", r.Kept);
        Assert.Equal(editedShaft, File.ReadAllBytes(shaftPath));
        Assert.Equal(1, p.State.FindByPath("Детали/Вал.tcprt")!.Version);
        Assert.Equal(2, p.State.FindByPath("Детали/Корпус.tcprt")!.Version);
        Assert.Equal(_env.Server.Doc(body).Latest.Sha256, Client.Hash(p.File("Детали/Корпус.tcprt")));

        // Неучтённый файл с тем же путём: по умолчанию оставляется.
        var q = _env.For("petrov", workspace: "ws-q");
        Directory.CreateDirectory(q.File("Детали"));
        File.WriteAllText(q.File("Детали/Болт.tcprt"), "чужой файл");
        var plan3 = await q.Service.PrepareOpenAsync(q.WorkRoot, head);
        var bolt = plan3.Items.Single(i => i.RelPath == "Детали/Болт.tcprt");
        Assert.Equal(OpenConflictKind.UnknownFile, Assert.Single(bolt.Conflicts).Kind);
        await q.Service.ExecuteOpenAsync(plan3);
        Assert.Equal("чужой файл", File.ReadAllText(q.File("Детали/Болт.tcprt")));
        Assert.Null(q.State.FindByPath("Детали/Болт.tcprt"));
    }

    /// <summary>Изделие v2 на сервере: Узел v1 (→ Болт v1) и напрямую Болт v2 — документ в комплекте в двух версиях.</summary>
    private async Task<(Client Iv, Guid Head, Guid Bolt)> TwoVersionKitAsync()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var bolt = _env.DocId("Детали/Болт.tcprt");
        await iv.CheckOutAsync(bolt);
        TestCadWriter.AppendGeometry(iv.File("Детали/Болт.tcprt"), "длиннее");
        await iv.CheckinAllAsync(); // Болт v2

        var asm = _env.Server.Doc(head);
        var v1 = asm.Latest;
        var v2 = new FakeVersion { Number = 2, Sha256 = v1.Sha256, Size = v1.Size, RelPath = v1.RelPath, Cad = v1.Cad, Kind = v1.Kind, Author = v1.Author, Properties = v1.Properties };
        v2.Refs.Add(new FakeRef(@"Сборки\Узел.tcasm", _env.DocId("Сборки/Узел.tcasm"), 1));
        v2.Refs.Add(new FakeRef(@"Детали\Корпус.tcprt", _env.DocId("Детали/Корпус.tcprt"), 1));
        v2.Refs.Add(new FakeRef(@"Детали\Болт.tcprt", bolt, 2));
        asm.Versions.Add(v2);
        return (iv, head, bolt);
    }

    [Fact]
    public async Task KitTree_ShowsStatesAndVersionConflict()
    {
        var (iv, head, bolt) = await TwoVersionKitAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка");
        var p = _env.For("petrov");
        await p.Api.LockAsync(_env.DocId("Детали/Вал.tcprt"));

        var tree = await iv.Service.GetKitTreeAsync(head, 2, iv.WorkRoot);

        Assert.Equal("АБВ.000", tree.Designation);
        Assert.Equal(2, tree.Version);
        var boltNode = tree.Children.Single(n => n.FileName == "Болт.tcprt");
        Assert.True(boltNode.HasVersionConflict);
        Assert.Equal(2, boltNode.Version);
        Assert.Equal(2, boltNode.LocalVersion);
        Assert.Contains("в нескольких версиях", boltNode.StateText);
        var unitNode = tree.Children.Single(n => n.FileName == "Узел.tcasm");
        var unitBolt = unitNode.Children.Single(n => n.FileName == "Болт.tcprt");
        Assert.Equal(1, unitBolt.Version);
        Assert.True(unitBolt.IsOutdated);
        Assert.Contains("в папке v2", unitBolt.StateText);
        var shaft = unitNode.Children.Single(n => n.FileName == "Вал.tcprt");
        Assert.Equal(LockState.LockedByOther, shaft.Lock);
        Assert.Contains("Петров", shaft.StateText);
        var bodyNode = tree.Children.Single(n => n.FileName == "Корпус.tcprt");
        Assert.Equal(LockState.LockedByMe, bodyNode.Lock);
        Assert.Equal(LocalFileState.ModifiedLocally, bodyNode.Local);
        Assert.Contains("изменён локально", bodyNode.StateText);
    }

    [Fact]
    public async Task Open_KitWithDocumentInTwoVersions_IsError()
    {
        var (iv, head, _) = await TwoVersionKitAsync();
        var plan = await iv.Service.PrepareOpenAsync(iv.WorkRoot, head, 2);
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.VersionConflict && p.Message.Contains("v1 и v2"));
        var r = await iv.Service.ExecuteOpenAsync(plan);
        Assert.False(r.Success);
    }
}

public class LockAndStatusTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private async Task<(Client Iv, Guid Head)> SetupAsync()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        return (iv, head);
    }

    [Fact]
    public async Task CheckOut_ClearsReadOnlyOnlyAfterServerLock()
    {
        var (iv, _) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        var path = iv.File("Детали/Корпус.tcprt");

        iv.Api.Offline = true;
        var failed = await iv.Service.CheckOutAsync(iv.WorkRoot, new[] { body });
        Assert.False(failed.Success);
        Assert.True(Client.IsReadOnly(path));
        Assert.False(iv.State.FindByDocument(body)!.LockedByMe);
        iv.Api.Offline = false;

        await iv.CheckOutAsync(body);
        Assert.False(Client.IsReadOnly(path));
        Assert.True(iv.State.FindByDocument(body)!.LockedByMe);
        Assert.Equal("ivanov", _env.Server.Doc(body).Lock!.UserLogin);
    }

    [Fact]
    public async Task CheckOut_LockedByOther_ShowsWhoAndSince_FileStaysReadOnly()
    {
        var (iv, head) = await SetupAsync();
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);

        var r = await p.Service.CheckOutAsync(p.WorkRoot, new[] { body });
        Assert.False(r.Success);
        var item = Assert.Single(r.Items);
        Assert.Equal("ivanov", item.LockedBy!.UserLogin);
        Assert.Contains("Иванов", item.Message);
        Assert.Contains(" с ", item.Message);
        Assert.True(Client.IsReadOnly(p.File("Детали/Корпус.tcprt")));

        // Разные детали одной сборки — можно одновременно.
        await p.CheckOutAsync(_env.DocId("Детали/Вал.tcprt"));
    }

    [Fact]
    public async Task CheckOut_NotLatestLocally_Refused()
    {
        var (iv, head) = await SetupAsync();
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка");
        await iv.CheckinAllAsync();

        var r = await p.Service.CheckOutAsync(p.WorkRoot, new[] { body });
        Assert.False(r.Success);
        Assert.Equal(2, r.Items[0].LatestVersion);
        Assert.Contains("сначала обновите документ до v2", r.Items[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_env.Server.Doc(body).Lock);
        Assert.True(Client.IsReadOnly(p.File("Детали/Корпус.tcprt")));
    }

    [Fact]
    public async Task Status_ReportsLocalAndServerStates()
    {
        var (iv, head) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка");
        TestCadWriter.Create(iv.File("Детали/Новая.tcprt"));
        File.WriteAllText(iv.File("readme.txt"), "не CAD");
        File.SetAttributes(iv.File("Детали/Вал.tcprt"), FileAttributes.Normal);
        File.Delete(iv.File("Детали/Вал.tcprt"));
        var p = _env.For("petrov");
        await p.OpenAsync(head);
        await p.CheckOutAsync(_env.DocId("Детали/Болт.tcprt"));

        var s = await iv.Service.GetStatusAsync(iv.WorkRoot);
        Assert.True(s.ServerChecked);
        var b = s.FindByPath("Детали/Корпус.tcprt")!;
        Assert.Equal(LocalFileState.ModifiedLocally, b.Local);
        Assert.Equal(LockState.LockedByMe, b.Lock);
        Assert.Equal(LocalFileState.Draft, s.FindByPath("Детали/Новая.tcprt")!.Local);
        Assert.Null(s.FindByPath("readme.txt"));
        Assert.Equal(LocalFileState.MissingLocally, s.FindByPath("Детали/Вал.tcprt")!.Local);
        var bolt = s.FindByPath("Детали/Болт.tcprt")!;
        Assert.Equal(LockState.LockedByOther, bolt.Lock);
        Assert.Equal("Петров П. П.", bolt.LockInfo!.UserDisplayName);
        Assert.Equal(LocalFileState.Unchanged, s.FindByPath("Изделие.tcasm")!.Local);
        Assert.Equal(1, s.FindByPath("Изделие.tcasm")!.LatestVersion);
    }

    [Fact]
    public async Task ForcedUnlock_FileBecomesReadOnly_EditsKept_Message()
    {
        var (iv, _) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        var path = iv.File("Детали/Корпус.tcprt");
        TestCadWriter.AppendGeometry(path, "несданная правка");
        var edited = File.ReadAllBytes(path);

        await _env.Server.Connect("admin").ForceUnlockAsync(body);

        var s = await iv.Service.GetStatusAsync(iv.WorkRoot);
        var f = s.FindByPath("Детали/Корпус.tcprt")!;
        Assert.True(f.LockRemovedByOthers);
        Assert.Equal(LocalFileState.ModifiedLocally, f.Local);
        Assert.True(Client.IsReadOnly(path));
        Assert.Equal(edited, File.ReadAllBytes(path));
        Assert.False(iv.State.FindByDocument(body)!.LockedByMe);
        Assert.Contains(s.Messages, m => m.Contains("сняли принудительно") && m.Contains("Корпус"));

        // Сдать без блокировки нельзя: документ не взят мной — не кандидат.
        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        Assert.Empty(plan.Items);
        // Можно снова взять (версия последняя) — правки становятся кандидатом.
        await iv.CheckOutAsync(body);
        var plan2 = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        Assert.True(Assert.Single(plan2.Included).ContentChanged);
    }

    [Fact]
    public async Task UndoCheckOut_KeepsEditsByDefault_RevertRestores()
    {
        var (iv, _) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        var shaft = _env.DocId("Детали/Вал.tcprt");
        await iv.CheckOutAsync(body, shaft);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка 1");
        TestCadWriter.AppendGeometry(iv.File("Детали/Вал.tcprt"), "правка 2");
        var edited = File.ReadAllBytes(iv.File("Детали/Корпус.tcprt"));

        var plan = await iv.Service.PrepareUndoCheckOutAsync(iv.WorkRoot, new[] { body, shaft });
        Assert.True(plan.HasLocalChanges);
        Assert.NotNull(plan.Warning);
        plan.Items.Single(i => i.DocumentId == shaft).Revert = true;
        var r = await iv.Service.ExecuteUndoCheckOutAsync(plan);
        Assert.True(r.Success);

        Assert.Null(_env.Server.Doc(body).Lock);
        Assert.Null(_env.Server.Doc(shaft).Lock);
        Assert.Equal(edited, File.ReadAllBytes(iv.File("Детали/Корпус.tcprt")));
        Assert.True(Client.IsReadOnly(iv.File("Детали/Корпус.tcprt")));
        Assert.Equal(_env.Server.Doc(shaft).Latest.Sha256, Client.Hash(iv.File("Детали/Вал.tcprt")));
        Assert.True(Client.IsReadOnly(iv.File("Детали/Вал.tcprt")));
        Assert.Equal(new[] { "Детали/Вал.tcprt" }, r.Reverted);
    }
}
