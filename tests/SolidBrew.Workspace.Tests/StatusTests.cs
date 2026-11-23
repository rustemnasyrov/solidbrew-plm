using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

public class StatusTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Status_ServerSearchLimitSmallerThanWorkspace_MissingSummariesFetchedOneByOne()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var body = _env.DocId("Детали/Корпус.tcprt");
        await iv.CheckOutAsync(body);

        // Сервер отдаёт поиском меньше записей, чем учтено в папке: остальные должны быть дочитаны.
        _env.Server.SearchLimitCap = 2;
        var status = await iv.Service.GetStatusAsync(iv.WorkRoot);

        Assert.True(status.ServerChecked);
        Assert.Equal(5, status.Files.Count);
        Assert.All(status.Files, f =>
        {
            Assert.Equal(1, f.LatestVersion);
            Assert.NotEqual(LockState.Unknown, f.Lock);
            Assert.Equal(DocumentStatus.Active, f.ServerStatus);
        });
        Assert.Equal(LockState.LockedByMe, status.FindByDocument(body)!.Lock);
        Assert.Single(status.Files, f => f.Lock == LockState.LockedByMe);
        Assert.Empty(status.Messages);
    }
}
