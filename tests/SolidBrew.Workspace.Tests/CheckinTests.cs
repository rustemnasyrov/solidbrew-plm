using System.IO.Compression;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

public class CheckinTests : IDisposable
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
    public async Task ChangeOnePart_OnlyItGetsNewVersion()
    {
        var (iv, head) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        var v1Sha = iv.State.FindByDocument(body)!.Sha256;
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "отверстие 8");
        Assert.True(await iv.Cache.VerifyAsync(v1Sha)); // правка в папке не портит кэш (копия, не жёсткая ссылка)

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var c = Assert.Single(plan.Items);
        Assert.True(c.Include);
        Assert.True(c.ContentChanged);
        Assert.False(c.ReferencesChanged);
        Assert.Equal(1, c.BaseVersion);
        var r = await iv.Service.ExecuteCheckinAsync(plan, "отверстие");
        Assert.True(r.Success, r.Message);

        Assert.Equal(2, _env.Server.Doc(body).Latest.Number);
        Assert.Equal("отверстие", _env.Server.Doc(body).Latest.Comment);
        Assert.Equal(1, _env.Server.Doc(head).Latest.Number);
        Assert.Null(_env.Server.Doc(body).Lock);
        var e = iv.State.FindByDocument(body)!;
        Assert.Equal(2, e.Version);
        Assert.False(e.LockedByMe);
        Assert.True(Client.IsReadOnly(iv.File("Детали/Корпус.tcprt")));
        Assert.True(iv.Cache.Contains(e.Sha256));
        Assert.Equal(LocalFileState.Unchanged, (await iv.Service.GetStatusAsync(iv.WorkRoot)).FindByDocument(body)!.Local);
    }

    [Fact]
    public async Task OnlyDrawingChanged_ModelVersionUnchanged()
    {
        var (iv, _) = await SetupAsync();
        var drw = _env.DocId("Чертежи/Корпус.tcdrw");
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.OpenAsync(drw); // чертёж в ту же папку; модель уже на месте
        Assert.Single(iv.State.FindByDocument(drw)!.Deps);
        await iv.CheckOutAsync(drw);
        TestCadWriter.AppendGeometry(iv.File("Чертежи/Корпус.tcdrw"), "размер 20");
        await iv.CheckinAllAsync();

        Assert.Equal(2, _env.Server.Doc(drw).Latest.Number);
        Assert.Equal(1, _env.Server.Doc(body).Latest.Number);
        Assert.Equal(1, _env.Server.Doc(drw).Latest.Refs.Single().ChildVersion);
    }

    [Fact]
    public async Task UpdateDependency_NewBindingWithoutByteChange_NewAssemblyVersion_WhereUsedShowsBoth()
    {
        var (iv, head) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        // Петров выпускает Корпус v2.
        var p = _env.For("petrov");
        await p.OpenAsync(body);
        await p.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(p.File("Детали/Корпус.tcprt"), "ребро");
        await p.CheckinAllAsync();
        Assert.Equal(1, _env.Server.Doc(head).Latest.Number); // старая сборка не изменилась

        // Иванов: взять сборку, обновить деталь до последней, сдать сборку.
        await iv.CheckOutAsync(head);
        var upd = await iv.Service.PrepareUpdateToLatestAsync(iv.WorkRoot, new[] { body });
        Assert.Equal(OpenConflictKind.OtherVersion, upd.Items.Single(i => i.DocumentId == body).Conflicts.Single().Kind);
        await iv.Service.ExecuteOpenAsync(upd);
        var asmSha = Client.Hash(iv.File("Изделие.tcasm"));

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var c = Assert.Single(plan.Included);
        Assert.Equal("Изделие.tcasm", c.RelPath);
        Assert.False(c.ContentChanged);
        Assert.True(c.ReferencesChanged);
        var r = await iv.Service.ExecuteCheckinAsync(plan, "обновлён корпус");
        Assert.True(r.Success, r.Message);

        var v2 = _env.Server.Doc(head).Latest;
        Assert.Equal(2, v2.Number);
        Assert.Equal(asmSha, v2.Sha256);
        Assert.Equal(2, v2.Refs.Single(x => x.Child == body).ChildVersion);
        Assert.Equal(1, _env.Server.Doc(head).Versions[0].Refs.Single(x => x.Child == body).ChildVersion);

        var wu = await iv.Service.GetWhereUsedAsync(body, 2);
        Assert.Contains(wu.ThisVersion, x => x.ParentDocumentId == head && x.ParentVersion == 2 && x.IsParentLatest);
        Assert.Contains(wu.OtherVersions, x => x.ParentDocumentId == head && x.ParentVersion == 1 && !x.UsesLatestChild);
        Assert.Contains(wu.NotLatest, x => x.ParentVersion == 1);
        Assert.Equal(2, iv.State.FindByDocument(head)!.Deps.Single(d => d.DocumentId == body).Version);
    }

    [Fact]
    public async Task Drafts_NewPartReferencedByAssembly_PublishedTogether()
    {
        var (iv, head) = await SetupAsync();
        await iv.CheckOutAsync(head);
        TestCadWriter.Write(iv.File("Детали/Новая.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.003").Geom("plate"));
        File.AppendAllText(iv.File("Изделие.tcasm"), "ref: Детали\\Новая.tcprt\r\n");

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var draft = plan.Items.Single(i => i.IsDraft);
        Assert.Equal("Детали/Новая.tcprt", draft.TempKey);
        Assert.True(draft.Include);
        Assert.Equal("АБВ.003", draft.Designation);

        draft.Include = false;
        plan.Revalidate();
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.DraftNotIncluded);
        draft.Include = true;
        plan.Revalidate();
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));

        var r = await iv.Service.ExecuteCheckinAsync(plan, "новая деталь");
        Assert.True(r.Success, r.Message);
        var newDoc = _env.Server.DocByPath("Детали/Новая.tcprt");
        Assert.Equal(1, newDoc.Latest.Number);
        var asm = _env.Server.Doc(head).Latest;
        Assert.Equal(2, asm.Number);
        Assert.Equal(1, asm.Refs.Single(x => x.Child == newDoc.Id).ChildVersion);
        Assert.Equal(newDoc.Id, iv.State.FindByPath("Детали/Новая.tcprt")!.DocumentId);
        Assert.True(Client.IsReadOnly(iv.File("Детали/Новая.tcprt")));
    }

    [Fact]
    public async Task AmbiguousOrMissingReference_Blocks()
    {
        var (iv, head) = await SetupAsync();
        await iv.CheckOutAsync(head);
        TestCadWriter.Create(iv.File("A/Дубль.tcprt"));
        TestCadWriter.Create(iv.File("B/Дубль.tcprt"), geometry: "другой");
        File.AppendAllText(iv.File("Изделие.tcasm"), "ref: Q:\\Чужой ПК\\Дубль.tcprt\r\nref: Q:\\Чужой ПК\\Нет.tcprt\r\n");

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var asm = plan.Items.Single(i => i.RelPath == "Изделие.tcasm");
        Assert.Equal(ReferenceResolution.Ambiguous, asm.References.Single(r => r.FileName == "Дубль.tcprt").Resolution);
        Assert.Equal(ReferenceResolution.Missing, asm.References.Single(r => r.FileName == "Нет.tcprt").Resolution);
        Assert.Equal(ReferenceResolution.ByRelativePath, asm.References.Single(r => r.FileName == "Корпус.tcprt").Resolution);
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.AmbiguousReference && p.Message.Contains("A/Дубль.tcprt"));
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.MissingReference && p.Message.Contains("Нет.tcprt"));

        var r = await iv.Service.ExecuteCheckinAsync(plan, null);
        Assert.False(r.Success);
        Assert.Empty(_env.Server.Checkins.Skip(1)); // только постановка на учёт
    }

    [Fact]
    public async Task ReferenceToModifiedButNotLockedDocument_Blocks()
    {
        var (iv, head) = await SetupAsync();
        await iv.CheckOutAsync(head);
        TestCadWriter.AppendGeometry(iv.File("Изделие.tcasm"), "правка");
        var bodyPath = iv.File("Детали/Корпус.tcprt");
        File.SetAttributes(bodyPath, FileAttributes.Normal);
        TestCadWriter.AppendGeometry(bodyPath, "тайная правка");

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.ChangedNotLocked && p.Message.Contains("Корпус"));
        Assert.False(plan.CanExecute);
    }

    [Fact]
    public async Task ServerFailure_NothingChangesLocally()
    {
        var (iv, head) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body, head);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка");
        TestCadWriter.AppendGeometry(iv.File("Изделие.tcasm"), "правка сборки");
        var stateBefore = File.ReadAllText(WorkspaceState.StatePath(iv.WorkRoot));
        var bytes = File.ReadAllBytes(iv.File("Детали/Корпус.tcprt"));

        _env.Server.CheckinFailure = _ => new PdmApiException(500, PdmApiException.DefaultMessage(500));
        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var r = await iv.Service.ExecuteCheckinAsync(plan, "x");
        Assert.False(r.Success);
        Assert.Contains("ничего не опубликовано", r.Message);
        Assert.NotEmpty(plan.ServerProblems);

        Assert.Equal(stateBefore, File.ReadAllText(WorkspaceState.StatePath(iv.WorkRoot)));
        Assert.Equal(bytes, File.ReadAllBytes(iv.File("Детали/Корпус.tcprt")));
        Assert.False(Client.IsReadOnly(iv.File("Детали/Корпус.tcprt")));
        Assert.Equal("ivanov", _env.Server.Doc(body).Lock!.UserLogin);
        Assert.Equal(1, _env.Server.Doc(body).Latest.Number);
        Assert.Equal(1, _env.Server.Doc(head).Latest.Number);

        // Проверка сервера (блокировку сняли между подготовкой и сдачей) — проблемы с кодами сервера, атомарно.
        _env.Server.CheckinFailure = null;
        var plan2 = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        await _env.Server.Connect("admin").ForceUnlockAsync(body);
        var r2 = await iv.Service.ExecuteCheckinAsync(plan2, "x");
        Assert.False(r2.Success);
        Assert.Contains(r2.Problems, p => p.Code == ProblemCodes.NotLocked && p.RelPath == "Детали/Корпус.tcprt");
        Assert.Equal(1, _env.Server.Doc(head).Latest.Number);
        Assert.Equal(stateBefore, File.ReadAllText(WorkspaceState.StatePath(iv.WorkRoot)));
    }

    [Fact]
    public async Task KeepLocks_FilesStayWritable()
    {
        var (iv, _) = await SetupAsync();
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка");
        TestCadWriter.Create(iv.File("Детали/Черновик.tcprt"));
        await iv.CheckinAllAsync(keepLocks: true);

        Assert.Equal("ivanov", _env.Server.Doc(body).Lock!.UserLogin);
        Assert.False(Client.IsReadOnly(iv.File("Детали/Корпус.tcprt")));
        Assert.True(iv.State.FindByDocument(body)!.LockedByMe);
        var draft = _env.Server.DocByPath("Детали/Черновик.tcprt");
        Assert.Equal("ivanov", draft.Lock!.UserLogin);
        Assert.False(Client.IsReadOnly(iv.File("Детали/Черновик.tcprt")));

        // Ещё правка — следующая версия.
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "ещё");
        await iv.CheckinAllAsync();
        Assert.Equal(3, _env.Server.Doc(body).Latest.Number);
    }

    [Fact]
    public async Task NewerCadVersion_RequiresConfirmation()
    {
        _env.Server.Settings[SettingKeys.WorkingVersion("testcad")] = "2018";
        var (iv, _) = await SetupAsync();
        TestCadWriter.Write(iv.File("Детали/Новая2022.tcprt"), new TestCadDocument { CadVersion = 2022 });

        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        Assert.True(plan.HasNewerCadVersion);
        Assert.Contains(plan.Problems, p => p.Severity == ProblemSeverity.Warning && p.Code == LocalProblemCodes.NewerCadVersion && p.Message.Contains("2018"));
        Assert.True(plan.CanExecute);
        var r = await iv.Service.ExecuteCheckinAsync(plan, null);
        Assert.False(r.Success);
        var r2 = await iv.Service.ExecuteCheckinAsync(plan, null, acceptNewerCad: true);
        Assert.True(r2.Success, r2.Message);
    }
}

