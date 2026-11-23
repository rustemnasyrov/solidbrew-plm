using Dapper;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

[Collection(PdmCollection.Name)]
public sealed class CheckinTests(PdmServerFixture fx)
{
    /// <summary>Сборка из двух деталей и чертёж сборки, сданные одной сдачей.</summary>
    internal sealed record Kit(ProjectDto Project, PdmApiClient Designer, UserDto User, PdmApiClient Admin,
        Guid Asm, Guid Part1, Guid Part2, Guid Drawing, CheckinItem AsmItem);

    internal static async Task<Kit> PublishKitAsync(PdmServerFixture fx)
    {
        var admin = await fx.AdminAsync();
        var project = await fx.NewProjectAsync(admin);
        var (designer, user) = await fx.NewUserAsync(UserRole.Designer, admin);
        var salt = Guid.NewGuid().ToString("N");

        var part1 = await NewAsync(designer, "p1", "Детали/Вал.prt", DocumentKind.Part, "вал v1 " + salt);
        var part2 = await NewAsync(designer, "p2", "Детали/Втулка.prt", DocumentKind.Part, "втулка v1 " + salt);
        var asm = await NewAsync(designer, "asm", "Узел.asm", DocumentKind.Assembly, "узел v1 " + salt,
            RefTemp(@"C:\work\Детали\Вал.prt", "p1"), RefTemp(@"C:\work\Детали\Втулка.prt", "p2"));
        var drw = await NewAsync(designer, "drw", "Узел.drw", DocumentKind.Drawing, "чертёж v1 " + salt, RefTemp("Узел.asm", "asm"));
        drw.Derived.Add(await DerivedAsync(designer, "Узел.pdf", "pdf v1 " + salt));

        var result = await designer.CheckinAsync(Request(project.Id, drw, asm, part1, part2));
        Guid Id(string key) => result.Items.Single(i => i.TempKey == key).DocumentId;
        Assert.All(result.Items, i => Assert.Equal(1, i.Version));
        return new Kit(project, designer, user, admin, Id("asm"), Id("p1"), Id("p2"), Id("drw"), asm);
    }

    internal static async Task<DerivedFileDto> DerivedAsync(PdmApiClient client, string name, string content)
    {
        var (sha, size) = await PutAsync(client, content);
        return new DerivedFileDto { FileName = name, Sha256 = sha, Size = size };
    }

    private static async Task<Dictionary<Guid, (int Latest, Guid? LockUser)>> SnapshotAsync(PdmApiClient client, Guid projectId) =>
        (await client.SearchDocumentsAsync(projectId: projectId, includeUnused: true))
            .ToDictionary(d => d.Id, d => (d.LatestVersion, d.Lock?.UserId));

