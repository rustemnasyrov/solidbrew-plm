using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.ViewModels;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class OpenAndUndoTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private OpenViewModel OpenVm(AppSession s, Guid documentId, int? version = null)
    {
        var ws = s.Workspace;
        return new OpenViewModel(ws, _env.Dialogs, _env.Shell, _env.WorkRoot(s.Me.Login),
            (r, p, ct) => ws.PrepareOpenAsync(r, documentId, version, p, ct), "Открыть");
    }

    [Fact]
    public async Task Open_FirstTime_PlacesKit_NoConflicts()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        var vm = OpenVm(s, head);
        await vm.InitializeAsync();

        Assert.False(vm.HasConflicts);
        Assert.False(vm.VerifyInCad); // по умолчанию включено только для SOLIDWORKS
        Assert.Equal(3, vm.Items.Count);
        Assert.Contains("Документов в комплекте: 3", vm.Summary);
        Assert.True(vm.OpenCommand.CanExecute(null));

        vm.VerifyInCad = true; // тестовый CAD умеет проверку
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.True(vm.IsDone, _env.Dialogs.AllErrors);
        Assert.True(File.Exists(Path.Combine(_env.WorkRoot("ivanov"), "Изделие.tcasm")));
        Assert.True(vm.HasCadReport);
        Assert.NotEmpty(vm.CadComponents);
        Assert.All(vm.CadComponents, c => Assert.True(c.InsideWorkRoot));
        Assert.False(vm.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task Open_WithLocalChanges_ConflictKeepByDefault_ReplaceOnRequest()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var file = Path.Combine(_env.WorkRoot("ivanov"), "Детали", "Вал.tcprt");
        File.SetAttributes(file, FileAttributes.Normal);
        TestCadWriter.AppendGeometry(file, "несданная правка");
        var edited = TestEnv.Hash(file);

        // 1) По умолчанию файл с правками остаётся.
        var vm = OpenVm(s, head);
        await vm.InitializeAsync();
        var conflict = Assert.Single(vm.Conflicts);
        Assert.Equal("Детали/Вал.tcprt", conflict.RelPath);
        Assert.True(conflict.HasLocalChanges);
        Assert.Equal(ConflictResolution.Keep, conflict.Resolution);
        Assert.False(conflict.IsReplace);
        Assert.Contains("Конфликтов: 1", vm.Summary);
        await vm.OpenCommand.ExecuteAsync(null);
        Assert.True(vm.IsDone);
        Assert.Equal(edited, TestEnv.Hash(file));
        Assert.Contains("Детали/Вал.tcprt", vm.Result!.Kept);

        // 2) «Заменить» — после подтверждения правки заменяются версией из хранилища.
        var vm2 = OpenVm(s, head);
        await vm2.InitializeAsync();
        var c2 = Assert.Single(vm2.Conflicts);
        c2.IsReplace = true;
        Assert.Equal(ConflictResolution.Replace, c2.Conflict.Resolution);
        Assert.Contains(vm2.Items, i => i.RelPath == "Детали/Вал.tcprt" && i.ActionText == "заменить");

        _env.Dialogs.ConfirmAnswers.Enqueue(false); // сначала отказались
        await vm2.OpenCommand.ExecuteAsync(null);
        Assert.False(vm2.IsDone);
        Assert.Equal(edited, TestEnv.Hash(file));

        _env.Dialogs.ConfirmAnswers.Enqueue(true);
        await vm2.OpenCommand.ExecuteAsync(null);
        Assert.True(vm2.IsDone, _env.Dialogs.AllErrors);
        Assert.NotEqual(edited, TestEnv.Hash(file));
        Assert.Equal(_env.Server.DocByPath("Детали/Вал.tcprt").Latest.Sha256, TestEnv.Hash(file));
        Assert.True(TestEnv.IsReadOnly(file));
    }

    [Fact]
    public async Task UndoCheckOut_WithLocalChanges_WarnsAndKeepsEdits()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var flow = _env.Flow(s);
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True(await flow.CheckOutAsync(new[] { shaft }), _env.Dialogs.AllErrors);
        var file = Path.Combine(_env.WorkRoot("ivanov"), "Детали", "Вал.tcprt");
        Assert.False(TestEnv.IsReadOnly(file));
        TestCadWriter.AppendGeometry(file, "правка");
        var edited = TestEnv.Hash(file);

        _env.Dialogs.ChooseAnswers.Enqueue(0); // оставить правки
        Assert.True(await flow.UndoCheckOutAsync(new[] { shaft }), _env.Dialogs.AllErrors);

        var choice = Assert.Single(_env.Dialogs.Choices);
        Assert.Contains("несданные правки", choice.Message);
        Assert.Contains(choice.Details, d => d.Contains("Вал.tcprt"));
        Assert.Equal(3, choice.Options.Count);
        Assert.Null(_env.Server.Doc(shaft).Lock);
        Assert.Equal(edited, TestEnv.Hash(file)); // ничего не удалено молча
        Assert.True(TestEnv.IsReadOnly(file));
    }

    [Fact]
    public async Task UndoCheckOut_RevertToStoredVersion_AfterConfirmation()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var flow = _env.Flow(s);
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True(await flow.CheckOutAsync(new[] { shaft }));
        var file = Path.Combine(_env.WorkRoot("ivanov"), "Детали", "Вал.tcprt");
        TestCadWriter.AppendGeometry(file, "правка");

        _env.Dialogs.ChooseAnswers.Enqueue(1);       // вернуть версию из хранилища
        _env.Dialogs.ConfirmAnswers.Enqueue(true);   // подтвердить потерю правок
        Assert.True(await flow.UndoCheckOutAsync(new[] { shaft }));
        Assert.Equal(_env.Server.Doc(shaft).Latest.Sha256, TestEnv.Hash(file));
        Assert.Null(_env.Server.Doc(shaft).Lock);
    }

    [Fact]
    public async Task UndoCheckOut_Cancelled_KeepsLock()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var flow = _env.Flow(s);
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True(await flow.CheckOutAsync(new[] { shaft }));
        var file = Path.Combine(_env.WorkRoot("ivanov"), "Детали", "Вал.tcprt");
        TestCadWriter.AppendGeometry(file, "правка");
        var edited = TestEnv.Hash(file);

        _env.Dialogs.ChooseAnswers.Enqueue(2); // не отменять
        Assert.False(await flow.UndoCheckOutAsync(new[] { shaft }));
        Assert.Equal("ivanov", _env.Server.Doc(shaft).Lock?.UserLogin);
        Assert.Equal(edited, TestEnv.Hash(file));
    }

    [Fact]
    public async Task UndoCheckOut_WithoutChanges_AsksSimpleConfirmation()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var flow = _env.Flow(s);
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True(await flow.CheckOutAsync(new[] { shaft }));

        Assert.True(await flow.UndoCheckOutAsync(new[] { shaft }));
        Assert.Empty(_env.Dialogs.Choices);
        Assert.Single(_env.Dialogs.Confirms);
        Assert.Null(_env.Server.Doc(shaft).Lock);
    }

    [Fact]
    public async Task CheckOut_LockedByOther_ShowsWhoAndSince()
    {
        var iv = _env.Session("ivanov");
        var head = await _env.ImportAsync(iv);
        await _env.OpenDirectAsync(iv, head);
        var pe = _env.Session("petrov");
        await _env.OpenDirectAsync(pe, head);
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True(await _env.Flow(pe).CheckOutAsync(new[] { shaft }));

        Assert.False(await _env.Flow(iv).CheckOutAsync(new[] { shaft }));
        var error = _env.Dialogs.Errors.Last().Error;
        Assert.Contains(error.Details, d => d.Contains("Петров П. П."));
        Assert.True(TestEnv.IsReadOnly(Path.Combine(_env.WorkRoot("ivanov"), "Детали", "Вал.tcprt")));
    }
}
