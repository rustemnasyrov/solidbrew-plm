using System.IO.Compression;
using System.Text.Json;
using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;
using SolidBrew.Core.Json;
using SolidBrew.Workspace;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>
/// Сквозные проверки критериев приёмки этапа 1 (ТЗ, раздел 11, п. 1–8) на тестовом CAD:
/// настоящий сервер, настоящий HTTP-клиент, два конструктора на двух «компьютерах» (свой кэш и рабочая папка).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class AcceptanceTests(ServerFixture fx) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ------------------------------------------------------------------ общие шаги

    private sealed record Imported(SourceKit Kit, ProjectDto Project, ImportResult Result, Dictionary<string, Guid> Ids)
    {
        public Guid Head => Ids[SourceKit.Head];
        public Guid this[string relPath] => Ids[relPath];
    }

    /// <summary>Поставить «сетевой» проект на учёт (чертежи — из дополнительной папки).</summary>
    private async Task<Imported> ImportAsync(Computer c)
    {
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"));
        using var report = await c.Service.PrepareImportAsync(kit.HeadFile, new[] { kit.DrawingsFolder });
        Assert.True(report.CanExecute(allowMissing: false), string.Join("\n", report.Problems));
        var result = await c.Service.ExecuteImportAsync(report, project.Id, "Постановка на учёт");
        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Problems));
        var ids = result.Response!.Items.ToDictionary(i => i.RelPath, i => i.DocumentId, StringComparer.OrdinalIgnoreCase);
        return new Imported(kit, project, result, ids);
    }

    private static async Task<int> LatestAsync(Computer c, Guid id) => (await c.DocAsync(id)).LatestVersion;

    private static async Task<int> KitVersionOf(Computer c, Guid head, int headVersion, Guid child) =>
        (await c.Api.GetKitAsync(head, headVersion)).Items.Single(i => i.DocumentId == child).Version;

    /// <summary>Проверка в CAD: обе конфигурации, все компоненты — из рабочей папки и нужных версий.</summary>
    private static void AssertLoadedFromWorkspace(OpenResult r, string workRoot, int components, params string[] expectedFiles)
    {
        var cad = r.CadReport ?? throw new Xunit.Sdk.XunitException("Нет отчёта проверки в CAD.");
        Assert.True(cad.Ok, string.Join("\n", cad.Problems));
        Assert.Equal(new[] { "Исп1", "Исп2" }, cad.Configurations.Select(c => c.Name));
        foreach (var config in cad.Configurations)
        {
            Assert.Equal(components, config.Components.Count);
            Assert.All(config.Components, comp =>
            {
                Assert.True(comp.InsideWorkRoot, comp.LoadedPath);
                Assert.StartsWith(workRoot.TrimEnd('\\') + "\\", comp.LoadedPath, StringComparison.OrdinalIgnoreCase);
            });
            foreach (var f in expectedFiles)
                Assert.Contains(config.Components, comp => string.Equals(comp.Name, f, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------ 1

    [Fact]
    public async Task Criterion1_ImportFromNetworkFolder_SourcesUnchanged_MissingAndAmbiguousShownSeparately()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var project = await fx.NewProjectAsync();
        var kit = SourceKit.Create(_dir.Path("Сеть"), withProblems: true);
        // Исходники с сетевого диска часто только для чтения; время — заведомо в прошлом, чтобы любая запись была видна.
        foreach (var f in Directory.EnumerateFiles(kit.Root, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            File.SetCreationTimeUtc(f, new DateTime(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        }
        File.SetAttributes(kit.Full(SourceKit.Body), FileAttributes.ReadOnly);
        var before = kit.Snapshot();

        using var report = await a.Service.PrepareImportAsync(kit.HeadFile, new[] { kit.DrawingsFolder });

        // Отсутствующая и неоднозначная ссылки — отдельными категориями и отдельными проблемами.
        var missing = Assert.Single(report.ByCategory(ImportReferenceCategory.Missing));
        Assert.Equal(@"Покупные\Двигатель.tcprt", missing.SavedPath);
        var ambiguous = Assert.Single(report.ByCategory(ImportReferenceCategory.Ambiguous));
        Assert.Equal("Шайба.tcprt", ambiguous.FileName);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.Contains(kit.Full(SourceKit.Washer), ambiguous.Candidates, StringComparer.OrdinalIgnoreCase);
        Assert.Null(ambiguous.EffectiveTarget); // PDM не выбирает файл по одному имени
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.MissingReference && p.Message.Contains("Двигатель"));
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.AmbiguousReference && p.Message.Contains("Шайба"));
        Assert.DoesNotContain(report.Problems, p => p.Code == LocalProblemCodes.MissingReference && p.Message.Contains("Шайба"));
        Assert.Empty(report.ByCategory(ImportReferenceCategory.NameOnly));
        Assert.Empty(report.ByCategory(ImportReferenceCategory.OutsideFolders));

        // Чертежи найдены по ссылкам в доп. папке; чужой чертёж не взят.
        var included = report.IncludedDocuments.Select(d => d.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(SourceKit.HeadDrawing, included);
        Assert.Contains(SourceKit.SubDrawing, included);
        Assert.Contains(SourceKit.BodyDrawing, included);
        Assert.DoesNotContain("Чертежи/Чужой.tcdrw", included);

        // Без решений — отказ, на сервере ничего нет.
        var refused = await a.Service.ExecuteImportAsync(report, project.Id, "без решений", allowMissing: true);
        Assert.False(refused.Success);
        Assert.Empty(await a.Api.SearchDocumentsAsync(projectId: project.Id, includeUnused: true));

        // Решение: выбрать шайбу из «Детали», отсутствующий двигатель — разрешить. Одна сдача.
        ambiguous.Chosen = kit.Full(SourceKit.Washer);
        var result = await a.Service.ExecuteImportAsync(report, project.Id, "Постановка на учёт", allowMissing: true);
        Assert.True(result.Success, result.Message + "\n" + string.Join("\n", result.Problems));

        var expected = new[] { SourceKit.Head, SourceKit.SubAssembly, SourceKit.Body, SourceKit.Shaft, SourceKit.Bolt, SourceKit.Washer, SourceKit.HeadDrawing, SourceKit.SubDrawing, SourceKit.BodyDrawing };
        var docs = await a.Api.SearchDocumentsAsync(projectId: project.Id, includeUnused: true);
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), docs.Select(d => d.RelPath).OrderBy(x => x, StringComparer.Ordinal));
        foreach (var d in docs)
        {
            var detail = await a.Api.GetDocumentAsync(d.Id);
            var v = Assert.Single(detail.Versions);
            Assert.Equal(result.Response!.CheckinId, v.CheckinId);
            Assert.Equal(Computer.Hash(kit.Full(d.RelPath)), v.Sha256);
        }

        var headKit = await a.Api.GetKitAsync(result.HeadDocumentId, 1);
        Assert.Contains(headKit.Unresolved, u => u.RefPath == @"Покупные\Двигатель.tcprt");
        Assert.Contains(headKit.Items, i => i.RelPath == SourceKit.Washer);
        Assert.DoesNotContain(headKit.Items, i => i.RelPath.Contains("Архив", StringComparison.Ordinal));

        // Исходники не изменены: содержимое, атрибуты, время изменения и создания.
        var after = kit.Snapshot();
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (path, state) in before)
            Assert.Equal(state, after[path]);
    }

    // ------------------------------------------------------------------ 2

    [Fact]
    public async Task Criterion2_OtherComputer_OpensOldAndNewAssembly_ComponentsFromWorkspace_ReopenFromCache()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var imp = await ImportAsync(a);
        await a.OpenAsync(imp.Head);
        await a.CheckOutAsync(SourceKit.Head, SourceKit.Body);
        a.Edit(SourceKit.Body, "fillet 2");
        a.Edit(SourceKit.Head, "mate");
        await a.CheckinAsync("v2");
        Assert.Equal(2, await LatestAsync(a, imp.Head));
        var bodyV1 = (await a.Api.GetKitAsync(imp.Head, 1)).Items.Single(i => i.DocumentId == imp[SourceKit.Body]);
        var bodyV2 = (await a.Api.GetKitAsync(imp.Head, 2)).Items.Single(i => i.DocumentId == imp[SourceKit.Body]);
        Assert.Equal(1, bodyV1.Version);
        Assert.Equal(2, bodyV2.Version);

        // Исходной папки больше нет.
        Directory.Move(imp.Kit.Root, imp.Kit.Root + "-перенесено");

        var b = await Computer.NewAsync(fx, _dir, "petrov");
        var bOld = b.WithFolder(_dir.Path("petrov", "work-v1"));
        var bNew = b.WithFolder(_dir.Path("petrov", "work-v2"));

        var r1 = await bOld.OpenAsync(imp.Head, version: 1, verify: true);
        AssertLoadedFromWorkspace(r1, bOld.WorkRoot, 4, "Узел.tcasm", "Вал.tcprt", "Болт.tcprt", "Корпус.tcprt");
        Assert.Equal(5, r1.Downloaded);
        Assert.Equal(5, b.Counter.Downloads);
        Assert.Equal(bodyV1.Sha256, Computer.Hash(bOld.File(SourceKit.Body)));

        var r2 = await bNew.OpenAsync(imp.Head, version: 2, verify: true);
        AssertLoadedFromWorkspace(r2, bNew.WorkRoot, 4, "Узел.tcasm", "Вал.tcprt", "Болт.tcprt", "Корпус.tcprt");
        Assert.Equal(2, r2.Downloaded); // общие версии — из кэша, докачаны только сборка v2 и корпус v2
        Assert.Equal(bodyV2.Sha256, Computer.Hash(bNew.File(SourceKit.Body)));

        // Повторное открытие — из кэша и папки, без скачиваний.
        var downloads = b.Counter.Downloads;
        var r3 = await bOld.OpenAsync(imp.Head, version: 1, verify: true);
        Assert.Empty(r3.Placed);
        Assert.Equal(0, r3.Downloaded);
        AssertLoadedFromWorkspace(r3, bOld.WorkRoot, 4);
        var r4 = await bNew.OpenAsync(imp.Head, version: 2);
        Assert.Empty(r4.Placed);
        // Старую папку на новую версию — тоже из кэша.
        var r5 = await bOld.OpenAsync(imp.Head, version: 2, verify: true);
        Assert.Equal(2, r5.Placed.Count);
        Assert.Equal(0, r5.Downloaded);
        AssertLoadedFromWorkspace(r5, bOld.WorkRoot, 4);
        Assert.Equal(downloads, b.Counter.Downloads);
    }

    // ------------------------------------------------------------------ 3

    [Fact]
    public async Task Criterion3_TwoDesignersCheckinDifferentParts_ConcurrentCheckoutOnlyOneWins_NotCheckedOutIsReadOnly()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var b = await Computer.NewAsync(fx, _dir, "petrov");
        var imp = await ImportAsync(a);
        await a.OpenAsync(imp.Head);
        await b.OpenAsync(imp.Head);

        // Разные детали одной сборки — независимо и одновременно.
        await a.CheckOutAsync(SourceKit.Body);
        await b.CheckOutAsync(SourceKit.Shaft);
        a.Edit(SourceKit.Body);
        b.Edit(SourceKit.Shaft);
        await Task.WhenAll(a.CheckinAsync("корпус"), b.CheckinAsync("вал"));
        var body = await a.Api.GetDocumentAsync(imp[SourceKit.Body]);
        var shaft = await a.Api.GetDocumentAsync(imp[SourceKit.Shaft]);
        Assert.Equal(2, body.Document.LatestVersion);
        Assert.Equal(a.User.Login, body.Versions[0].AuthorLogin);
        Assert.Equal(2, shaft.Document.LatestVersion);
        Assert.Equal(b.User.Login, shaft.Versions[0].AuthorLogin);
        Assert.Equal(1, await LatestAsync(a, imp.Head));

        // Одновременное взятие одного файла: ровно один успех, у другого файл только для чтения.
        var bolt = imp[SourceKit.Bolt];
        for (var round = 0; round < 15; round++)
        {
            var results = await Task.WhenAll(
                Task.Run(() => a.Service.CheckOutAsync(a.WorkRoot, new[] { bolt })),
                Task.Run(() => b.Service.CheckOutAsync(b.WorkRoot, new[] { bolt })));
            Assert.Single(results, r => r.Success);
            var (winner, loser, lost) = results[0].Success ? (a, b, results[1]) : (b, a, results[0]);
            var item = Assert.Single(lost.Items);
            Assert.NotNull(item.LockedBy);
            Assert.Equal(winner.User.Id, item.LockedBy!.UserId);
            Assert.Contains(winner.User.DisplayName, item.Message);

            var loserFile = loser.File(SourceKit.Bolt);
            Assert.True(Computer.IsReadOnly(loserFile));
            Assert.Throws<UnauthorizedAccessException>(() => File.AppendAllText(loserFile, "попытка сохранить"));
            Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(loserFile, "попытка перезаписать"));
            Assert.False(Computer.IsReadOnly(winner.File(SourceKit.Bolt)));
            Assert.False(loser.State.FindByPath(SourceKit.Bolt)!.LockedByMe);

            var undo = await winner.Service.PrepareUndoCheckOutAsync(winner.WorkRoot, new[] { bolt });
            Assert.True((await winner.Service.ExecuteUndoCheckOutAsync(undo)).Success);
        }

        // То же на уровне API: несколько пользователей одновременно, много раз.
        var users = new List<PdmApiClient>();
        for (var i = 0; i < 5; i++)
        {
            var u = await fx.NewUserAsync("racer");
            var client = fx.NewClient();
            await client.LoginAsync(u.Login, ServerFixture.UserPassword);
            users.Add(client);
        }
        var shaftId = imp[SourceKit.Shaft];
        for (var round = 0; round < 15; round++)
        {
            var attempts = await Task.WhenAll(users.Select(u => Task.Run(async () =>
            {
                try
                {
                    await u.LockAsync(shaftId);
                    return (Client: u, Error: (PdmApiException?)null);
                }
                catch (PdmApiException ex)
                {
                    return (Client: u, Error: ex);
                }
            })));
            var won = Assert.Single(attempts, x => x.Error == null);
            Assert.All(attempts.Where(x => x.Error != null), x =>
            {
                Assert.Equal(409, x.Error!.StatusCode);
                Assert.NotNull(x.Error.Error?.Lock);
            });
            await won.Client.UnlockAsync(shaftId);
        }

        // Не взятый в работу файл (корпус у Петрова) остаётся только для чтения.
        Assert.True(Computer.IsReadOnly(b.File(SourceKit.Body)));
        Assert.Throws<UnauthorizedAccessException>(() => File.AppendAllText(b.File(SourceKit.Body), "x"));
    }

    // ------------------------------------------------------------------ 4

    [Fact]
    public async Task Criterion4_NewAssemblyWithPartsAndDrawingsCheckedInWhole_MissingDependencyOrFailurePublishesNothing()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var imp = await ImportAsync(a);
        await a.OpenAsync(imp.Head);
        await a.CheckOutAsync(SourceKit.Bolt);
        a.Edit(SourceKit.Bolt, "thread 1.25");

        // Новые (черновые) документы: сборка, детали, чертежи. Муфты пока нет — отсутствующая зависимость.
        const string drive = "Изделие/Привод/Привод.tcasm";
        const string gear = "Изделие/Привод/Шестерня.tcprt";
        const string clutch = "Изделие/Привод/Муфта.tcprt";
        const string driveDrawing = "Изделие/Привод/Привод.tcdrw";
        const string gearDrawing = "Изделие/Привод/Шестерня.tcdrw";
        TestCadWriter.Write(a.File(gear), new TestCadDocument().Prop("Обозначение", "АБВ.201").Geom("gear z=20"));
        TestCadWriter.Write(a.File(drive), new TestCadDocument().Config("Исп1", "Исп2").Prop("Обозначение", "АБВ.200")
            .Ref("Шестерня.tcprt", "Муфта.tcprt", @"..\Детали\Болт.tcprt"));
        TestCadWriter.Write(a.File(driveDrawing), new TestCadDocument().Prop("Обозначение", "АБВ.200 СБ").Ref("Привод.tcasm"));
        TestCadWriter.Write(a.File(gearDrawing), new TestCadDocument().Prop("Обозначение", "АБВ.201 Ч").Ref("Шестерня.tcprt"));

        var docsBefore = (await a.Api.SearchDocumentsAsync(projectId: imp.Project.Id, includeUnused: true)).Count;
        async Task AssertNothingPublishedAsync()
        {
            Assert.Equal(docsBefore, (await a.Api.SearchDocumentsAsync(projectId: imp.Project.Id, includeUnused: true)).Count);
            var bolt = await a.DocAsync(imp[SourceKit.Bolt]);
            Assert.Equal(1, bolt.LatestVersion);
            Assert.Equal(a.User.Id, bolt.Lock?.UserId); // блокировка цела
            Assert.True(a.State.FindByPath(SourceKit.Bolt)!.LockedByMe);
            Assert.False(Computer.IsReadOnly(a.File(SourceKit.Bolt)));
            Assert.Null(a.State.FindByPath(drive)); // черновики остаются черновиками
        }

        // 1) Отсутствующая зависимость: клиент не даёт сдать.
        var plan = await a.Service.PrepareCheckinAsync(a.WorkRoot);
        Assert.False(plan.CanExecute);
        Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.MissingReference && p.Message.Contains("Муфта"));
        var refused = await a.Service.ExecuteCheckinAsync(plan, "без муфты");
        Assert.False(refused.Success);
        await AssertNothingPublishedAsync();

        // 2) Отсутствующая зависимость, дошедшая до сервера (ссылка на несуществующую версию), — сервер отклоняет всю сдачу.
        var gearSha = Computer.Hash(a.File(gear));
        await using (var s = File.OpenRead(a.File(gear)))
            await a.Api.UploadObjectAsync(gearSha, s, s.Length, null);
        var rejected = await Assert.ThrowsAsync<PdmApiException>(() => a.Api.CheckinAsync(new CheckinRequest
        {
            ProjectId = imp.Project.Id,
            Items =
            {
                new CheckinItem
                {
                    TempKey = gear, RelPath = gear, Sha256 = gearSha, Size = new FileInfo(a.File(gear)).Length, Cad = TestCadAdapter.Id, Kind = DocumentKind.Part,
                    References = { new CheckinReference { RefPath = "Болт.tcprt", DocumentId = imp[SourceKit.Bolt], Version = 99 } },
                },
            },
        }));
        Assert.Contains(rejected.Error!.Problems, p => p.Code == ProblemCodes.MissingDependency);
        await AssertNothingPublishedAsync();

        // 3) Сбой на сервере перед COMMIT: ничего не опубликовано, правки и блокировки целы.
        TestCadWriter.Write(a.File(clutch), new TestCadDocument().Prop("Обозначение", "АБВ.202").Geom("clutch"));
        var local = new[] { SourceKit.Bolt, drive, gear, clutch, driveDrawing, gearDrawing }.ToDictionary(p => p, p => Computer.Hash(a.File(p)));
        var failing = await a.Service.PrepareCheckinAsync(a.WorkRoot);
        Assert.True(failing.CanExecute, string.Join("\n", failing.Problems));
        Assert.Equal(6, failing.Included.Count());
        fx.Hooks.BeforeCommit = (_, _, _) => throw new InvalidOperationException("Внедрённый сбой перед COMMIT");
        CheckinResult failed;
        try
        {
            failed = await a.Service.ExecuteCheckinAsync(failing, "сбой");
        }
        finally
        {
            fx.Hooks.BeforeCommit = null;
        }
        Assert.False(failed.Success);
        Assert.NotEmpty(failed.Problems);
        await AssertNothingPublishedAsync();
        foreach (var (path, sha) in local) Assert.Equal(sha, Computer.Hash(a.File(path)));

        // 4) После устранения — вся новая сборка с деталями и чертежами одной сдачей.
        var ok = await a.CheckinAsync("Новый привод");
        Assert.Equal(6, ok.Published.Count);
        var published = ok.Published.ToDictionary(p => p.RelPath, StringComparer.OrdinalIgnoreCase);
        Assert.All(new[] { drive, gear, clutch, driveDrawing, gearDrawing }, p => Assert.Equal(1, published[p].Version));
        Assert.Equal(2, published[SourceKit.Bolt].Version);
        var checkinIds = new HashSet<long>();
        foreach (var p in ok.Published) checkinIds.Add((await a.Api.GetDocumentAsync(p.DocumentId)).Versions[0].CheckinId);
        Assert.Single(checkinIds);

        var driveKit = await a.Api.GetKitAsync(published[drive].DocumentId, 1);
        Assert.Equal(4, driveKit.Items.Count);
        Assert.Contains(driveKit.Items, i => i.RelPath == SourceKit.Bolt && i.Version == 2);
        Assert.Contains(driveKit.Items, i => i.RelPath == clutch && i.Version == 1);
        var drawingKit = await a.Api.GetKitAsync(published[driveDrawing].DocumentId, 1);
        Assert.Contains(drawingKit.Items, i => i.RelPath == drive);
        Assert.Null((await a.DocAsync(imp[SourceKit.Bolt])).Lock);
        Assert.All(local.Keys, p => Assert.True(Computer.IsReadOnly(a.File(p)), p));
    }

    // ------------------------------------------------------------------ 5

    [Fact]
    public async Task Criterion5_NewPartVersionKeepsOldAssembly_ExplicitUpdateMakesNewAssemblyVersion_WhereUsedShowsBoth()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var b = await Computer.NewAsync(fx, _dir, "petrov");
        var imp = await ImportAsync(a);
        var body = imp[SourceKit.Body];
        await a.OpenAsync(imp.Head);
        await b.OpenAsync(imp.Head);

        // Петров выпускает корпус v2: сборка v1 по-прежнему с корпусом v1.
        await b.CheckOutAsync(SourceKit.Body);
        b.Edit(SourceKit.Body);
        await b.CheckinAsync("корпус v2");
        Assert.Equal(2, await LatestAsync(a, body));
        Assert.Equal(1, await LatestAsync(a, imp.Head));
        Assert.Equal(1, await KitVersionOf(a, imp.Head, 1, body));

        // Иванов берёт сборку, обновляет корпус до последней версии и сдаёт сборку.
        var headSha = Computer.Hash(a.File(SourceKit.Head));
        await a.CheckOutAsync(SourceKit.Head);
        var update = await a.Service.PrepareUpdateToLatestAsync(a.WorkRoot, new[] { body });
        Assert.True((await a.Service.ExecuteOpenAsync(update)).Success);
        Assert.Equal(2, a.State.FindByPath(SourceKit.Body)!.Version);

        var plan = await a.Service.PrepareCheckinAsync(a.WorkRoot);
        var headItem = Assert.Single(plan.Included);
        Assert.Equal(SourceKit.Head, headItem.RelPath);
        Assert.False(headItem.ContentChanged);
        Assert.True(headItem.ReferencesChanged);
        var result = await a.Service.ExecuteCheckinAsync(plan, "обновлён корпус");
        Assert.True(result.Success, result.Message);
        Assert.Equal(headSha, Computer.Hash(a.File(SourceKit.Head))); // байты сборки те же

        Assert.Equal(2, await LatestAsync(a, imp.Head));
        Assert.Equal(2, await KitVersionOf(a, imp.Head, 2, body));
        Assert.Equal(1, await KitVersionOf(a, imp.Head, 1, body));

        var view = await a.Service.GetWhereUsedAsync(body, 2);
        var usesV2 = Assert.Single(view.ThisVersion, i => i.ParentDocumentId == imp.Head);
        Assert.Equal(2, usesV2.ParentVersion);
        Assert.True(usesV2.UsesLatestChild);
        var usesV1 = Assert.Single(view.OtherVersions, i => i.ParentDocumentId == imp.Head);
        Assert.Equal(1, usesV1.ParentVersion);
        Assert.Equal(1, usesV1.ChildVersion);
        Assert.False(usesV1.UsesLatestChild);
        Assert.Contains(view.NotLatest, i => i.ParentDocumentId == imp.Head && i.ParentVersion == 1);
        Assert.Contains(view.OtherVersions, i => i.ParentDocumentId == imp[SourceKit.BodyDrawing]); // чертёж остался на v1
    }

    // ------------------------------------------------------------------ 6

    [Fact]
    public async Task Criterion6_DrawingOnlyChange_NewDrawingVersion_ModelUnchanged()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var imp = await ImportAsync(a);
        var body = imp[SourceKit.Body];
        var drawing = imp[SourceKit.BodyDrawing];
        await a.OpenAsync(imp.Head);
        var open = await a.OpenAsync(drawing);
        Assert.Single(open.Placed); // корпус уже в папке — разложен только чертёж

        await a.CheckOutAsync(SourceKit.BodyDrawing);
        a.Edit(SourceKit.BodyDrawing, "dimension 42");
        var r = await a.CheckinAsync("только чертёж");
        var only = Assert.Single(r.Published);
        Assert.Equal(drawing, only.DocumentId);
        Assert.Equal(2, only.Version);
        Assert.Equal(1, await LatestAsync(a, body));
        Assert.Equal(1, await LatestAsync(a, imp.Head));
        Assert.Equal(1, await KitVersionOf(a, drawing, 2, body));

        // И наоборот: изменение модели не создаёт новую версию чертежа.
        await a.CheckOutAsync(SourceKit.Body);
        a.Edit(SourceKit.Body);
        var r2 = await a.CheckinAsync("только модель");
        Assert.Equal(body, Assert.Single(r2.Published).DocumentId);
        Assert.Equal(2, await LatestAsync(a, body));
        Assert.Equal(2, await LatestAsync(a, drawing));
        Assert.Equal(1, await KitVersionOf(a, drawing, 2, body));
    }

    // ------------------------------------------------------------------ 7

    [Fact]
    public async Task Criterion7_RenamePartUsedInAssembly_NewAndOldAssemblyVersionsOpenAndVerify()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var imp = await ImportAsync(a);
        var shaft = imp[SourceKit.Shaft];
        var sub = imp[SourceKit.SubAssembly];
        await a.OpenAsync(imp.Head);

        const string newName = "Изделие/Детали/Вал-01.tcprt";
        var plan = await a.Service.PrepareRenameAsync(a.WorkRoot, SourceKit.Shaft, newName);
        Assert.True(plan.CanExecute, string.Join("\n", plan.Problems));
        Assert.Equal(sub, Assert.Single(plan.Parents).DocumentId);
        var renamed = await a.Service.ExecuteRenameAsync(plan);
        Assert.True(renamed.Success, renamed.Message + "\n" + string.Join("\n", renamed.Problems));
        Assert.True(File.Exists(a.File(newName)));
        Assert.False(File.Exists(a.File(SourceKit.Shaft)));
        var shaftDoc = await a.DocAsync(shaft);
        Assert.Equal(newName, shaftDoc.RelPath);
        Assert.Equal(2, shaftDoc.LatestVersion);
        Assert.Equal(2, await LatestAsync(a, sub));
        Assert.Contains("Вал-01.tcprt", File.ReadAllText(a.File(SourceKit.SubAssembly)));
        Assert.Null((await a.DocAsync(sub)).Lock); // взятые ради переименования документы отпущены

        // Новая версия головной сборки — с переименованной деталью.
        await a.CheckOutAsync(SourceKit.Head);
        await a.CheckinAsync("узел после переименования вала");
        Assert.Equal(2, await LatestAsync(a, imp.Head));
        Assert.Contains((await a.Api.GetKitAsync(imp.Head, 2)).Items, i => i.RelPath == newName);
        Assert.Contains((await a.Api.GetKitAsync(imp.Head, 1)).Items, i => i.RelPath == SourceKit.Shaft);

        // Другой компьютер: обе версии открываются и проходят проверку.
        var b = await Computer.NewAsync(fx, _dir, "petrov");
        var bNew = b.WithFolder(_dir.Path("petrov", "new"));
        var bOld = b.WithFolder(_dir.Path("petrov", "old"));
        AssertLoadedFromWorkspace(await bNew.OpenAsync(imp.Head, 2, verify: true), bNew.WorkRoot, 4, "Вал-01.tcprt");
        AssertLoadedFromWorkspace(await bOld.OpenAsync(imp.Head, 1, verify: true), bOld.WorkRoot, 4, "Вал.tcprt");
        // Старая версия поверх новой в той же папке: документ лежит под другим именем — заменяется.
        var back = await bNew.OpenAsync(imp.Head, 1, verify: true);
        AssertLoadedFromWorkspace(back, bNew.WorkRoot, 4, "Вал.tcprt");
        Assert.False(File.Exists(bNew.File(newName)));
        Assert.Equal(0, back.Downloaded);
    }

    // ------------------------------------------------------------------ 8

    [Fact]
    public async Task Criterion8_ExportZip_FilesWithPaths_ManifestVersionsAndHashesMatchContent()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var imp = await ImportAsync(a);
        await a.OpenAsync(imp.Head);
        await a.CheckOutAsync(SourceKit.Body);
        a.Edit(SourceKit.Body);
        await a.CheckinAsync("корпус v2"); // в последней версии корпуса другой хеш — выгрузка v1 сборки должна взять v1

        // Выгружает читатель: любой пользователь может выгрузить комплект.
        var reader = await fx.NewUserAsync("reader", UserRole.Reader);
        var rc = await Computer.LoginAsync(fx, _dir, reader, "reader");
        var zip = _dir.Path("Выгрузка", "Изделие v1.zip");
        await rc.Service.ExportAsync(imp.Head, 1, includeDerived: true, zip);

        var kit = await a.Api.GetKitAsync(imp.Head, 1);
        using (var archive = ZipFile.OpenRead(zip))
        {
            var manifestEntry = archive.GetEntry(ExportManifest.FileName) ?? throw new Xunit.Sdk.XunitException("Нет manifest.json.");
            ExportManifest manifest;
            await using (var s = manifestEntry.Open())
                manifest = (await JsonSerializer.DeserializeAsync<ExportManifest>(s, PdmJson.Options))!;
            Assert.Equal(imp.Head, manifest.HeadDocumentId);
            Assert.Equal(1, manifest.HeadVersion);
            Assert.Equal(reader.Login, manifest.CreatedBy);
            Assert.Equal(kit.Items.Count, manifest.Files.Count(f => !f.Derived));
            Assert.Equal(kit.Items.Count + 1, archive.Entries.Count);
            foreach (var f in manifest.Files)
            {
                var item = kit.Items.Single(i => i.DocumentId == f.DocumentId);
                Assert.Equal(item.Version, f.Version);
                Assert.Equal(item.RelPath, f.RelPath);
                Assert.Equal(item.Sha256, f.Sha256);
                var entry = archive.GetEntry(f.RelPath) ?? throw new Xunit.Sdk.XunitException($"В архиве нет «{f.RelPath}».");
                await using var content = entry.Open();
                var bytes = new MemoryStream();
                await content.CopyToAsync(bytes);
                Assert.Equal(f.Size, bytes.Length);
                bytes.Position = 0;
                Assert.Equal(f.Sha256, Sha256Hex.Compute(bytes));
            }
            Assert.Equal(1, manifest.Files.Single(f => f.DocumentId == imp[SourceKit.Body]).Version);
        }

        var journal = await a.Api.GetDocumentJournalAsync(imp.Head);
        Assert.Contains(journal, j => j.Action == JournalActions.Export && j.UserLogin == reader.Login && j.Version == 1);
    }
}
