using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

/// <summary>Удаление документа администратором (ТЗ 7.9): только без ссылок опубликованных версий, история сохраняется.</summary>
[Collection(PdmCollection.Name)]
public sealed class DeleteDocumentTests(PdmServerFixture fx)
{
    [Fact]
    public async Task Admin_DeletesUnreferenced_HiddenEverywhere_PathFreed_HistoryKept()
    {
        var admin = await fx.AdminAsync();
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var project = await fx.NewProjectAsync(admin);
        var content = "delete-part-" + Guid.NewGuid();
        var created = await designer.CheckinAsync(Request(project.Id, await NewAsync(designer, "p", "Детали/Лишняя.prt", DocumentKind.Part, content)));
        var id = created.Items[0].DocumentId;

        // Конструктор удалить не может.
        var denied = await Assert.ThrowsAsync<PdmApiException>(() => designer.DeleteDocumentAsync(id));
        Assert.Equal(403, denied.StatusCode);

        await admin.DeleteDocumentAsync(id);

        var detail = await admin.GetDocumentAsync(id);
        Assert.Equal(DocumentStatus.Deleted, detail.Document.Status);
        Assert.Single(detail.Versions); // версии не удаляются
        Assert.DoesNotContain(await admin.SearchDocumentsAsync(projectId: project.Id, includeUnused: true), d => d.Id == id);
        Assert.Empty(await admin.FindVersionsByHashAsync([Sha(System.Text.Encoding.UTF8.GetBytes(content))]));
        Assert.Contains(await admin.GetDocumentJournalAsync(id), j => j.Action == JournalActions.Deleted && j.UserLogin == "admin");

        // Взять, пометить, удалить повторно — нельзя; путь свободен для нового документа.
        Assert.Equal(422, (await Assert.ThrowsAsync<PdmApiException>(() => designer.LockAsync(id))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<PdmApiException>(() => designer.MarkUnusedAsync(id, true))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<PdmApiException>(() => admin.DeleteDocumentAsync(id))).StatusCode);
        var again = await designer.CheckinAsync(Request(project.Id, await NewAsync(designer, "p2", "Детали/Лишняя.prt", DocumentKind.Part, "new-" + Guid.NewGuid())));
        Assert.NotEqual(id, again.Items[0].DocumentId);
    }

    [Fact]
    public async Task Referenced_Refused_UntilParentDeleted()
    {
        var admin = await fx.AdminAsync();
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var project = await fx.NewProjectAsync(admin);
        var part = await NewAsync(designer, "p", "Детали/Вал.prt", DocumentKind.Part, "ref-part-" + Guid.NewGuid());
        var asm = await NewAsync(designer, "a", "Узел.asm", DocumentKind.Assembly, "ref-asm-" + Guid.NewGuid(), RefTemp(@"C:\x\Вал.prt", "p"));
        var result = await designer.CheckinAsync(Request(project.Id, part, asm));
        var partId = result.Items.Single(i => i.TempKey == "p").DocumentId;
        var asmId = result.Items.Single(i => i.TempKey == "a").DocumentId;

        var refused = await Assert.ThrowsAsync<PdmApiException>(() => admin.DeleteDocumentAsync(partId));
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal(ProblemCodes.ReferencedByPublished, refused.Error!.Problems.Single().Code);
        Assert.Contains("Узел.asm", refused.Message);
        Assert.Contains("не используется", refused.Message);
        Assert.Equal(DocumentStatus.Active, (await admin.GetDocumentAsync(partId)).Document.Status);

        // Сначала сборку, потом деталь: ссылки удалённой сборки не мешают.
        await admin.DeleteDocumentAsync(asmId);
        await admin.DeleteDocumentAsync(partId);
        Assert.Equal(DocumentStatus.Deleted, (await admin.GetDocumentAsync(partId)).Document.Status);
    }

    [Fact]
    public async Task LockedByOther_Refused_OwnLockReleased()
    {
        var admin = await fx.AdminAsync();
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var project = await fx.NewProjectAsync(admin);
        var id = (await designer.CheckinAsync(Request(project.Id, await NewAsync(designer, "p", "Деталь.prt", DocumentKind.Part, "lk-" + Guid.NewGuid())))).Items[0].DocumentId;

        await designer.LockAsync(id);
        var busy = await Assert.ThrowsAsync<PdmApiException>(() => admin.DeleteDocumentAsync(id));
        Assert.Equal(409, busy.StatusCode);
        Assert.Equal(ProblemCodes.LockedByOther, busy.Error!.Problems.Single().Code);
        await designer.UnlockAsync(id);

        await admin.LockAsync(id); // своя блокировка не мешает и снимается
        await admin.DeleteDocumentAsync(id);
        Assert.DoesNotContain(await admin.GetLocksAsync(), l => l.DocumentId == id);
    }
}
