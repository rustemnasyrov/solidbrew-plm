using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class CheckinViewModelTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task BlockingProblem_DisablesBothCheckinButtons()
    {
        var s = _env.Session("ivanov");
        var ws = _env.WorkRoot("ivanov");
        s.Workspace.InitWorkspace(ws, _env.Project.Id);
        // Новая сборка ссылается на деталь, которой нет в рабочей папке.
        TestCadWriter.Create(Path.Combine(ws, "Сборка.tcasm"), new[] { @"Детали\Нет.tcprt" });

        var vm = new CheckinViewModel(s.Workspace, _env.Dialogs, ws);
        await vm.InitializeAsync();

        var item = Assert.Single(vm.Items);
        Assert.True(item.Include);
        Assert.Equal("новый документ", item.ChangeText);
        Assert.True(vm.HasBlockingProblems);
        Assert.Contains(vm.Problems, p => p.IsBlocking && p.Message.Contains("Нет.tcprt"));
        Assert.False(vm.CheckinCommand.CanExecute(null));
        Assert.False(vm.CheckinKeepLocksCommand.CanExecute(null));

        // Положили деталь — после обновления списка сдача доступна (сборка и новая деталь).
        TestCadWriter.Create(Path.Combine(ws, "Детали", "Нет.tcprt"));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.False(vm.HasBlockingProblems);
        Assert.True(vm.CheckinCommand.CanExecute(null));

        // Сняли отметку с детали — сборка ссылается на неотмеченный черновик: снова блокирующая ошибка.
        vm.Items.Single(i => i.FileName == "Нет.tcprt").Include = false;
        Assert.True(vm.HasBlockingProblems);
        Assert.False(vm.CheckinCommand.CanExecute(null));
        Assert.Empty(_env.Server.Checkins);
    }

    [Fact]
    public async Task CheckinKeepLocks_PassesCommentAndKeepsLock()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var ws = _env.WorkRoot("ivanov");
        var shaft = _env.DocId("Детали/Вал.tcprt");
        var r = await s.Workspace.CheckOutAsync(ws, new[] { shaft });
        Assert.True(r.Success, r.Message);
        TestCadWriter.AppendGeometry(Path.Combine(ws, "Детали", "Вал.tcprt"), "fillet 2");

        var vm = new CheckinViewModel(s.Workspace, _env.Dialogs, ws);
        await vm.InitializeAsync();
        var row = Assert.Single(vm.Items, i => i.Include);
        Assert.Equal("Детали/Вал.tcprt", row.RelPath);
        Assert.Equal("содержимое", row.ChangeText);
        Assert.True(vm.CheckinKeepLocksCommand.CanExecute(null));

        vm.Comment = "  Скругление вала  ";
        await vm.CheckinKeepLocksCommand.ExecuteAsync(null);

        Assert.True(vm.DialogResult, _env.Dialogs.AllErrors);
        var req = _env.Server.Checkins.Last();
        Assert.Equal("Скругление вала", req.Comment);
        Assert.True(req.KeepLocks);
        Assert.Equal(2, _env.Server.Doc(shaft).Latest.Number);
        Assert.Equal("ivanov", _env.Server.Doc(shaft).Lock?.UserLogin);
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task Checkin_ReleasesLock_WithoutKeepLocks()
    {
        var s = _env.Session("ivanov");
        var head = await _env.ImportAsync(s);
        await _env.OpenDirectAsync(s, head);
        var ws = _env.WorkRoot("ivanov");
        var shaft = _env.DocId("Детали/Вал.tcprt");
        Assert.True((await s.Workspace.CheckOutAsync(ws, new[] { shaft })).Success);
        TestCadWriter.AppendGeometry(Path.Combine(ws, "Детали", "Вал.tcprt"), "chamfer 1");

        var vm = new CheckinViewModel(s.Workspace, _env.Dialogs, ws);
        await vm.InitializeAsync();
        vm.Comment = "Фаска";
        await vm.CheckinCommand.ExecuteAsync(null);

        var req = _env.Server.Checkins.Last();
        Assert.False(req.KeepLocks);
        Assert.Equal("Фаска", req.Comment);
        Assert.Null(_env.Server.Doc(shaft).Lock);
    }

    [Fact]
    public async Task NewerCadVersion_RequiresSeparateConfirmation()
    {
        _env.Server.Settings[SettingKeys.WorkingVersion(TestCadAdapter.Id)] = "2018";
        var s = _env.Session("ivanov");
        var ws = _env.WorkRoot("ivanov");
        s.Workspace.InitWorkspace(ws, _env.Project.Id);
        TestCadWriter.Write(Path.Combine(ws, "Новая.tcprt"), new TestCadDocument { CadVersion = 2024 }.Geom("box 1 1 1"));

        var vm = new CheckinViewModel(s.Workspace, _env.Dialogs, ws);
        await vm.InitializeAsync();
        Assert.True(vm.Plan!.HasNewerCadVersion);
        Assert.Contains(vm.Problems, p => p.IsWarning);
        Assert.True(vm.CheckinCommand.CanExecute(null));

        _env.Dialogs.ConfirmAnswers.Enqueue(false);
        await vm.CheckinCommand.ExecuteAsync(null);
        Assert.Single(_env.Dialogs.Confirms);
        Assert.Empty(_env.Server.Checkins);
        Assert.Null(vm.DialogResult);

        _env.Dialogs.ConfirmAnswers.Enqueue(true);
        await vm.CheckinCommand.ExecuteAsync(null);
        Assert.True(_env.Server.Checkins.Single().AcceptNewerCadVersion);
        Assert.True(vm.DialogResult);
    }

    [Fact]
    public async Task ServerRejection_ShowsProblems_AndKeepsDialogOpen()
    {
        var s = _env.Session("ivanov");
        var ws = _env.WorkRoot("ivanov");
        s.Workspace.InitWorkspace(ws, _env.Project.Id);
        TestCadWriter.Create(Path.Combine(ws, "Деталь.tcprt"));
        _env.Server.CheckinFailure = _ => new Core.Api.PdmApiException(422, "Сдача отклонена.",
            new ErrorResponse { Message = "Сдача отклонена.", Problems = { new ProblemDto { Code = ProblemCodes.Validation, Message = "Проверка не пройдена.", RelPath = "Деталь.tcprt" } } });

        var vm = new CheckinViewModel(s.Workspace, _env.Dialogs, ws);
        await vm.InitializeAsync();
        await vm.CheckinCommand.ExecuteAsync(null);

        Assert.Null(vm.DialogResult);
        Assert.NotNull(vm.ServerMessage);
        Assert.Contains(vm.Problems, p => p.Message.Contains("Проверка не пройдена"));
        Assert.True(File.Exists(Path.Combine(ws, "Деталь.tcprt")));
    }
}
