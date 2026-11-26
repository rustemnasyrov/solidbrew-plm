using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.ViewModels;

namespace SolidBrew.Client.Tests;

/// <summary>
/// «Сдать в хранилище» нескольких файлов по ссылке после переименования папки проекта: ссылки деталей на общую исходную
/// модель записаны путём старой папки. Одно подтверждение на всю сдачу, модель загружается один раз.
/// </summary>
public sealed class LinkedBatchTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string OldPath(params string[] parts) => Path.Combine(new[] { _env.Path("Старая") }.Concat(parts).ToArray());
    private string NewPath(params string[] parts) => Path.Combine(new[] { _env.Path("Новая") }.Concat(parts).ToArray());

    /// <summary>Сборка учтена в PDM, обе детали оставлены ссылками (как «не учтён в PDM» в дереве).</summary>
    private async Task<(AppSession Session, Guid Head)> SetUpAsync()
    {
        TestCadWriter.Create(NewPath("Models", "Светофильтры.tcprt"), geometry: "исходная модель");
        foreach (var part in new[] { "Деталь1.tcprt", "Деталь2.tcprt" })
            TestCadWriter.Create(NewPath("Козырек", part), new[] { OldPath("Models", "Светофильтры.tcprt") }, geometry: part);
        var asm = TestCadWriter.Create(NewPath("Козырек", "Козырек.tcasm"),
            new[] { OldPath("Козырек", "Деталь1.tcprt"), OldPath("Козырек", "Деталь2.tcprt") });

        var s = _env.Session("ivanov");
        using var report = await s.Workspace.PrepareImportAsync(asm);
        foreach (var d in report.Documents.Where(d => !d.IsHead)) d.Include = false;
        report.Recompute();
        var result = await s.Workspace.ExecuteImportAsync(report, _env.Project.Id, null);
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        await _env.OpenDirectAsync(s, result.HeadDocumentId);
        return (s, result.HeadDocumentId);
    }

    private List<WorkflowController.LinkedFile> Files(Guid head) => new[] { "Деталь1.tcprt", "Деталь2.tcprt" }
        .Select(p => new WorkflowController.LinkedFile(head, _env.Project.Id, "Козырек.tcasm", "Козырек", OldPath("Козырек", p), NewPath("Козырек", p)))
        .ToList();

    [Fact]
    public async Task RenamedFolder_BothParts_OneConfirmation_ModelUploadedOnce_ReferencesBound()
    {
        var (s, head) = await SetUpAsync();
        var flow = _env.Flow(s);

        Assert.True(await flow.RegisterLinkedManyAsync(Files(head)), _env.Dialogs.AllErrors);

        // Первое окно — «сдать 2 файла?», второе — одно подтверждение найденного на прежнем месте.
        Assert.Equal(2, _env.Dialogs.Confirms.Count);
        var confirm = _env.Dialogs.Confirms[1];
        Assert.Contains("не по сохранённому пути", confirm.Message);
        Assert.Equal(2, confirm.Details.Count);
        Assert.All(confirm.Details, d => Assert.Contains("перенесён", d));
        Assert.All(confirm.Details, d => Assert.Contains(NewPath("Models", "Светофильтры.tcprt"), d));

        Assert.Equal(4, _env.Server.Docs.Count); // сборка, две детали, модель — один раз
        var model = _env.DocId("Models/Светофильтры.tcprt");
        Assert.Equal(model, _env.Server.DocByPath("Деталь1.tcprt").Latest.Refs.Single().Child);
        Assert.Equal(model, _env.Server.DocByPath("Деталь2.tcprt").Latest.Refs.Single().Child);
        var asm = _env.Server.Doc(head).Latest;
        Assert.All(asm.Refs, r => Assert.NotNull(r.Child));
        Assert.Equal(2, asm.Number); // обе привязки — одной новой версией сборки
    }

    [Fact]
    public async Task RenamedFolder_ConfirmationDeclined_NothingRegistered()
    {
        var (s, head) = await SetUpAsync();
        var flow = _env.Flow(s);
        _env.Dialogs.ConfirmAnswers.Enqueue(true);  // сдать 2 файла
        _env.Dialogs.ConfirmAnswers.Enqueue(false); // найденное на прежнем месте — не подтверждать

        Assert.False(await flow.RegisterLinkedManyAsync(Files(head)));

        Assert.Single(_env.Server.Docs); // только сборка
        Assert.Contains("не подтверждены", _env.Dialogs.AllErrors);
        Assert.Equal(1, _env.Server.Doc(head).Latest.Number);
    }
}
