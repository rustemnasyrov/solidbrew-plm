using SolidBrew.Core.Contracts;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

/// <summary>Комплект с чертежами (<c>includeDrawings</c>) и корневые изделия (<c>roots</c>, <see cref="DocumentSummaryDto.UsedInCount"/>).</summary>
[Collection(PdmCollection.Name)]
public sealed class KitDrawingsTests(PdmServerFixture fx)
{
    [Fact]
    public async Task Kit_IncludeDrawings_AddsLatestDrawingsOfKitDocuments_AndTheirMissingDependencies()
    {
        var kit = await CheckinTests.PublishKitAsync(fx);
        var d = kit.Designer;
        var salt = Guid.NewGuid().ToString("N");

        // Чертёж детали «Вал» (ещё и с шайбой вне комплекта) и неиспользуемый чертёж «Втулки».
        var washer = await NewAsync(d, "w", "Детали/Шайба.prt", DocumentKind.Part, "шайба " + salt);
        var shaftDrw = await NewAsync(d, "sd", "Чертежи/Вал.drw", DocumentKind.Drawing, "чертёж вала v1 " + salt,
            RefPublished(@"C:\work\Детали\Вал.prt", kit.Part1, 1), RefTemp(@"C:\work\Детали\Шайба.prt", "w"));
        var bushDrw = await NewAsync(d, "bd", "Чертежи/Втулка.drw", DocumentKind.Drawing, "чертёж втулки " + salt,
            RefPublished(@"C:\work\Детали\Втулка.prt", kit.Part2, 1));
        var r = await d.CheckinAsync(Request(kit.Project.Id, washer, shaftDrw, bushDrw));
        Guid Id(string key) => r.Items.Single(i => i.TempKey == key).DocumentId;
        var (washerId, shaftDrwId, bushDrwId) = (Id("w"), Id("sd"), Id("bd"));
        await d.MarkUnusedAsync(bushDrwId, true);

        // По умолчанию — как раньше: только комплект от сборки вниз.
        var plain = await d.GetKitAsync(kit.Asm, 1);
        Assert.Equal(3, plain.Items.Count);
        Assert.All(plain.Items, i => Assert.False(i.IsAttachedDrawing));

        var full = await d.GetKitAsync(kit.Asm, 1, includeDrawings: true);
        Assert.Equal(kit.Asm, full.Items[0].DocumentId);
        Assert.Equal(6, full.Items.Count);
        Assert.Equal(new[] { kit.Asm, kit.Part1, kit.Part2 }.Order(), full.Items.Where(i => !i.IsAttachedDrawing).Select(i => i.DocumentId).Order());
        Assert.Equal(new[] { kit.Drawing, shaftDrwId, washerId }.Order(), full.Items.Where(i => i.IsAttachedDrawing).Select(i => i.DocumentId).Order());
        Assert.DoesNotContain(full.Items, i => i.DocumentId == bushDrwId); // «не используется» — не добавляется
        Assert.All(full.Items, i => Assert.False(i.DrawingModelVersionMismatch));
        Assert.Contains(full.Edges, e => e.ParentDocumentId == shaftDrwId && e.ChildDocumentId == kit.Part1 && e.ChildVersion == 1);
        Assert.Contains(full.Edges, e => e.ParentDocumentId == shaftDrwId && e.ChildDocumentId == washerId);
        Assert.Contains(full.Edges, e => e.ParentDocumentId == kit.Drawing && e.ChildDocumentId == kit.Asm);

        // Новая версия детали и чертежа к ней: старая сборка v1 получает последний чертёж, сданный с другой версией модели.
        await d.LockAsync(kit.Part1);
        await d.CheckinAsync(Request(kit.Project.Id, await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "вал v2 " + salt)));
        await d.LockAsync(shaftDrwId);
        await d.CheckinAsync(Request(kit.Project.Id, await EditAsync(d, shaftDrwId, 1, "Чертежи/Вал.drw", DocumentKind.Drawing, "чертёж вала v2 " + salt,
            RefPublished(@"C:\work\Детали\Вал.prt", kit.Part1, 2))));

