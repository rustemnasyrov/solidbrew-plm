using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

/// <summary>
/// Переписывание ссылок при раскладке: SOLIDWORKS (и тестовый CAD) ищет компоненты по сохранённым абсолютным путям,
/// поэтому без переписывания документы из подпапок грузят компоненты из исходной папки или не находят их.
/// </summary>
public class ReferenceRewriteTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private TestCadAdapter Cad => (TestCadAdapter)_env.Registry.Get(TestCadAdapter.Id)!;

    private static bool NoSourcePaths(string file, string src) => !File.ReadAllText(file).Contains(src, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Open_SourceFolderStillAvailable_DrawingAndAssemblyLoadFromWorkspace_NotAnEdit()
    {
        var (head, _) = await _env.ImportSourceAsync(_env.For("ivanov"));
        var src = _env.Path("src");
        Assert.True(Directory.Exists(src)); // исходная папка на месте — без переписывания CAD тихо взял бы модели из неё
        var p = _env.For("petrov");

        var asm = await p.OpenAsync(head, verify: true);
        Assert.True(asm.CadReport!.Ok, string.Join("; ", asm.CadReport.Problems));
        Assert.All(asm.CadReport.Configurations.SelectMany(c => c.Components), c => Assert.True(c.InsideWorkRoot, c.LoadedPath));

        // Чертёж в «Чертежи/» → деталь в «Детали/».
        var drawing = await p.OpenAsync(_env.DocId("Чертежи/Корпус.tcdrw"), verify: true);
        Assert.True(drawing.CadReport!.Ok, string.Join("; ", drawing.CadReport.Problems));
        Assert.Equal(p.File("Детали/Корпус.tcprt"), Assert.Single(drawing.CadReport.Configurations[0].Components).LoadedPath);
        Assert.Equal(new[] { "Чертежи/Корпус.tcdrw" }, drawing.ReferencesRewritten);

        foreach (var e in p.State.Entries) Assert.True(NoSourcePaths(p.File(e.RelPath), src), e.RelPath);
        var status = await p.Service.GetStatusAsync(p.WorkRoot);
        Assert.All(status.Files, f => Assert.Equal(LocalFileState.Unchanged, f.Local));

        // Кэш не тронут: в нём опубликованные байты.
        var drw = p.State.FindByPath("Чертежи/Корпус.tcdrw")!;
        Assert.NotNull(drw.PlacedSha256);
        Assert.True(await p.Cache.VerifyAsync(drw.Sha256));
        Assert.False(NoSourcePaths(p.Cache.PathOf(drw.Sha256), src));
    }

    [Fact]
    public async Task Open_DocumentOpenInCad_WarningNotFailure_RetryRewrites()
    {
        var (head, _) = await _env.ImportSourceAsync(_env.For("ivanov"));
        Directory.Move(_env.Path("src"), _env.Path("src-gone"));
        var p = _env.For("petrov");
        Cad.OpenDocuments.Add(p.File("Изделие.tcasm"));

        var plan = await p.Service.PrepareOpenAsync(p.WorkRoot, head);
        var r = await p.Service.ExecuteOpenAsync(plan, verifyInCad: true);

        Assert.True(r.Success, r.Message);
        Assert.Equal(5, r.Placed.Count);
        Assert.Equal(new[] { "Сборки/Узел.tcasm" }, r.ReferencesRewritten);
        var warning = Assert.Single(r.Problems, x => x.Code == LocalProblemCodes.ReferencesNotRewritten);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Equal("Изделие.tcasm", warning.RelPath);
        Assert.Contains("ссылки не переписаны", warning.Message);
        Assert.Contains("по старым путям", warning.Message);
        Assert.Contains("повторите «Открыть»", warning.Message);
        Assert.False(r.CadReport!.Ok); // компоненты по старым путям не найдены — проверка это показывает

        var entry = p.State.FindByPath("Изделие.tcasm")!;
        Assert.Null(entry.PlacedSha256);
        Assert.Equal(entry.Sha256, Client.Hash(p.File("Изделие.tcasm")));
        Assert.All(entry.Deps, d => Assert.StartsWith(_env.Path("src") + "\\", d.RefPath));
        Assert.True(Client.IsReadOnly(p.File("Изделие.tcasm")));

        // Документ закрыт в CAD — повторное «Открыть» ничего не раскладывает, но переписывает ссылки.
        Cad.OpenDocuments.Clear();
        var again = await p.OpenAsync(head, verify: true);
        Assert.Empty(again.Placed);
        Assert.Equal(new[] { "Изделие.tcasm" }, again.ReferencesRewritten);
        Assert.True(again.CadReport!.Ok, string.Join("; ", again.CadReport.Problems));
        Assert.True(Client.IsReadOnly(p.File("Изделие.tcasm")));
        Assert.All(p.State.FindByPath("Изделие.tcasm")!.Deps, d => Assert.StartsWith(p.WorkRoot + "\\", d.RefPath));
    }

    [Fact]
    public async Task Checkin_SendsWorkspaceBytes_OtherComputerRewritesToItsFolder_UnchangedLockedIsNotCandidate()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        await iv.CheckOutAsync(head);

        // Взят, ссылки переписаны, но не правился — не кандидат на сдачу.
        var idle = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var asmItem = Assert.Single(idle.Items);
        Assert.False(asmItem.ContentChanged);
        Assert.False(asmItem.ReferencesChanged);
        Assert.False(asmItem.Include);

        TestCadWriter.AppendGeometry(iv.File("Изделие.tcasm"), "сопряжение");
        var plan = await iv.Service.PrepareCheckinAsync(iv.WorkRoot);
        var item = Assert.Single(plan.Included);
        Assert.True(item.ContentChanged);
        Assert.False(item.ReferencesChanged); // привязка — (документ, версия), а не путь
        Assert.All(item.References, x => Assert.Equal(ReferenceResolution.ByAbsolutePath, x.Resolution));
        var result = await iv.Service.ExecuteCheckinAsync(plan, "v2");
        Assert.True(result.Success, result.Message);

        var entry = iv.State.FindByPath("Изделие.tcasm")!;
        Assert.Equal(2, entry.Version);
        Assert.Null(entry.PlacedSha256);
        Assert.Equal(_env.Server.Doc(head).Latest.Sha256, entry.Sha256);
        Assert.Equal(entry.Sha256, Client.Hash(iv.File("Изделие.tcasm")));
        Assert.All(_env.Server.Doc(head).Latest.Refs, x => Assert.StartsWith(iv.WorkRoot + "\\", x.RefPath));
        Assert.Equal(3, entry.Deps.Count);

        // Другой компьютер: в опубликованных байтах пути Иванова — переписываются на свою папку.
        var p = _env.For("petrov");
        var r = await p.OpenAsync(head, verify: true);
        Assert.True(r.CadReport!.Ok, string.Join("; ", r.CadReport.Problems));
        Assert.Contains("Изделие.tcasm", r.ReferencesRewritten);
        Assert.True(NoSourcePaths(p.File("Изделие.tcasm"), iv.WorkRoot));
    }

    [Fact]
    public async Task UpdateToLatest_PlacedFromCache_ReferencesRewritten()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var p = _env.For("petrov");
        await p.OpenAsync(head);

        var unit = _env.DocId("Сборки/Узел.tcasm");
        await iv.CheckOutAsync(unit);
        TestCadWriter.AppendGeometry(iv.File("Сборки/Узел.tcasm"), "ещё болт");
        await iv.CheckinAllAsync("узел v2");

        var update = await p.Service.PrepareUpdateToLatestAsync(p.WorkRoot, new[] { unit });
        var r = await p.Service.ExecuteOpenAsync(update);
        Assert.True(r.Success, r.Message);
        Assert.Equal(new[] { "Сборки/Узел.tcasm" }, r.Placed);
        Assert.Equal(new[] { "Сборки/Узел.tcasm" }, r.ReferencesRewritten);
        Assert.True(NoSourcePaths(p.File("Сборки/Узел.tcasm"), iv.WorkRoot));
        var e = p.State.FindByPath("Сборки/Узел.tcasm")!;
        Assert.Equal(2, e.Version);
        Assert.Equal(e.PlacedSha256, Client.Hash(p.File("Сборки/Узел.tcasm")));
        Assert.Equal(LocalFileState.Unchanged, (await p.Service.GetStatusAsync(p.WorkRoot, queryServer: false)).FindByPath("Сборки/Узел.tcasm")!.Local);

        var verify = await p.OpenAsync(head, verify: true);
        Assert.True(verify.CadReport!.Ok, string.Join("; ", verify.CadReport.Problems));
    }

    [Fact]
    public async Task UndoCheckOut_Revert_RestoresPublishedBytesAndRewritesAgain()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        var placed = iv.State.FindByPath("Изделие.tcasm")!.PlacedSha256;
        Assert.NotNull(placed);

        await iv.CheckOutAsync(head);
        Assert.Equal(LocalFileState.Unchanged, (await iv.Service.GetStatusAsync(iv.WorkRoot)).FindByPath("Изделие.tcasm")!.Local);
        TestCadWriter.AppendGeometry(iv.File("Изделие.tcasm"), "неудачная правка");
        var undo = await iv.Service.PrepareUndoCheckOutAsync(iv.WorkRoot, new[] { head });
        Assert.True(Assert.Single(undo.Items).HasLocalChanges);
        var r = await iv.Service.ExecuteUndoCheckOutAsync(undo, revertAll: true);
        Assert.True(r.Success, r.Message);
        Assert.Equal(new[] { "Изделие.tcasm" }, r.Reverted);
        Assert.DoesNotContain(r.Problems, x => x.Code == LocalProblemCodes.ReferencesNotRewritten);

        var e = iv.State.FindByPath("Изделие.tcasm")!;
        Assert.Equal(placed, e.PlacedSha256);
        Assert.Equal(placed, Client.Hash(iv.File("Изделие.tcasm")));
        Assert.All(e.Deps, d => Assert.StartsWith(iv.WorkRoot + "\\", d.RefPath));
        Assert.True(Client.IsReadOnly(iv.File("Изделие.tcasm")));
        Assert.Equal(LocalFileState.Unchanged, (await iv.Service.GetStatusAsync(iv.WorkRoot)).FindByPath("Изделие.tcasm")!.Local);

        // Без правок «Отменить взятие» ничего не считает правкой.
        await iv.CheckOutAsync(head);
        var undo2 = await iv.Service.PrepareUndoCheckOutAsync(iv.WorkRoot, new[] { head });
        Assert.False(Assert.Single(undo2.Items).HasLocalChanges);
    }

    [Fact]
    public async Task Rename_WithRewrittenFiles_NoFalseLocalChanges()
    {
        var iv = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(iv);
        await iv.OpenAsync(head);
        await iv.OpenAsync(_env.DocId("Чертежи/Корпус.tcdrw"));

        // Переименовать деталь, на которую ссылаются сборка и чертёж, — оба переписаны при раскладке, правками не считаются.
        var plan = await iv.Service.PrepareRenameAsync(iv.WorkRoot, "Детали/Корпус.tcprt", "Детали/Корпус-01.tcprt");
        Assert.DoesNotContain(plan.Problems, x => x.Code == LocalProblemCodes.LocalChanges);
        Assert.True(plan.CanExecute, string.Join("; ", plan.Problems));
        var r = await iv.Service.ExecuteRenameAsync(plan);
        Assert.True(r.Success, r.Message);

        var status = await iv.Service.GetStatusAsync(iv.WorkRoot);
        Assert.All(status.Files, f => Assert.Equal(LocalFileState.Unchanged, f.Local));
        Assert.True(NoSourcePaths(iv.File("Чертежи/Корпус.tcdrw"), _env.Path("src")));
        var drw = await iv.OpenAsync(_env.DocId("Чертежи/Корпус.tcdrw"), verify: true);
        Assert.Equal(iv.File("Детали/Корпус-01.tcprt"), Assert.Single(drw.CadReport!.Configurations[0].Components).LoadedPath);
        Assert.True(drw.CadReport.Ok, string.Join("; ", drw.CadReport.Problems));
    }
}
