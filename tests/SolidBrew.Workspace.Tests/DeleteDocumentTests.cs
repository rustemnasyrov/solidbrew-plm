using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

/// <summary>Удаление документа администратором: сервер и рабочая папка.</summary>
public class DeleteDocumentTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Unreferenced_Deleted_FileAndEntryRemoved()
    {
        var c = _env.For("admin");
        var (head, _) = await _env.ImportSourceAsync(c);
        await c.OpenAsync(head);
        var drawing = _env.DocId("Чертежи/Корпус.tcdrw");
        await c.OpenAsync(drawing);
        var file = c.File("Чертежи/Корпус.tcdrw");
        Assert.True(File.Exists(file));

        var r = await c.Service.DeleteDocumentAsync(c.WorkRoot, drawing);

        Assert.True(r.Success, r.Message);
        Assert.Equal(DocumentStatus.Deleted, _env.Server.Doc(drawing).Status);
        Assert.False(File.Exists(file));
        Assert.Null(c.State.FindByDocument(drawing));
    }

    [Fact]
    public async Task Referenced_Refused_NothingChanged()
    {
        var c = _env.For("admin");
        var (head, _) = await _env.ImportSourceAsync(c);
        await c.OpenAsync(head);
        var shaft = _env.DocId("Детали/Вал.tcprt");

        var r = await c.Service.DeleteDocumentAsync(c.WorkRoot, shaft);

        Assert.False(r.Success);
        Assert.Contains("не используется", r.Message);
        Assert.Contains(r.Problems, p => p.Code == ProblemCodes.ReferencedByPublished);
        Assert.Equal(DocumentStatus.Active, _env.Server.Doc(shaft).Status);
        Assert.True(File.Exists(c.File("Детали/Вал.tcprt")));
        Assert.NotNull(c.State.FindByDocument(shaft));
    }

    [Fact]
    public async Task LocalEditsKept_AsDraft()
    {
        var c = _env.For("admin");
        var (head, _) = await _env.ImportSourceAsync(c);
        await c.OpenAsync(head);
        var drawing = _env.DocId("Чертежи/Корпус.tcdrw");
        await c.OpenAsync(drawing);
        await c.CheckOutAsync(drawing);
        var file = c.File("Чертежи/Корпус.tcdrw");
        TestCadWriter.AppendGeometry(file, "правка");

        var r = await c.Service.DeleteDocumentAsync(c.WorkRoot, drawing);

        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(file)); // правки не потеряны
        Assert.False(Client.IsReadOnly(file));
        Assert.Contains(r.Problems, p => p.Message.Contains("черновик"));
        Assert.Null(c.State.FindByDocument(drawing));
    }

    [Fact]
    public async Task Designer_Refused()
    {
        var c = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(c);
        var drawing = _env.DocId("Чертежи/Корпус.tcdrw");

        var r = await c.Service.DeleteDocumentAsync(null, drawing);

        Assert.False(r.Success);
        Assert.Contains("администратор", r.Message);
        Assert.Equal(DocumentStatus.Active, _env.Server.Doc(drawing).Status);
    }
}
