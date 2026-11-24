using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

/// <summary>
/// Доработки ядра под «окно изделия»: постановка с чертежами из подпапок головной папки, открытие с чертежами и дерево
/// с чертежами под моделями, документы другого проекта в подпапке проекта, копии документа в других рабочих папках, профиль.
/// </summary>
public sealed class ProductWindowTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static async Task<List<string>> RefsOf(string path) =>
        (await new TestCadAdapter().ReadAsync(path)).References.Select(r => r.SavedPath).ToList();

    [Fact]
    public async Task Import_FindsDrawingsInSubfoldersOfHeadFolder_WithoutExtraFolders()
    {
        var src = _env.CreateSourceProject();
        TestCadWriter.Write(Path.Combine(src, @"Прочее\Лишняя.tcprt"), new TestCadDocument().Prop("Обозначение", "ЛИШ.001"));
        var iv = _env.For("ivanov");

        using var report = await iv.Service.PrepareImportAsync(Path.Combine(src, "Изделие.tcasm"));
        var included = report.IncludedDocuments.Select(d => d.RelPath).OrderBy(p => p).ToList();
        Assert.Equal(new[]
        {
            "Детали/Болт.tcprt", "Детали/Вал.tcprt", "Детали/Корпус.tcprt", "Изделие.tcasm", "Сборки/Узел.tcasm",
            "Чертежи/Изделие.tcdrw", "Чертежи/Корпус.tcdrw",
        }.OrderBy(p => p), included);
        Assert.All(report.IncludedDocuments.Where(d => d.Kind == DocumentKind.Drawing), d => Assert.True(d.IsExtra));
        // Только документы, ссылающиеся на комплект: чужой чертёж и несвязанная деталь не попадают.
        Assert.DoesNotContain(report.Documents, d => d.FileName == "Лишняя.tcprt" && d.IsIncluded);
        Assert.DoesNotContain(report.Documents, d => d.FileName == "Чужой.tcdrw" && d.IsIncluded);

        var result = await iv.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка", allowMissing: false);
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        Assert.Equal(7, result.Response!.Items.Count);
    }

    [Fact]
    public async Task Open_WithDrawings_PlacesDrawingsRewritesTheirReferences_TreeShowsThemUnderModels()
    {
        var (head, _) = await _env.ImportSourceAsync(_env.For("ivanov"));
        var bodyDrawing = _env.DocId("Чертежи/Корпус.tcdrw");
        var headDrawing = _env.DocId("Чертежи/Изделие.tcdrw");
        var body = _env.DocId("Детали/Корпус.tcprt");
        Directory.Move(_env.Path("src"), _env.Path("src-unavailable"));
        var p = _env.For("petrov");

        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, head, includeDrawings: true);
        Assert.Equal(7, plan.Items.Count);
        Assert.Equal(new[] { bodyDrawing, headDrawing }.Order(), plan.Items.Where(i => i.IsAttachedDrawing).Select(i => i.DocumentId).Order());
        Assert.DoesNotContain(plan.Problems, x => x.Code == LocalProblemCodes.DrawingOtherModelVersion);
        var r = await p.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(r.Success, r.Message);
        Assert.True(r.CadReport!.Ok, string.Join("\n", r.CadReport.Problems));
        Assert.Contains("Чертежи/Корпус.tcdrw", r.ReferencesRewritten);
        Assert.Contains("Чертежи/Изделие.tcdrw", r.ReferencesRewritten);
        Assert.Equal(new[] { p.File("Детали/Корпус.tcprt") }, await RefsOf(p.File("Чертежи/Корпус.tcdrw")));
        Assert.Equal(new[] { p.File("Изделие.tcasm") }, await RefsOf(p.File("Чертежи/Изделие.tcdrw")));
        Assert.True(Client.IsReadOnly(p.File("Чертежи/Корпус.tcdrw")));
        Assert.Equal(7, p.State.Entries.Count);

        var tree = await p.Service.GetKitTreeAsync(head, 1, p.WorkRoot, includeDrawings: true);
        var headDrw = tree.Children.Single(n => n.IsDrawing);
        Assert.Equal(headDrawing, headDrw.DocumentId);
        Assert.True(headDrw.IsAttachedDrawing);
        Assert.Equal(head, headDrw.DrawsDocumentId);
        Assert.Empty(headDrw.Children); // модель под чертежом не повторяется
        Assert.StartsWith("чертёж", headDrw.StateText);
        Assert.Equal(headDrw, tree.Children.Last()); // чертежи — после обычных детей
        var bodyNode = tree.Children.Single(n => n.DocumentId == body);
        var bodyDrw = Assert.Single(bodyNode.Children);
        Assert.Equal(bodyDrawing, bodyDrw.DocumentId);
        Assert.Equal(body, bodyDrw.DrawsDocumentId);
        Assert.Equal(new[] { body }, bodyDrw.DrawsDocumentIds);
        Assert.Equal(LocalFileState.Unchanged, bodyDrw.Local);

        var plain = await p.Service.GetKitTreeAsync(head, 1, p.WorkRoot);
        Assert.DoesNotContain(Flatten(plain), n => n.IsDrawing);
    }

    [Fact]
    public async Task Open_WithDrawings_DrawingCheckedInWithNewerModel_IsAddedAndMarked()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        var body = _env.DocId("Детали/Корпус.tcprt");
        var bodyDrawing = _env.DocId("Чертежи/Корпус.tcdrw");
        var open = await iv.Service.PrepareOpenAsync(iv.WorkRoot, head, includeDrawings: true);
        Assert.True((await iv.Service.ExecuteOpenAsync(open)).Success);

        // Корпус v2, затем чертёж v2 с привязкой к Корпусу v2. Изделие v1 по-прежнему ссылается на Корпус v1.
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "fillet 2");
        await iv.CheckinAllAsync();
        await iv.CheckOutAsync(bodyDrawing);
        await iv.CheckinAllAsync();
        Assert.Equal(2, _env.Server.Doc(bodyDrawing).Latest.Number);

        var p = _env.For("petrov");
        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, head, 1, includeDrawings: true);
        var drw = plan.Items.Single(i => i.DocumentId == bodyDrawing);
        Assert.Equal(2, drw.Version);
        Assert.Equal(1, plan.Items.Single(i => i.DocumentId == body).Version);
        var info = Assert.Single(plan.Problems, x => x.Code == LocalProblemCodes.DrawingOtherModelVersion);
        Assert.Equal(ProblemSeverity.Info, info.Severity);
        Assert.True(plan.CanExecute);
        await p.Service.ExecuteOpenAsync(plan);
        Assert.Equal(new[] { p.File("Детали/Корпус.tcprt") }, await RefsOf(p.File("Чертежи/Корпус.tcdrw")));

        var tree = await p.Service.GetKitTreeAsync(head, 1, p.WorkRoot, includeDrawings: true);
        var node = Flatten(tree).First(n => n.DocumentId == bodyDrawing);
        Assert.True(node.DrawingModelVersionMismatch);
        Assert.Contains("другой версией модели", node.StateText);
    }

    /// <summary>
    /// Изделие проекта А ссылается на сборку проекта Б (общий кокпит): документы проекта Б раскладываются в подпапку
    /// с именем проекта; открытие проверяется в TestCad; правка детали проекта Б сдаётся под её собственным путём; переименование.
    /// </summary>
    [Fact]
    public async Task ForeignProject_PlacedInProjectSubfolder_OpenVerifyCheckinRename()
    {
        var projectB = _env.Server.AddProject("Кабина: общая");
        const string folder = "Кабина_ общая";
        var iv = _env.For("ivanov");

        // Проект Б: Кокпит.tcasm → Детали/Ручка.tcprt.
        var srcB = _env.Path("srcB");
        TestCadWriter.Write(Path.Combine(srcB, @"Детали\Ручка.tcprt"), new TestCadDocument().Prop("Обозначение", "КАБ.001").Geom("knob"));
        TestCadWriter.Write(Path.Combine(srcB, "Кокпит.tcasm"), new TestCadDocument().Prop("Обозначение", "КАБ.000").Ref(Path.Combine(srcB, @"Детали\Ручка.tcprt")));
        using (var report = await iv.Service.PrepareImportAsync(Path.Combine(srcB, "Кокпит.tcasm")))
            Assert.True((await iv.Service.ExecuteImportAsync(report, projectB.Id, "Б")).Success);
        var cockpit = _env.DocId("Кокпит.tcasm");
        var knob = _env.DocId("Детали/Ручка.tcprt");
        Directory.Move(srcB, _env.Path("srcB-unavailable"));

        // Проект А: в рабочую папку проекта А берётся кокпит (подпапка проекта Б), рядом создаётся новый тренажёр.
        iv.Service.InitWorkspace(iv.WorkRoot, _env.Project.Id);
        var upd = await iv.Service.PrepareUpdateToLatestAsync(iv.WorkRoot, new[] { cockpit });
        Assert.All(upd.Items, i => Assert.Equal(folder, i.ProjectFolder));
        Assert.Equal(folder + "/Кокпит.tcasm", upd.HeadRelPath);
        Assert.True((await iv.Service.ExecuteOpenAsync(upd)).Success);
        Assert.True(File.Exists(iv.File(folder + "/Детали/Ручка.tcprt")));
        TestCadWriter.Write(iv.File("Тренажёр.tcasm"), new TestCadDocument().Prop("Обозначение", "ТР.000").Ref(iv.File(folder + "/Кокпит.tcasm")));
        var first = await iv.CheckinAllAsync("тренажёр");
        var trainer = Assert.Single(first.Published).DocumentId;
        Assert.Equal(_env.Project.Id, _env.Server.Doc(trainer).ProjectId);
        Assert.Equal("Тренажёр.tcasm", _env.Server.Doc(trainer).Latest.RelPath);
        Assert.Equal((cockpit, 1), _env.Server.Doc(trainer).Latest.Refs.Select(x => (x.Child!.Value, x.ChildVersion!.Value)).Single());

        // Другой компьютер: открытие изделия проекта А с проверкой в CAD.
        var p = _env.For("petrov");
        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, trainer);
        Assert.Equal("Тренажёр.tcasm", plan.HeadRelPath);
        var knobItem = plan.Items.Single(i => i.DocumentId == knob);
        Assert.Equal(folder + "/Детали/Ручка.tcprt", knobItem.RelPath);
        Assert.Equal("Детали/Ручка.tcprt", knobItem.DocumentRelPath);
        Assert.Null(plan.Items.Single(i => i.DocumentId == trainer).ProjectFolder);
        var r = await p.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(r.Success, r.Message);
        Assert.True(r.CadReport!.Ok, string.Join("\n", r.CadReport.Problems));
        Assert.Contains(r.CadReport.Configurations[0].Components, c => c.LoadedPath == p.File(folder + "/Детали/Ручка.tcprt"));
        var knobEntry = p.State.FindByDocument(knob)!;
        Assert.Equal(folder, knobEntry.ProjectFolder);
        Assert.Equal("Детали/Ручка.tcprt", knobEntry.DocumentRelPath);
        Assert.Equal(new[] { p.File(folder + "/Кокпит.tcasm") }, await RefsOf(p.File("Тренажёр.tcasm")));

        var status = await p.Service.GetStatusAsync(p.WorkRoot);
        Assert.All(status.Files, f => Assert.Equal(LocalFileState.Unchanged, f.Local));
        Assert.Equal(folder, status.FindByDocument(knob)!.ProjectFolder);

        // Правка детали проекта Б и сдача: путь документа — собственный, проект — Б.
        await p.CheckOutAsync(knob);
        TestCadWriter.AppendGeometry(p.File(folder + "/Детали/Ручка.tcprt"), "правка");
        var checkinPlan = await p.Service.PrepareCheckinAsync(p.WorkRoot);
        var cand = Assert.Single(checkinPlan.Included);
        Assert.Equal(folder + "/Детали/Ручка.tcprt", cand.RelPath);
        Assert.Equal("Детали/Ручка.tcprt", cand.DocumentRelPath);
        var ci = await p.Service.ExecuteCheckinAsync(checkinPlan, "правка ручки");
        Assert.True(ci.Success, ci.Message + string.Join("\n", ci.Problems));
        Assert.Equal("Детали/Ручка.tcprt", _env.Server.Doc(knob).Latest.RelPath);
        Assert.Equal(2, _env.Server.Doc(knob).Latest.Number);
        Assert.Equal(projectB.Id, _env.Server.Doc(knob).ProjectId);
        knobEntry = p.State.FindByDocument(knob)!;
        Assert.Equal(folder, knobEntry.ProjectFolder);
        Assert.Equal(2, knobEntry.Version);
        Assert.All((await p.Service.GetStatusAsync(p.WorkRoot)).Files, f => Assert.Equal(LocalFileState.Unchanged, f.Local));

        // Обновить кокпит до последней — без конфликтов «документ лежит под другим путём».
        await p.CheckOutAsync(cockpit);
        Assert.Contains((await p.Service.PrepareCheckinAsync(p.WorkRoot)).Included, c => c.DocumentId == cockpit); // новая привязка к Ручке v2
        await p.CheckinAllAsync("кокпит на ручку v2");
        Assert.Equal("Кокпит.tcasm", _env.Server.Doc(cockpit).Latest.RelPath);

        // Переименование детали проекта Б: новый путь задаётся без подпапки проекта.
        var rename = await p.Service.PrepareRenameAsync(p.WorkRoot, folder + "/Детали/Ручка.tcprt", "Детали/Ручка-2.tcprt");
        Assert.True(rename.CanExecute, string.Join("\n", rename.Problems));
        Assert.Equal(folder + "/Детали/Ручка-2.tcprt", rename.NewRelPath);
        Assert.Equal("Детали/Ручка-2.tcprt", rename.NewDocumentRelPath);
        var parent = Assert.Single(rename.Parents);
        Assert.Equal(folder + "/Кокпит.tcasm", parent.RelPath);
        Assert.Equal("Кокпит.tcasm", parent.DocumentRelPath);
        var renamed = await p.Service.ExecuteRenameAsync(rename);
        Assert.True(renamed.Success, renamed.Message + string.Join("\n", renamed.Problems));
        Assert.Equal("Детали/Ручка-2.tcprt", _env.Server.Doc(knob).Latest.RelPath);
        Assert.Equal("Кокпит.tcasm", _env.Server.Doc(cockpit).Latest.RelPath);
        Assert.True(File.Exists(p.File(folder + "/Детали/Ручка-2.tcprt")));
        Assert.Equal(folder, p.State.FindByDocument(cockpit)!.ProjectFolder);
        Assert.Equal(new[] { p.File(folder + "/Детали/Ручка-2.tcprt") }, await RefsOf(p.File(folder + "/Кокпит.tcasm")));
    }

    [Fact]
    public void ProjectFolderName_ReplacesInvalidCharacters()
    {
        Assert.Equal("Кабина_ общая", WorkspaceService.ProjectFolderName("Кабина: общая"));
        Assert.Equal("А_Б_В", WorkspaceService.ProjectFolderName("А/Б\\В"));
        Assert.Equal("Проект", WorkspaceService.ProjectFolderName(" ..  "));
        Assert.Equal("_CON", WorkspaceService.ProjectFolderName("CON"));
        Assert.Equal("Библиотека", WorkspaceService.ProjectFolderName("Библиотека"));
        Assert.Equal("Б/Детали/Ручка.tcprt", WorkspaceService.LocalRelPath("Б", "Детали/Ручка.tcprt"));
        Assert.Equal("Детали/Ручка.tcprt", WorkspaceService.StripProjectFolder("Б", "Б/Детали/Ручка.tcprt"));
        Assert.Equal("Детали/Ручка.tcprt", WorkspaceService.StripProjectFolder(null, "Детали/Ручка.tcprt"));
    }

    [Fact]
    public async Task FindOtherWorkspaceCopies_ReportsCopiesAndLocalEdits()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        var iv2 = _env.For("ivanov", workspace: "ws-ivanov-2");
        await iv.OpenAsync(head);
        await iv2.OpenAsync(head);
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);
        TestCadWriter.AppendGeometry(iv.File("Детали/Корпус.tcprt"), "правка в первой папке");
        var known = new[] { iv.WorkRoot, iv2.WorkRoot, iv2.WorkRoot + "\\", _env.Path("не-рабочая"), _env.Path("нет-такой") };
        Directory.CreateDirectory(_env.Path("не-рабочая"));

        var fromSecond = WorkspaceService.FindOtherWorkspaceCopies(body, iv2.WorkRoot, known);
        var copy = Assert.Single(fromSecond);
        Assert.Equal(iv.WorkRoot, copy.WorkRoot);
        Assert.Equal("Детали/Корпус.tcprt", copy.RelPath);
        Assert.True(copy.Exists);
        Assert.True(copy.IsModified);
        Assert.True(copy.LockedByMe);
        Assert.Contains("несданные правки", copy.Message);

        var fromFirst = WorkspaceService.FindOtherWorkspaceCopies(body, iv.WorkRoot, known);
        var other = Assert.Single(fromFirst);
        Assert.Equal(iv2.WorkRoot, other.WorkRoot);
        Assert.False(other.IsModified);
        Assert.False(other.LockedByMe);

        File.SetAttributes(iv2.File("Детали/Корпус.tcprt"), FileAttributes.Normal);
        File.Delete(iv2.File("Детали/Корпус.tcprt"));
        Assert.False(Assert.Single(WorkspaceService.FindOtherWorkspaceCopies(body, iv.WorkRoot, known)).Exists);
        Assert.Empty(WorkspaceService.FindOtherWorkspaceCopies(Guid.NewGuid(), iv.WorkRoot, known));
    }

    [Fact]
    public void ClientProfile_PinnedAndRecentProducts_SurviveSaveAndLoad()
    {
        var profile = new ClientProfile();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        profile.Pin(a, "ТР.000", "Тренажёр");
        profile.Pin(b, "КАБ.000", null);
        profile.Pin(a, "ТР.000", "Тренажёр-1"); // повторно — обновление, без дубля
        Assert.Equal(new[] { a, b }, profile.PinnedProducts.Select(p => p.DocumentId));
        Assert.Equal("Тренажёр-1", profile.PinnedProducts[0].Name);
        Assert.True(profile.Unpin(b));
        Assert.False(profile.Unpin(b));
        Assert.False(profile.IsPinned(b));

        var ws1 = _env.Path("ws1");
        var ws2 = _env.Path("ws2");
        profile.RememberProduct(a, ws1, "ТР.000", "Тренажёр", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        profile.RememberProduct(b, ws2, "КАБ.000", "Кабина");
        profile.RememberProduct(a, ws1 + "\\"); // та же пара — поднимается наверх, обозначение сохраняется
        Assert.Equal(new[] { (a, ws1), (b, ws2) }, profile.RecentProducts.Select(r => (r.DocumentId, r.WorkRoot)));
        Assert.Equal("ТР.000", profile.RecentProducts[0].Designation);
        Assert.True(profile.RecentProducts[0].At > new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(ws1, profile.LastWorkspace);
        Assert.Equal("Тренажёр", profile.PinnedProducts.Single().Name); // имя закреплённого обновлено
        for (var i = 0; i < ClientProfile.MaxRecentProducts + 5; i++) profile.RememberProduct(Guid.NewGuid(), ws2);
        Assert.Equal(ClientProfile.MaxRecentProducts, profile.RecentProducts.Count);

        var path = _env.Path("profile.json");
        ClientProfileStore.Save(profile, path);
        var loaded = ClientProfileStore.Load(path);
        Assert.Equal(a, loaded.PinnedProducts.Single().DocumentId);
        Assert.Equal(profile.RecentProducts.Select(r => (r.DocumentId, r.WorkRoot, r.At)), loaded.RecentProducts.Select(r => (r.DocumentId, r.WorkRoot, r.At)));
        Assert.True(loaded.ForgetProduct(loaded.RecentProducts[0].DocumentId));
    }

    private static IEnumerable<KitTreeNode> Flatten(KitTreeNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