        var old = await d.GetKitAsync(kit.Asm, 1, includeDrawings: true);
        var drawing = old.Items.Single(i => i.DocumentId == shaftDrwId);
        Assert.Equal(2, drawing.Version);
        Assert.True(drawing.IsAttachedDrawing);
        Assert.True(drawing.DrawingModelVersionMismatch);
        Assert.Equal(1, old.Items.Single(i => i.DocumentId == kit.Part1).Version); // деталь не добавляется второй версией
        Assert.Single(old.Items, i => i.DocumentId == kit.Part1);
        Assert.DoesNotContain(old.Items, i => i.DocumentId == washerId); // чертёж v2 на шайбу не ссылается
        Assert.Contains(old.Edges, e => e.ParentDocumentId == shaftDrwId && e.ChildDocumentId == kit.Part1 && e.ChildVersion == 2);
        Assert.False(old.Items.Single(i => i.DocumentId == kit.Drawing).DrawingModelVersionMismatch);
    }

    [Fact]
    public async Task Search_Roots_OnlyAssembliesNotUsedByOtherLatestVersions_UsedInCountIgnoresDrawings()
    {
        var admin = await fx.AdminAsync();
        var project = await fx.NewProjectAsync(admin);
        var (d, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var salt = Guid.NewGuid().ToString("N");

        var part = await NewAsync(d, "p", "Детали/Ручка.prt", DocumentKind.Part, "ручка " + salt);
        var cockpit = await NewAsync(d, "c", "Кокпит.asm", DocumentKind.Assembly, "кокпит " + salt, RefTemp("Ручка.prt", "p"));
        var trainer1 = await NewAsync(d, "t1", "Тренажёр-1.asm", DocumentKind.Assembly, "тренажёр 1 " + salt, RefTemp("Кокпит.asm", "c"));
        var trainer2 = await NewAsync(d, "t2", "Тренажёр-2.asm", DocumentKind.Assembly, "тренажёр 2 " + salt, RefTemp("Кокпит.asm", "c"));
        var drawing = await NewAsync(d, "dt", "Тренажёр-1.drw", DocumentKind.Drawing, "чертёж " + salt, RefTemp("Тренажёр-1.asm", "t1"));
        var lone = await NewAsync(d, "l", "Стенд.asm", DocumentKind.Assembly, "стенд " + salt, RefTemp("Кокпит.asm", "c"));
        var r = await d.CheckinAsync(Request(project.Id, part, cockpit, trainer1, trainer2, drawing, lone));
        Guid Id(string key) => r.Items.Single(i => i.TempKey == key).DocumentId;
        await d.MarkUnusedAsync(Id("l"), true); // неиспользуемый документ не корень и не считается применением

        var all = (await d.SearchDocumentsAsync(projectId: project.Id, includeUnused: true)).ToDictionary(x => x.Id);
        Assert.Equal(2, all[Id("c")].UsedInCount);
        Assert.Equal(1, all[Id("p")].UsedInCount);
        Assert.Equal(0, all[Id("t1")].UsedInCount); // чертёж — не применение
        Assert.Equal(0, all[Id("dt")].UsedInCount);

        var roots = await d.SearchDocumentsAsync(projectId: project.Id, roots: true);
        Assert.Equal(new[] { Id("t1"), Id("t2") }.Order(), roots.Select(x => x.Id).Order());
        Assert.Equal(Id("t2"), Assert.Single(await d.SearchDocumentsAsync(q: "Тренажёр-2", projectId: project.Id, roots: true)).Id);

        // Новая версия тренажёра-2 без кокпита: кокпит применяется только в тренажёре-1 (по последним версиям).
        await d.LockAsync(Id("t2"));
        await d.CheckinAsync(Request(project.Id, await EditAsync(d, Id("t2"), 1, "Тренажёр-2.asm", DocumentKind.Assembly, "тренажёр 2 v2 " + salt)));
        Assert.Equal(1, (await d.GetDocumentAsync(Id("c"))).Document.UsedInCount);
    }
}