public class RenameAndMiscTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Rename_SharedPart_ParentsUpdated_OldAndNewVersionsOpen()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var bolt = _env.DocId("Детали/Болт.tcprt");
        var unit = _env.DocId("Сборки/Узел.tcasm");

        var plan = await iv.Service.PrepareRenameAsync(iv.WorkRoot, "Детали/Болт.tcprt", "Детали/Крепёж/Болт М8.tcprt");
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        Assert.Equal(new[] { head, unit }.OrderBy(x => x), plan.Parents.Select(p => p.DocumentId).OrderBy(x => x));
        var r = await iv.Service.ExecuteRenameAsync(plan);
        Assert.True(r.Success, r.Message + string.Join("\n", r.Problems));

        Assert.Equal("Детали/Крепёж/Болт М8.tcprt", _env.Server.Doc(bolt).Latest.RelPath);
        Assert.Equal(2, _env.Server.Doc(bolt).Latest.Number);
        Assert.Equal(_env.Server.Doc(bolt).Versions[0].Sha256, _env.Server.Doc(bolt).Latest.Sha256);
        Assert.Equal(2, _env.Server.Doc(head).Latest.Number);
        Assert.Equal(2, _env.Server.Doc(unit).Latest.Number);
        Assert.All(new[] { head, unit, bolt }, id => Assert.Null(_env.Server.Doc(id).Lock));
        Assert.False(File.Exists(iv.File("Детали/Болт.tcprt")));
        Assert.True(File.Exists(iv.File("Детали/Крепёж/Болт М8.tcprt")));
        Assert.True(Client.IsReadOnly(iv.File("Изделие.tcasm")));
        Assert.Contains("Крепёж", File.ReadAllText(iv.File("Изделие.tcasm")));
        var st = await iv.Service.GetStatusAsync(iv.WorkRoot);
        Assert.All(st.Files, f => Assert.Equal(LocalFileState.Unchanged, f.Local));

        // Новая версия открывается и проверяется в CAD на другом компьютере.
        Directory.Move(_env.Path("src"), _env.Path("src-gone"));
        var p = _env.For("petrov");
        var open2 = await p.OpenAsync(head, verify: true);
        Assert.True(open2.CadReport!.Ok, string.Join("\n", open2.CadReport.Problems));
        Assert.True(File.Exists(p.File("Детали/Крепёж/Болт М8.tcprt")));

        // Старая версия — со старым именем.
        var q = _env.For("petrov", workspace: "ws-old");
        var open1 = await q.OpenAsync(head, version: 1, verify: true);
        Assert.True(open1.CadReport!.Ok, string.Join("\n", open1.CadReport.Problems));
        Assert.True(File.Exists(q.File("Детали/Болт.tcprt")));
    }

    [Fact]
    public async Task Rename_ParentLockedByOther_NotExecutable()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        await _env.Server.Connect("petrov").LockAsync(_env.DocId("Сборки/Узел.tcasm"));

        var plan = await iv.Service.PrepareRenameAsync(iv.WorkRoot, "Детали/Болт.tcprt", "Детали/Болт2.tcprt");
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.LockedByOther && p.Message.Contains("Петров"));
        Assert.True(plan.Parents.Single(x => x.RelPath == "Сборки/Узел.tcasm").LockedByOther);
    }

    [Fact]
    public async Task Rename_ServerFailure_LocksRolledBack_FilesUnchanged()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var plan = await iv.Service.PrepareRenameAsync(iv.WorkRoot, "Детали/Болт.tcprt", "Детали/Болт2.tcprt");
        var asmBytes = File.ReadAllBytes(iv.File("Изделие.tcasm"));
        _env.Server.CheckinFailure = _ => new PdmApiException(503, PdmApiException.DefaultMessage(503));

        var r = await iv.Service.ExecuteRenameAsync(plan);
        Assert.False(r.Success);
        Assert.DoesNotContain(_env.Server.Docs.Values, d => d.Lock != null);
        Assert.True(File.Exists(iv.File("Детали/Болт.tcprt")));
        Assert.False(File.Exists(iv.File("Детали/Болт2.tcprt")));
        Assert.Equal(asmBytes, File.ReadAllBytes(iv.File("Изделие.tcasm")));
        Assert.All(_env.Server.Docs.Values, d => Assert.Single(d.Versions));
    }

    [Fact]
    public async Task Export_MarkUnused()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        var zipPath = _env.Path("out", "комплект.zip");
        await iv.Service.ExportAsync(head, 1, includeDerived: true, zipPath);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            Assert.Contains(zip.Entries, e => e.FullName == ExportManifest.FileName);
            Assert.Contains(zip.Entries, e => e.FullName == "Сборки/Узел.tcasm");
        }
        Assert.False(File.Exists(zipPath + ".part"));

        var drw = _env.DocId("Чертежи/Изделие.tcdrw");
        var s = await iv.Service.MarkUnusedAsync(drw, true);
        Assert.Equal(DocumentStatus.Unused, s.Status);
    }
}