    private static async Task<PdmApiException> AssertRejectedAsync(PdmApiClient client, CheckinRequest request, Guid projectId, string code)
    {
        var before = await SnapshotAsync(client, projectId);
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => client.CheckinAsync(request));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains(ex.Error!.Problems, p => p.Code == code);
        Assert.Contains("Ничего не опубликовано", ex.Message);
        var after = await SnapshotAsync(client, projectId);
        Assert.Equal(before, after); // ни новых документов, ни версий; блокировки на месте
        return ex;
    }

    [Fact]
    public async Task NewAssembly_WithPartsAndDrawing_InOneCheckin()
    {
        var kit = await PublishKitAsync(fx);
        var detail = await kit.Designer.GetDocumentAsync(kit.Asm);
        Assert.Equal(DocumentKind.Assembly, detail.Document.Kind);
        Assert.Equal("Узел", detail.Document.Designation);
        Assert.Equal("Узел.asm", detail.Document.FileName);
        var v1 = Assert.Single(detail.Versions);
        Assert.Equal("тест", v1.Comment);
        Assert.Equal(2, v1.References.Count);
        Assert.Contains(v1.References, r => r.ChildDocumentId == kit.Part1 && r.ChildVersion == 1 && r.ChildRelPath == "Детали/Вал.prt" && r.ChildLatestVersion == 1);
        Assert.Equal("По умолчанию", Assert.Single(v1.Configurations));
        Assert.Equal("Узел", v1.Properties[""]["Обозначение"]);

        var drawing = await kit.Designer.GetDocumentAsync(kit.Drawing);
        Assert.Equal("Узел.pdf", Assert.Single(drawing.Versions[0].Derived).FileName);

        var tree = await kit.Designer.GetKitAsync(kit.Drawing, 1);
        Assert.Equal(4, tree.Items.Count);
        Assert.Equal(kit.Drawing, tree.Items[0].DocumentId);
        Assert.Equal(3, tree.Edges.Count);
        Assert.Empty(tree.Unresolved);

        // Поиск: по обозначению, имени файла и автору; блокировки сняты.
        var found = await kit.Designer.SearchDocumentsAsync(q: "втулк", projectId: kit.Project.Id);
        Assert.Equal(kit.Part2, Assert.Single(found).Id);
        var byAuthor = await kit.Designer.SearchDocumentsAsync(author: kit.User.Login, projectId: kit.Project.Id);
        Assert.Equal(4, byAuthor.Count);
        Assert.All(byAuthor, d => Assert.Null(d.Lock));
        Assert.Contains(await kit.Designer.GetDocumentJournalAsync(kit.Asm), j => j.Action == JournalActions.Checkin && j.Version == 1);
    }

    [Fact]
    public async Task ChangeOnePart_ThenDrawingOnly_DoesNotTouchModel()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;

        await d.LockAsync(kit.Part1);
        var part = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "вал v2 " + Guid.NewGuid());
        var result = await d.CheckinAsync(Request(kit.Project.Id, part));
        Assert.Equal(2, result.Items.Single().Version);
        Assert.Null((await d.GetDocumentAsync(kit.Part1)).Document.Lock);
        Assert.Equal(1, (await d.GetDocumentAsync(kit.Asm)).Document.LatestVersion);

        await d.LockAsync(kit.Drawing);
        var drawing = await EditAsync(d, kit.Drawing, 1, "Узел.drw", DocumentKind.Drawing, "чертёж v2 " + Guid.NewGuid(), RefPublished("Узел.asm", kit.Asm, 1));
        result = await d.CheckinAsync(Request(kit.Project.Id, drawing));
        Assert.Equal(2, result.Items.Single().Version);
        Assert.Equal(1, (await d.GetDocumentAsync(kit.Asm)).Document.LatestVersion);
        Assert.Equal(2, (await d.GetDocumentAsync(kit.Part1)).Document.LatestVersion);
        Assert.Equal(1, (await d.GetDocumentAsync(kit.Part2)).Document.LatestVersion);
    }

    [Fact]
    public async Task SameBytes_NewReference_CreatesNewAssemblyVersion()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        await d.CheckinAsync(Request(kit.Project.Id, await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "вал v2 " + Guid.NewGuid())));

        // Сборка не менялась побайтно, но теперь ссылается на деталь v2 (ТЗ 7.6).
        await d.LockAsync(kit.Asm);
        var asm = new CheckinItem
        {
            DocumentId = kit.Asm, BaseVersion = 1, RelPath = "Узел.asm", Sha256 = kit.AsmItem.Sha256, Size = kit.AsmItem.Size,
            Cad = Cad, Kind = DocumentKind.Assembly,
            References = [RefPublished(@"C:\work\Детали\Вал.prt", kit.Part1, 2), RefPublished(@"C:\work\Детали\Втулка.prt", kit.Part2, 1)],
        };
        var result = await d.CheckinAsync(Request(kit.Project.Id, asm));
        Assert.Equal(2, result.Items.Single().Version);

        var detail = await d.GetDocumentAsync(kit.Asm);
        Assert.Equal(detail.Versions[0].Sha256, detail.Versions[1].Sha256);
        Assert.Equal(2, detail.Versions[0].Number);

        var oldKit = await d.GetKitAsync(kit.Asm, 1);
        var oldPart = oldKit.Items.Single(i => i.DocumentId == kit.Part1);
        Assert.Equal(1, oldPart.Version);
        Assert.Equal(2, oldPart.LatestVersion);
        Assert.Equal(2, (await d.GetKitAsync(kit.Asm, 2)).Items.Single(i => i.DocumentId == kit.Part1).Version);

        var whereUsed = await d.GetWhereUsedAsync(kit.Part1, 1);
        Assert.Equal(2, whereUsed.Items.Count);
        var usesOld = whereUsed.Items.Single(i => i.ChildVersion == 1);
        Assert.Equal(1, usesOld.ParentVersion);
        Assert.False(usesOld.IsParentLatest);
        Assert.False(usesOld.UsesLatestChild);
        var usesNew = whereUsed.Items.Single(i => i.ChildVersion == 2);
        Assert.Equal(2, usesNew.ParentVersion);
        Assert.True(usesNew.IsParentLatest);
        Assert.True(usesNew.UsesLatestChild);
    }

    [Fact]
    public async Task Rename_PartAndReferencingAssembly_InOneCheckin()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part2);
        await d.LockAsync(kit.Asm);

        var part = await EditAsync(d, kit.Part2, 1, "Детали/Втулка-новая.prt", DocumentKind.Part, "втулка v1 переименована " + Guid.NewGuid());
        var asm = await EditAsync(d, kit.Asm, 1, "Узел.asm", DocumentKind.Assembly, "узел v2 " + Guid.NewGuid(),
            RefPublished(@"C:\work\Детали\Вал.prt", kit.Part1, 1), RefInCheckin(@"C:\work\Детали\Втулка-новая.prt", kit.Part2));
        var result = await d.CheckinAsync(Request(kit.Project.Id, part, asm));
        Assert.All(result.Items, i => Assert.Equal(2, i.Version));

        var partDetail = await d.GetDocumentAsync(kit.Part2);
        Assert.Equal("Детали/Втулка-новая.prt", partDetail.Document.RelPath);
        Assert.Equal("Детали/Втулка.prt", partDetail.Versions[1].RelPath);

        var oldKit = await d.GetKitAsync(kit.Asm, 1);
        Assert.Equal("Детали/Втулка.prt", oldKit.Items.Single(i => i.DocumentId == kit.Part2).RelPath);
        var newKit = await d.GetKitAsync(kit.Asm, 2);
        var newPart = newKit.Items.Single(i => i.DocumentId == kit.Part2);
        Assert.Equal("Детали/Втулка-новая.prt", newPart.RelPath);
        Assert.Equal(2, newPart.Version);

        // Старый путь свободен: новый документ может его занять.
        var newcomer = await NewAsync(d, "n", "Детали/Втулка.prt", DocumentKind.Part, "новая втулка " + Guid.NewGuid());
        await d.CheckinAsync(Request(kit.Project.Id, newcomer));
    }

    [Fact]
    public async Task SwapPaths_WithinOneCheckin_IsAllowed()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        await d.LockAsync(kit.Part2);
        var a = await EditAsync(d, kit.Part1, 1, "Детали/Втулка.prt", DocumentKind.Part, "swap a " + Guid.NewGuid());
        var b = await EditAsync(d, kit.Part2, 1, "Детали/Вал.prt", DocumentKind.Part, "swap b " + Guid.NewGuid());
        await d.CheckinAsync(Request(kit.Project.Id, a, b));
        Assert.Equal("Детали/Втулка.prt", (await d.GetDocumentAsync(kit.Part1)).Document.RelPath);
    }

    [Fact]
    public async Task KeepLocks_KeepsExistingAndLocksNew()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        var part = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "keep " + Guid.NewGuid());
        var extra = await NewAsync(d, "e", "Детали/Шайба.prt", DocumentKind.Part, "шайба " + Guid.NewGuid());
        var request = Request(kit.Project.Id, part, extra);
        request.KeepLocks = true;
        var result = await d.CheckinAsync(request);
        Assert.Equal(kit.User.Id, (await d.GetDocumentAsync(kit.Part1)).Document.Lock?.UserId);
        Assert.Equal(kit.User.Id, (await d.GetDocumentAsync(result.Items.Single(i => i.TempKey == "e").DocumentId)).Document.Lock?.UserId);
    }

    [Fact]
    public async Task NotLocked_IsRejected()
    {
        var kit = await PublishKitAsync(fx);
        var part = await EditAsync(kit.Designer, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "x " + Guid.NewGuid());
        await AssertRejectedAsync(kit.Designer, Request(kit.Project.Id, part), kit.Project.Id, ProblemCodes.NotLocked);
    }

    [Fact]
    public async Task LockedByOther_IsRejected_WithLock()
    {
        var kit = await PublishKitAsync(fx);
        var (other, otherUser) = await fx.NewUserAsync(UserRole.Designer, kit.Admin);
        await other.LockAsync(kit.Part1);
        var part = await EditAsync(kit.Designer, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "x " + Guid.NewGuid());
        var ex = await AssertRejectedAsync(kit.Designer, Request(kit.Project.Id, part), kit.Project.Id, ProblemCodes.LockedByOther);
        Assert.Equal(otherUser.Id, ex.Error!.Lock!.UserId);
        Assert.Equal(otherUser.Id, (await kit.Designer.GetDocumentAsync(kit.Part1)).Document.Lock!.UserId);
    }

    [Fact]
    public async Task StaleBaseVersion_IsRejected_LocksKept()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        var v2 = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "v2 " + Guid.NewGuid());
        var keep = Request(kit.Project.Id, v2);
        keep.KeepLocks = true;
        await d.CheckinAsync(keep);

        var stale = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "v3 " + Guid.NewGuid());
        var ex = await AssertRejectedAsync(d, Request(kit.Project.Id, stale), kit.Project.Id, ProblemCodes.StaleBaseVersion);
        Assert.Contains("v1", ex.Error!.Problems[0].Message);
        Assert.Equal(kit.User.Id, (await d.GetDocumentAsync(kit.Part1)).Document.Lock?.UserId);
    }

    [Fact]
    public async Task MissingDependency_IsRejected()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        var asm = await NewAsync(d, "a", "Другой.asm", DocumentKind.Assembly, "dep " + Guid.NewGuid(), RefPublished("Вал.prt", kit.Part1, 99));
        await AssertRejectedAsync(d, Request(kit.Project.Id, asm), kit.Project.Id, ProblemCodes.MissingDependency);

        var asm2 = await NewAsync(d, "a", "Другой.asm", DocumentKind.Assembly, "dep2 " + Guid.NewGuid(), RefTemp("Нет.prt", "нет-такого"));
        await AssertRejectedAsync(d, Request(kit.Project.Id, asm2), kit.Project.Id, ProblemCodes.MissingDependency);

        var asm3 = await NewAsync(d, "a", "Другой.asm", DocumentKind.Assembly, "dep3 " + Guid.NewGuid(), RefInCheckin("Вал.prt", kit.Part1));
        await AssertRejectedAsync(d, Request(kit.Project.Id, asm3), kit.Project.Id, ProblemCodes.MissingDependency);
    }

    [Fact]
    public async Task UnresolvedReference_IsRejected_UnlessAllowed()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        var asm = await NewAsync(d, "a", "Неполная.asm", DocumentKind.Assembly, "unres " + Guid.NewGuid(), RefMissing(@"\\server\share\Потерянная.prt"));
        await AssertRejectedAsync(d, Request(kit.Project.Id, asm), kit.Project.Id, ProblemCodes.UnresolvedReference);

        var request = Request(kit.Project.Id, asm);
        request.AllowUnresolvedReferences = true;
        var result = await d.CheckinAsync(request);
        var tree = await d.GetKitAsync(result.Items[0].DocumentId, 1);
        Assert.Equal(@"\\server\share\Потерянная.prt", Assert.Single(tree.Unresolved).RefPath);
    }

    [Fact]
    public async Task MissingObject_IsRejected()
    {
        var kit = await PublishKitAsync(fx);
        var data = System.Text.Encoding.UTF8.GetBytes("не загружено " + Guid.NewGuid());
        var item = new CheckinItem { TempKey = "m", RelPath = "Без содержимого.prt", Sha256 = Sha(data), Size = data.Length, Cad = Cad, Kind = DocumentKind.Part };
        await AssertRejectedAsync(kit.Designer, Request(kit.Project.Id, item), kit.Project.Id, ProblemCodes.MissingObject);
    }

    [Fact]
    public async Task DuplicatePath_IsRejected()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        var clash = await NewAsync(d, "c", "детали/вал.PRT", DocumentKind.Part, "clash " + Guid.NewGuid());
        await AssertRejectedAsync(d, Request(kit.Project.Id, clash), kit.Project.Id, ProblemCodes.DuplicatePath);

        var a = await NewAsync(d, "a", "Новое/Одно.prt", DocumentKind.Part, "dup a " + Guid.NewGuid());
        var b = await NewAsync(d, "b", "Новое/Одно.prt", DocumentKind.Part, "dup b " + Guid.NewGuid());
        await AssertRejectedAsync(d, Request(kit.Project.Id, a, b), kit.Project.Id, ProblemCodes.DuplicatePath);
    }

    [Fact]
    public async Task LibraryReadOnly_IsRejected_ForDesigner()
    {
        var library = await fx.LibraryAsync();
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer);
        var item = await NewAsync(designer, "l", fx.Unique("Лист-") + ".prt", DocumentKind.Part, "lib " + Guid.NewGuid());
        await AssertRejectedAsync(designer, Request(library.Id, item), library.Id, ProblemCodes.LibraryReadOnly);
    }

    [Fact]
    public async Task NewerCadVersion_IsRejected_UnlessAccepted()
    {
        var kit = await PublishKitAsync(fx);
        var key = SettingKeys.WorkingVersion(Cad);
        await kit.Admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "2022" } });
        try
        {
            var item = await NewAsync(kit.Designer, "n", "Новая версия.prt", DocumentKind.Part, "newer " + Guid.NewGuid());
            item.CadVersion = "2024 SP1";
            item.CadVersionMajor = 2024;
            var ex = await AssertRejectedAsync(kit.Designer, Request(kit.Project.Id, item), kit.Project.Id, ProblemCodes.NewerCadVersion);
            Assert.Contains("2022", ex.Message);

            var request = Request(kit.Project.Id, item);
            request.AcceptNewerCadVersion = true;
            await kit.Designer.CheckinAsync(request);
        }
        finally
        {
            await kit.Admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "" } });
        }
    }

    [Fact]
    public async Task AllProblems_AreReportedTogether()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        var notLocked = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "all " + Guid.NewGuid());
        var unresolved = await NewAsync(d, "u", "Сборка2.asm", DocumentKind.Assembly, "all2 " + Guid.NewGuid(), RefMissing("Где-то.prt"));
        var duplicate = await NewAsync(d, "dup", "Узел.drw", DocumentKind.Drawing, "all3 " + Guid.NewGuid());
        var ex = await AssertRejectedAsync(d, Request(kit.Project.Id, notLocked, unresolved, duplicate), kit.Project.Id, ProblemCodes.NotLocked);
        Assert.Contains(ex.Error!.Problems, p => p.Code == ProblemCodes.UnresolvedReference && p.TempKey == "u");
        Assert.Contains(ex.Error.Problems, p => p.Code == ProblemCodes.DuplicatePath && p.TempKey == "dup");
        Assert.Contains("найдено проблем — 3", ex.Message);
    }

    [Fact]
    public async Task FailureInsideTransaction_PublishesNothing()
    {
        var kit = await PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        var part = await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "atom " + Guid.NewGuid());
        var newDoc = await NewAsync(d, "n", "Детали/Новая.prt", DocumentKind.Part, "atom new " + Guid.NewGuid());
        var before = await SnapshotAsync(d, kit.Project.Id);

        // 1) Нарушение отложенного ограничения уникальности пути при COMMIT.
        fx.Hooks.BeforeCommit = (connection, tx, ct) => connection.ExecuteAsync("""
            INSERT INTO documents (id, project_id, cad, kind, current_rel_path, rel_key, created_by)
            SELECT gen_random_uuid(), project_id, cad, kind, current_rel_path, rel_key, created_by
            FROM documents WHERE project_id = @projectId AND rel_key = @key
            """, new { projectId = kit.Project.Id, key = "ДЕТАЛИ/НОВАЯ.PRT" }, tx);
        try
        {
            var ex = await Assert.ThrowsAsync<PdmApiException>(() => d.CheckinAsync(Request(kit.Project.Id, part, newDoc)));
            Assert.Equal(422, ex.StatusCode);
            Assert.Equal(ProblemCodes.DuplicatePath, ex.Error!.Problems.Single().Code);
        }
        finally
        {
            fx.Hooks.BeforeCommit = null;
        }
        Assert.Equal(before, await SnapshotAsync(d, kit.Project.Id));

        // 2) Произвольный сбой перед COMMIT.
        fx.Hooks.BeforeCommit = (_, _, _) => throw new InvalidOperationException("внедрённый сбой");
        try
        {
            var ex = await Assert.ThrowsAsync<PdmApiException>(() => d.CheckinAsync(Request(kit.Project.Id, part, newDoc)));
            Assert.Equal(500, ex.StatusCode);
        }
        finally
        {
            fx.Hooks.BeforeCommit = null;
        }
        Assert.Equal(before, await SnapshotAsync(d, kit.Project.Id));
        Assert.Equal(kit.User.Id, (await d.GetDocumentAsync(kit.Part1)).Document.Lock?.UserId);

        // Без сбоя та же сдача проходит.
        var ok = await d.CheckinAsync(Request(kit.Project.Id, part, newDoc));
        Assert.Equal(2, ok.Items.Single(i => i.DocumentId == kit.Part1).Version);
    }
}
