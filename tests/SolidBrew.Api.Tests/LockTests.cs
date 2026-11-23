using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

[Collection(PdmCollection.Name)]
public sealed class LockTests(PdmServerFixture fx)
{
    private async Task<(Guid Id, string RelPath)> PublishPartAsync(PdmApiClient designer, PdmApiClient admin)
    {
        var project = await fx.NewProjectAsync(admin);
        var relPath = "Детали/Корпус.prt";
        var item = await NewAsync(designer, "p", relPath, DocumentKind.Part, "lock-part-" + Guid.NewGuid());
        var result = await designer.CheckinAsync(Request(project.Id, item));
        return (result.Items[0].DocumentId, relPath);
    }

    [Fact]
    public async Task FiftyParallelLocks_ExactlyOneWins()
    {
        var admin = await fx.AdminAsync();
        var (owner, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (id, _) = await PublishPartAsync(owner, admin);

        var users = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => fx.NewUserAsync(UserRole.Designer, admin)));

        var results = await Task.WhenAll(users.Select(async u =>
        {
            try
            {
                return (User: u.User, Lock: await u.Client.LockAsync(id), Error: (PdmApiException?)null);
            }
            catch (PdmApiException ex)
            {
                return (User: u.User, Lock: (LockDto?)null, Error: ex);
            }
        }));

        var winners = results.Where(r => r.Lock is not null).ToList();
        var winner = Assert.Single(winners);
        Assert.Equal(winner.User.Id, winner.Lock!.UserId);
        var losers = results.Where(r => r.Error is not null).ToList();
        Assert.Equal(49, losers.Count);
        Assert.All(losers, l =>
        {
            Assert.Equal(409, l.Error!.StatusCode);
            Assert.Equal(winner.User.Id, l.Error.Error!.Lock!.UserId);
            Assert.Equal(ProblemCodes.LockedByOther, l.Error.Error.Problems.Single().Code);
            Assert.Contains("взят в работу пользователем", l.Error.Message);
        });
    }

    [Fact]
    public async Task Lock_IsIdempotent_UnlockOnlyOwn()
    {
        var admin = await fx.AdminAsync();
        var (a, userA) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (b, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (id, _) = await PublishPartAsync(a, admin);

        var first = await a.LockAsync(id);
        var second = await a.LockAsync(id);
        Assert.Equal(first.At, second.At);

        var ex = await Assert.ThrowsAsync<PdmApiException>(() => b.UnlockAsync(id));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(userA.Id, ex.Error!.Lock!.UserId);

        await a.UnlockAsync(id);
        Assert.Null((await a.GetDocumentAsync(id)).Document.Lock);
        await b.LockAsync(id);
    }

    [Fact]
    public async Task ForceUnlock_NotifiesOwner_AndJournals()
    {
        var admin = await fx.AdminAsync();
        var (owner, ownerUser) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (id, relPath) = await PublishPartAsync(owner, admin);
        await owner.LockAsync(id);

        Assert.Contains(await admin.GetLocksAsync(), l => l.DocumentId == id && l.UserId == ownerUser.Id);
        await admin.ForceUnlockAsync(id);
        Assert.DoesNotContain(await admin.GetLocksAsync(), l => l.DocumentId == id);

        var notifications = await owner.GetNotificationsAsync();
        var note = Assert.Single(notifications);
        Assert.False(note.Seen);
        Assert.Contains(relPath, note.Text);
        await owner.MarkNotificationsSeenAsync();
        Assert.All(await owner.GetNotificationsAsync(), n => Assert.True(n.Seen));

        var journal = await owner.GetDocumentJournalAsync(id);
        Assert.Contains(journal, j => j.Action == JournalActions.ForceUnlock && j.UserLogin == "admin");
        Assert.Contains(await admin.GetJournalAsync(50), j => j.Action == JournalActions.ForceUnlock && j.DocumentId == id);

        var designerEx = await Assert.ThrowsAsync<PdmApiException>(() => owner.ForceUnlockAsync(id));
        Assert.Equal(403, designerEx.StatusCode);
    }

    [Fact]
    public async Task UnusedDocument_CannotBeLocked_AndHiddenFromSearch()
    {
        var admin = await fx.AdminAsync();
        var (a, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (b, _) = await fx.NewUserAsync(UserRole.Designer, admin);
        var (id, _) = await PublishPartAsync(a, admin);
        var projectId = (await a.GetDocumentAsync(id)).Document.ProjectId;

        await b.LockAsync(id);
        var busy = await Assert.ThrowsAsync<PdmApiException>(() => a.MarkUnusedAsync(id, true));
        Assert.Equal(409, busy.StatusCode);
        await b.UnlockAsync(id);

        var summary = await a.MarkUnusedAsync(id, true);
        Assert.Equal(DocumentStatus.Unused, summary.Status);
        var ex = await Assert.ThrowsAsync<PdmApiException>(() => a.LockAsync(id));
        Assert.Equal(422, ex.StatusCode);

        Assert.DoesNotContain(await a.SearchDocumentsAsync(projectId: projectId), d => d.Id == id);
        Assert.Contains(await a.SearchDocumentsAsync(projectId: projectId, includeUnused: true), d => d.Id == id);

        await a.MarkUnusedAsync(id, false);
        await a.LockAsync(id);
    }
}
