using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

public class ImportTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static Dictionary<string, (string Hash, FileAttributes Attrs, DateTime Write)> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Client.Hash(f), File.GetAttributes(f), File.GetLastWriteTimeUtc(f)));

    [Fact]
    public async Task Import_StandardProject_DrawingsFoundByReferences_SourcesUntouched()
    {
        var src = _env.CreateSourceProject();
        File.SetAttributes(System.IO.Path.Combine(src, @"Детали\Вал.tcprt"), FileAttributes.ReadOnly);
        var before = Snapshot(src);
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"), new[] { System.IO.Path.Combine(src, "Чертежи") });

        var included = report.IncludedDocuments.Select(d => d.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "Детали/Болт.tcprt", "Детали/Вал.tcprt", "Детали/Корпус.tcprt", "Изделие.tcasm",
            "Сборки/Узел.tcasm", "Чертежи/Изделие.tcdrw", "Чертежи/Корпус.tcdrw",
        }, included);
        Assert.DoesNotContain(report.Documents, d => d.FileName == "Чужой.tcdrw");
        Assert.All(report.References, r => Assert.Equal(ImportReferenceCategory.Found, r.Category));
        Assert.True(report.CanExecute(false));
        Assert.All(report.Documents, d => Assert.StartsWith(System.IO.Path.Combine(c.Cache.Root, "staging"), d.StagingPath));

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка");
        Assert.True(result.Success, result.Message);
        Assert.Equal(7, _env.Server.Docs.Count);
        Assert.Equal(DocumentKind.Drawing, _env.Server.DocByPath("Чертежи/Корпус.tcdrw").Latest.Kind);
        var drw = _env.Server.DocByPath("Чертежи/Корпус.tcdrw").Latest;
        Assert.Equal(_env.DocId("Детали/Корпус.tcprt"), drw.Refs.Single().Child);
        Assert.Equal(System.IO.Path.Combine(src, @"Детали\Корпус.tcprt"), drw.Refs.Single().RefPath);
        Assert.Equal("АБВ.000", _env.Server.Summary(_env.Server.Doc(result.HeadDocumentId)).Designation);

        // Исходники не изменены: хеши, атрибуты, время.
        Assert.Equal(before, Snapshot(src));
        Assert.False(Directory.Exists(report.StagingRoot));
    }

    [Fact]
    public async Task Import_AllFiveCategories_AndDecisions()
    {
        var src = _env.Path("src");
        var outside = TestCadWriter.Create(_env.Path("lib", "Стандарт.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "Детали", "Найден.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "Детали", "Шайба.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "A", "Гайка.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "B", "Гайка.tcprt"), geometry: "другая гайка");
        var head = TestCadWriter.Create(System.IO.Path.Combine(src, "Сборка.tcasm"), new[]
        {
            System.IO.Path.Combine(src, "Детали", "Найден.tcprt"),
            outside,
            @"Q:\Старый диск\Шайба.tcprt",
            @"Q:\Старый диск\Гайка.tcprt",
            @"Q:\Старый диск\Нет.tcprt",
        });
        var before = Snapshot(_env.Root);
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(head);
        var refs = report.Head.References.ToDictionary(r => r.FileName);
        Assert.Equal(ImportReferenceCategory.Found, refs["Найден.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.OutsideFolders, refs["Стандарт.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.NameOnly, refs["Шайба.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.Ambiguous, refs["Гайка.tcprt"].Category);
        Assert.Equal(2, refs["Гайка.tcprt"].Candidates.Count);
        Assert.Equal(ImportReferenceCategory.Missing, refs["Нет.tcprt"].Category);

        // По умолчанию: включён только найденный; неоднозначность блокирует даже с allowMissing.
        Assert.Equal(new[] { "Найден.tcprt", "Сборка.tcasm" }, report.IncludedDocuments.Select(d => d.FileName).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(report.CanExecute(false));
        Assert.False(report.CanExecute(true));
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.AmbiguousReference);
        var denied = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: true);
        Assert.False(denied.Success);
        Assert.Empty(_env.Server.Docs);

        // Решения: выбрать гайку, подтвердить шайбу; «вне папок» не подтверждаем.
        refs["Гайка.tcprt"].Chosen = refs["Гайка.tcprt"].Candidates.First(p => p.Contains(@"\B\"));
        refs["Шайба.tcprt"].Accept = true;
        report.Recompute();
        Assert.Equal(new[] { "B/Гайка.tcprt", "Детали/Найден.tcprt", "Детали/Шайба.tcprt", "Сборка.tcasm" }, report.IncludedDocuments.Select(d => d.RelPath).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(report.CanExecute(false)); // отсутствующая и неподтверждённая
        Assert.True(report.CanExecute(true));

        var refused = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: false);
        Assert.False(refused.Success);
        Assert.Empty(_env.Server.Docs);

        var ok = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: true);
        Assert.True(ok.Success, ok.Message + string.Join("\n", ok.Problems));
        Assert.Equal(4, _env.Server.Docs.Count);
        var asm = _env.Server.Doc(ok.HeadDocumentId).Latest;
        Assert.Equal(2, asm.Refs.Count(r => r.Child == null)); // Стандарт (не подтверждён) и Нет
        Assert.Equal(_env.DocId("B/Гайка.tcprt"), asm.Refs.Single(r => r.RefPath.EndsWith("Гайка.tcprt")).Child);

        Assert.Equal(before, Snapshot(_env.Root).Where(p => before.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public async Task Import_UncheckedModel_NotUploaded_StaysByLink_OpensByLink()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        using var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"), new[] { System.IO.Path.Combine(src, "Чертежи") });

        var shaft = report.Documents.Single(d => d.FileName == "Вал.tcprt");
        shaft.Include = false;
        report.Recompute();
        Assert.Equal(ImportDisposition.ByLink, shaft.Disposition);
        Assert.DoesNotContain(report.NewDocuments, d => d.FileName == "Вал.tcprt");
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.ByLink && p.Severity == ProblemSeverity.Info);
        Assert.True(report.CanExecute(false)); // снятая галочка — не «отсутствующая ссылка»

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        Assert.Equal(6, _env.Server.Docs.Count);
        var node = _env.Server.DocByPath("Сборки/Узел.tcasm").Latest;
        var shaftRef = node.Refs.Single(r => r.RefPath.EndsWith("Вал.tcprt"));
        Assert.Null(shaftRef.Child);
        Assert.Equal(System.IO.Path.Combine(src, @"Детали\Вал.tcprt"), shaftRef.RefPath);

        // Открытие: вал не в PDM, CAD берёт его по ссылке из исходной папки — это не ошибка.
        var plan = await c.Service.PrepareOpenAsync(c.WorkRoot, result.HeadDocumentId);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.UnresolvedInKit && p.Severity == ProblemSeverity.Info);
        var open = await c.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(open.CadReport!.Ok, string.Join("\n", open.CadReport.Problems));
        Assert.Contains(open.CadReport.Configurations.SelectMany(x => x.Components),
            x => x.LoadedPath == System.IO.Path.Combine(src, @"Детали\Вал.tcprt"));
    }

    [Fact]
    public async Task LinkedFile_Register_Bind_Place_ThenCheckOutLikeAnyDocument()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        var shaftSource = System.IO.Path.Combine(src, @"Детали\Вал.tcprt");
        Guid headId;
        using (var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm")))
        {
            report.Documents.Single(d => d.FileName == "Вал.tcprt").Include = false;
            report.Recompute();
            var r = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
            Assert.True(r.Success, r.Message);
            headId = r.HeadDocumentId;
        }
        await c.OpenAsync(headId, verify: true);
        var nodeId = _env.DocId("Сборки/Узел.tcasm");

        // «Поставить на учёт» файл по ссылке: рядом с родителем (Сборки/), затем привязать ссылку родителя.
        using (var reg = await c.Service.PrepareImportAsync(shaftSource, null, null, default,
                   new ImportOptions { RelPathPrefix = "Сборки", AllowExistingHead = true }))
        {
            Assert.Equal("Сборки/Вал.tcprt", reg.Head.RelPath);
            var imported = await c.Service.ExecuteImportAsync(reg, _env.Project.Id, null);
            Assert.True(imported.Success, imported.Message);

            var bind = await c.Service.BindReferenceAsync(c.WorkRoot, nodeId, shaftSource, imported.HeadDocumentId, imported.HeadVersion);
            Assert.True(bind.Success, bind.Message + string.Join("\n", bind.Problems));
            Assert.False(bind.ParentModifiedLocally);
            Assert.Equal(2, bind.ParentVersion);
        }
        var shaftId = _env.DocId("Сборки/Вал.tcprt");
        var node = _env.Server.Doc(nodeId);
        Assert.Null(node.Lock); // взятие было только на время привязки
        Assert.Equal(node.Versions[0].Sha256, node.Latest.Sha256); // байты те же — новая только привязка
        Assert.Contains(node.Latest.Refs, r => r.Child == shaftId && r.ChildVersion == 1);

        // Разложить: родитель новой версией, вал — из хранилища, ссылки на рабочую папку; проверка в CAD.
        var plan = await c.Service.PrepareUpdateToLatestAsync(c.WorkRoot, new[] { nodeId });
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        var placed = await c.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(placed.Success, placed.Message);
        Assert.True(File.Exists(c.File("Сборки/Вал.tcprt")));
        Assert.True(placed.CadReport!.Ok, string.Join("\n", placed.CadReport.Problems));
        Assert.Contains(placed.CadReport.Configurations.SelectMany(x => x.Components), x => x.LoadedPath == c.File("Сборки/Вал.tcprt"));

        // Дальше — обычный документ.
        await c.CheckOutAsync(shaftId);
        Assert.False(Client.IsReadOnly(c.File("Сборки/Вал.tcprt")));
    }

    [Fact]
    public async Task Checkin_RefNotInPdm_AllowedIfWasUnresolvedOrFileExists_NewMissingBlocks()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        var shaftSource = System.IO.Path.Combine(src, @"Детали\Вал.tcprt");
        using (var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm")))
        {
            report.Documents.Single(d => d.FileName == "Вал.tcprt").Include = false;
            report.Recompute();
            var r = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
            await c.OpenAsync(r.HeadDocumentId);
        }
        var nodeId = _env.DocId("Сборки/Узел.tcasm");
        await c.CheckOutAsync(nodeId);
        var node = c.File("Сборки/Узел.tcasm");

        // 1. Файл по ссылке есть (вне рабочей папки) — сдача проходит, ссылка остаётся «по ссылке».
        TestCadWriter.AppendGeometry(node, "правка 1");
        var plan = await c.Service.PrepareCheckinAsync(c.WorkRoot);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.ByLink && p.Severity == ProblemSeverity.Info);
        var r1 = await c.Service.ExecuteCheckinAsync(plan, "правка", keepLocks: true);
        Assert.True(r1.Success, r1.Message + string.Join("\n", r1.Problems));
        Assert.Contains(_env.Server.Doc(nodeId).Latest.Refs, r => r.Child == null && r.RefPath == shaftSource);

        // 2. Файла больше нет, но ссылка была неучтённой и в сданной версии — тоже проходит.
        File.Delete(shaftSource);
        TestCadWriter.AppendGeometry(node, "правка 2");
        plan = await c.Service.PrepareCheckinAsync(c.WorkRoot);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        var r2 = await c.Service.ExecuteCheckinAsync(plan, "правка", keepLocks: true);
        Assert.True(r2.Success, r2.Message + string.Join("\n", r2.Problems));

        // 3. Новая ссылка на несуществующий файл — ошибка, как раньше.
        var text = File.ReadAllText(node);
        File.WriteAllText(node, text + "ref: Q:\\Нет такого\\Ось.tcprt\r\n");
        plan = await c.Service.PrepareCheckinAsync(c.WorkRoot);
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.MissingReference && p.Message.Contains("Ось.tcprt"));
    }

    [Fact]
    public async Task MissingLinkedFile_UserPicksFileElsewhere_Registered_Bound_Placed()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        var shaftSource = System.IO.Path.Combine(src, @"Детали\Вал.tcprt");
        using (var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm")))
        {
            report.Documents.Single(d => d.FileName == "Вал.tcprt").Include = false;
            report.Recompute();
            var r = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
            await c.OpenAsync(r.HeadDocumentId);
        }
        // Файла по ссылке больше нет; модель нашлась в другой папке.
        var elsewhere = _env.Path("Архив", "Вал.tcprt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(elsewhere)!);
        File.Move(shaftSource, elsewhere);
        var nodeId = _env.DocId("Сборки/Узел.tcasm");

        using var reg = await c.Service.PrepareImportAsync(elsewhere, null, null, default, new ImportOptions { RelPathPrefix = "Сборки", AllowExistingHead = true });
        var imported = await c.Service.ExecuteImportAsync(reg, _env.Project.Id, null);
        Assert.True(imported.Success, imported.Message);
        var bind = await c.Service.BindReferenceAsync(c.WorkRoot, nodeId, shaftSource, imported.HeadDocumentId, imported.HeadVersion);
        Assert.True(bind.Success, bind.Message);

        var plan = await c.Service.PrepareUpdateToLatestAsync(c.WorkRoot, new[] { nodeId });
        var placed = await c.Service.ExecuteOpenAsync(plan, verifyInCad: true);
        Assert.True(placed.Success, placed.Message);
        Assert.True(placed.CadReport!.Ok, string.Join("\n", placed.CadReport.Problems));
        Assert.Contains(placed.CadReport.Configurations.SelectMany(x => x.Components), x => x.LoadedPath == c.File("Сборки/Вал.tcprt"));
    }

    [Fact]
    public async Task LinkedFile_Bind_ParentLockedAndEdited_KeepsEditsAndLock()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        var shaftSource = System.IO.Path.Combine(src, @"Детали\Вал.tcprt");
        using (var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm")))
        {
            report.Documents.Single(d => d.FileName == "Вал.tcprt").Include = false;
            report.Recompute();
            var r = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
            await c.OpenAsync(r.HeadDocumentId);
        }
        var nodeId = _env.DocId("Сборки/Узел.tcasm");
        await c.CheckOutAsync(nodeId);
        TestCadWriter.AppendGeometry(c.File("Сборки/Узел.tcasm"), "правка");

        using var reg = await c.Service.PrepareImportAsync(shaftSource, null, null, default, new ImportOptions { RelPathPrefix = "Сборки", AllowExistingHead = true });
        var imported = await c.Service.ExecuteImportAsync(reg, _env.Project.Id, null);
        var bind = await c.Service.BindReferenceAsync(c.WorkRoot, nodeId, shaftSource, imported.HeadDocumentId, imported.HeadVersion);
        Assert.True(bind.Success, bind.Message);
        Assert.True(bind.ParentModifiedLocally);
        Assert.NotNull(_env.Server.Doc(nodeId).Lock); // блокировка моя — осталась
        Assert.Contains("правка", File.ReadAllText(c.File("Сборки/Узел.tcasm")));
        var entry = c.State.FindByDocument(nodeId)!;
        Assert.Equal(2, entry.Version);
        Assert.Contains(entry.Deps, d => d.DocumentId == imported.HeadDocumentId);

        // Повтор для того же файла: он уже в PDM — загружать нечего, только привязка (ссылка уже привязана — сообщение).
        using var again = await c.Service.PrepareImportAsync(shaftSource, null, null, default, new ImportOptions { RelPathPrefix = "Сборки", AllowExistingHead = true });
        Assert.Equal(ImportDisposition.FromStorage, again.Head.Disposition);
        var reused = await c.Service.ExecuteImportAsync(again, _env.Project.Id, null);
        Assert.True(reused.Success, reused.Message);
        Assert.Equal(imported.HeadDocumentId, reused.HeadDocumentId);
        var rebind = await c.Service.BindReferenceAsync(c.WorkRoot, nodeId, shaftSource, reused.HeadDocumentId, reused.HeadVersion);
        Assert.False(rebind.Success);
    }

    [Fact]
    public async Task Import_ContentAlreadyInPdm_TakenFromStorage_NotUploadedAgain()
    {
        var c = _env.For("ivanov");
        await _env.ImportSourceAsync(c);
        var boltDoc = _env.Server.DocByPath("Детали/Болт.tcprt");

        // Второе изделие использует тот же болт (копия файла, те же байты) и новую деталь.
        var src2 = _env.Path("src2");
        var bolt2 = System.IO.Path.Combine(src2, "Детали", "Болт.tcprt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(bolt2)!);
        File.Copy(System.IO.Path.Combine(_env.Path("src"), @"Детали\Болт.tcprt"), bolt2);
        TestCadWriter.Write(System.IO.Path.Combine(src2, "Детали", "Ось.tcprt"), new TestCadDocument().Prop("Обозначение", "ГДЕ.001").Geom("axle"));
        var head2 = TestCadWriter.Write(System.IO.Path.Combine(src2, "Изделие2.tcasm"), new TestCadDocument().Prop("Обозначение", "ГДЕ.000")
            .Ref(bolt2, System.IO.Path.Combine(src2, "Детали", "Ось.tcprt")));

        using var report = await c.Service.PrepareImportAsync(head2);
        var bolt = report.Documents.Single(d => d.SourcePath == bolt2);
        Assert.Equal(ImportDisposition.FromStorage, bolt.Disposition);
        Assert.Equal(boltDoc.Id, bolt.Existing!.Document.Id);
        Assert.Equal(new[] { "Детали/Ось.tcprt", "Изделие2.tcasm" }, report.NewDocuments.Select(d => d.RelPath).OrderBy(x => x, StringComparer.Ordinal));

        var docsBefore = _env.Server.Docs.Count;
        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        Assert.Equal(docsBefore + 2, _env.Server.Docs.Count);
        Assert.Contains("Взято из хранилища: 1", result.Message);
        var head = _env.Server.Doc(result.HeadDocumentId).Latest;
        Assert.Contains(head.Refs, r => r.Child == boltDoc.Id && r.ChildVersion == 1);

        // Повторная постановка того же изделия: головной документ уже учтён — ставить нельзя, открывать надо из PDM.
        using var again = await c.Service.PrepareImportAsync(head2);
        Assert.Equal(ImportDisposition.FromStorage, again.Documents.Single(d => d.SourcePath == bolt2).Disposition);
        Assert.Contains(again.Problems, p => p.Code == LocalProblemCodes.AlreadyInStorage); // голова уже учтена
        Assert.False(again.CanExecute(true));
    }

    [Fact]
    public async Task Import_StalePath_FileInSameFolder_IsFound()
    {
        // Чертёж перенесён вместе с моделью: в файле старый путь, модель лежит рядом — CAD найдёт её в папке чертежа.
        var src = _env.Path("src");
        TestCadWriter.Create(System.IO.Path.Combine(src, "Пластина.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "Другая папка", "Пластина.tcprt"), geometry: "двойник");
        var head = TestCadWriter.Create(System.IO.Path.Combine(src, "Пластина.tcdrw"), new[] { @"Q:\Старый проект\Пластина.tcprt" });
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(head);

        var r = report.Head.References.Single();
        Assert.Equal(ImportReferenceCategory.Found, r.Category);
        Assert.Equal(System.IO.Path.Combine(src, "Пластина.tcprt"), r.TargetPath);
        Assert.True(report.CanExecute(false));
    }

    private TestCadAdapter Cad => (TestCadAdapter)_env.Registry.Get(TestCadAdapter.Id)!;

    [Fact]
    public async Task Import_SearchFolders_OnlyLinkedDrawings_ReadFully_NeighbourAssemblyIgnored()
    {
        var src = _env.CreateSourceProject();
        // Соседнее изделие в той же папке использует общий болт — в комплект оно попасть не должно.
        TestCadWriter.Write(System.IO.Path.Combine(src, "Соседнее.tcasm"), new TestCadDocument().Prop("Обозначение", "ГДЕ.000")
            .Ref(System.IO.Path.Combine(src, @"Детали\Болт.tcprt")));
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"));

        Assert.Equal(new[]
        {
            "Детали/Болт.tcprt", "Детали/Вал.tcprt", "Детали/Корпус.tcprt", "Изделие.tcasm",
            "Сборки/Узел.tcasm", "Чертежи/Изделие.tcdrw", "Чертежи/Корпус.tcdrw",
        }, report.IncludedDocuments.Select(d => d.RelPath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.DoesNotContain(report.Documents, d => d.FileName is "Соседнее.tcasm" or "Чужой.tcdrw");
        // Полностью (с открытием в CAD) прочитаны только 7 документов комплекта; у чужого чертежа — только ссылки.
        Assert.Equal(7, Cad.FullReadCount);
    }

    [Fact]
    public async Task Import_Repeated_PropertiesFromCache_DrawingKeyIncludesModel()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        var head = System.IO.Path.Combine(src, "Изделие.tcasm");
        using (var first = await c.Service.PrepareImportAsync(head)) { }
        Assert.Equal(7, Cad.FullReadCount);

        using var second = await c.Service.PrepareImportAsync(head);
        // Все 7 — из кэша: детали и сборки по своему хешу, чертежи — по хешу чертежа и модели.
        Assert.Equal(7, Cad.FullReadCount);
        Assert.Equal("АБВ.001 СБ", second.Documents.Single(d => d.FileName == "Корпус.tcdrw").Designation);
        var asm = second.Documents.Single(d => d.FileName == "Изделие.tcasm");
        Assert.Equal("АБВ.000", asm.Designation);
        Assert.Equal(new[] { "Исп1", "Исп2" }, asm.Configurations);
        Assert.Equal("12", asm.Properties["Исп2"]["Масса"]);
        Assert.Equal(3, asm.References.Count);

        // Изменённая деталь без чертежа — перечитывается только она.
        TestCadWriter.Write(System.IO.Path.Combine(src, @"Детали\Вал.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.002").Prop("Наименование", "Вал новый").Geom("cyl 6 100"));
        using (var third = await c.Service.PrepareImportAsync(head))
            Assert.Equal("Вал новый", third.Documents.Single(d => d.FileName == "Вал.tcprt").Name);
        Assert.Equal(8, Cad.FullReadCount);

        // Изменённая модель — перечитываются и она, и её чертёж (байты чертежа те же).
        TestCadWriter.Write(System.IO.Path.Combine(src, @"Детали\Корпус.tcprt"), new TestCadDocument().Prop("Обозначение", "АБВ.001").Prop("Наименование", "Корпус").Geom("box 11 20 30"));
        using (var fourth = await c.Service.PrepareImportAsync(head)) { }
        Assert.Equal(10, Cad.FullReadCount);
    }

    [Fact]
    public async Task Import_ThenOpen_DownloadsNothing()
    {
        var c = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(c);
        var open = await c.OpenAsync(head, verify: true);
        Assert.Equal(0, open.Downloaded);
        Assert.Equal(0, _env.Server.DownloadCount);
        Assert.True(open.CadReport!.Ok, string.Join("\n", open.CadReport.Problems));
    }
}
