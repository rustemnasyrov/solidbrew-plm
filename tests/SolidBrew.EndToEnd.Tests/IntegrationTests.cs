using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.EndToEnd.Tests;

/// <summary>Стыковка клиента (Workspace) и сервера: права на настройки, «сдать и оставить в работе», статус большой папки.</summary>
[Collection(EndToEndCollection.Name)]
public sealed class IntegrationTests(ServerFixture fx) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Designer_SeesWorkingCadVersion_NewerCadVersionWarnsOnCheckin()
    {
        var admin = await fx.AdminAsync();
        var key = SettingKeys.WorkingVersion(TestCadAdapter.Id);
        await admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "2018" } });
        try
        {
            var a = await Computer.NewAsync(fx, _dir, "ivanov");
            Assert.Equal(2018, (await a.Service.GetWorkingCadVersionsAsync())[TestCadAdapter.Id]);

            var project = await fx.NewProjectAsync();
            a.Service.InitWorkspace(a.WorkRoot, project.Id);
            TestCadWriter.Write(a.File("Деталь.tcprt"), new TestCadDocument { CadVersion = 2024 }.Prop("Обозначение", "НОВ.001"));
            var plan = await a.Service.PrepareCheckinAsync(a.WorkRoot);
            Assert.True(plan.HasNewerCadVersion);
            Assert.Contains(plan.Problems, p => p.Code == LocalProblemCodes.NewerCadVersion && p.Message.Contains("2018"));
            var refused = await a.Service.ExecuteCheckinAsync(plan, "без подтверждения");
            Assert.False(refused.Success);
            var accepted = await a.Service.ExecuteCheckinAsync(plan, "с подтверждением", acceptNewerCad: true);
            Assert.True(accepted.Success, accepted.Message + string.Join("\n", accepted.Problems));
        }
        finally
        {
            await admin.UpdateSettingsAsync(new SettingsDto { Values = { [key] = "" } });
        }
    }

    [Fact]
    public async Task CheckinKeepLocks_NewAndExistingDocumentsStayLocked_WithoutWarnings()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var project = await fx.NewProjectAsync();
        a.Service.InitWorkspace(a.WorkRoot, project.Id);
        TestCadWriter.Write(a.File("Узлы/Рама.tcasm"), new TestCadDocument().Prop("Обозначение", "РМ.000").Ref("Стойка.tcprt"));
        TestCadWriter.Write(a.File("Узлы/Стойка.tcprt"), new TestCadDocument().Prop("Обозначение", "РМ.001"));

        // Сервер сам оставляет новые документы за автором; повторное «взять» своим же — безвредно.
        var first = await a.CheckinAsync("новые", keepLocks: true);
        Assert.Equal(2, first.Published.Count);
        Assert.Empty(first.Problems);
        foreach (var p in first.Published)
        {
            Assert.Equal(a.User.Id, (await a.DocAsync(p.DocumentId)).Lock?.UserId);
            Assert.True(a.State.FindByDocument(p.DocumentId)!.LockedByMe);
            Assert.False(Computer.IsReadOnly(a.File(p.RelPath)));
            Assert.Equal(a.User.Id, (await a.Api.LockAsync(p.DocumentId)).UserId);
        }

        a.Edit("Узлы/Стойка.tcprt");
        var second = await a.CheckinAsync("правка", keepLocks: true);
        Assert.Equal(2, Assert.Single(second.Published).Version);
        Assert.Empty(second.Problems);
        Assert.NotNull((await a.DocAsync(a.Id("Узлы/Стойка.tcprt"))).Lock);
    }

    [Fact]
    public async Task Status_OfWorkspace_MatchesServer_ForEveryDocument()
    {
        var a = await Computer.NewAsync(fx, _dir, "ivanov");
        var b = await Computer.NewAsync(fx, _dir, "petrov");
        var project = await fx.NewProjectAsync();
        a.Service.InitWorkspace(a.WorkRoot, project.Id);
        for (var i = 0; i < 12; i++)
            TestCadWriter.Write(a.File($"Детали/Д{i:00}.tcprt"), new TestCadDocument().Prop("Обозначение", $"Д.{i:000}"));
        await a.CheckinAsync("двенадцать деталей");

        var b2 = b.WithFolder(_dir.Path("petrov", "ws"));
        b2.Service.InitWorkspace(b2.WorkRoot, project.Id);
        var target = a.Id("Детали/Д05.tcprt");
        await b2.OpenAsync(target);
        await b2.CheckOutAsync("Детали/Д05.tcprt");

        await a.CheckOutAsync("Детали/Д07.tcprt");
        a.Edit("Детали/Д07.tcprt");
        await a.CheckinAsync("д07 v2", keepLocks: true);

        // Документы, которых нет в папке, но есть в проекте, не мешают; все учтённые — с данными сервера.
        var status = await a.Service.GetStatusAsync(a.WorkRoot);
        Assert.True(status.ServerChecked, status.ServerError);
        Assert.Equal(12, status.Files.Count);
        Assert.All(status.Files, f => Assert.NotNull(f.LatestVersion));
        Assert.Equal(LockState.LockedByOther, status.FindByDocument(target)!.Lock);
        Assert.Equal(b.User.Id, status.FindByDocument(target)!.LockInfo!.UserId);
        Assert.Equal(LockState.LockedByMe, status.FindByPath("Детали/Д07.tcprt")!.Lock);
        Assert.Equal(10, status.Files.Count(f => f.Lock == LockState.Free));
    }
}
