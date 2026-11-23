using SolidBrew.Client.Infrastructure;
using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class CommonTests
{
    [Fact]
    public async Task BusyState_RunsOperationsInTurn_ReportsProgress_AndCancels()
    {
        var busy = new BusyState();
        var errors = new List<string>();
        busy.ErrorHandler = (t, e) => errors.Add(t + ": " + e.Message);
        var order = new List<int>();
        var gate = new TaskCompletionSource();

        var first = busy.RunAsync("Первая", async (p, ct) =>
        {
            p.Report(new WorkProgress(WorkStage.Downloading, 1, 4, "Скачивание 1 из 4"));
            await gate.Task;
            order.Add(1);
        });
        var second = busy.RunAsync("Вторая", (_, _) =>
        {
            order.Add(2);
            return Task.CompletedTask;
        });
        Assert.True(busy.IsBusy);
        gate.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(new[] { 1, 2 }, order);
        Assert.Empty(errors);
        Assert.False(busy.IsBusy);

        var started = new TaskCompletionSource();
        var cancelled = busy.RunAsync("Долгая", async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await started.Task;
        Assert.True(busy.CancelCommand.CanExecute(null));
        busy.CancelCommand.Execute(null);
        Assert.False(await cancelled);
        Assert.Empty(errors); // отмена — не ошибка
        Assert.Contains("отменено", busy.StatusText);
    }

    [Fact]
    public async Task BusyState_Error_IsHumanReadable_WithProblems()
    {
        var busy = new BusyState();
        ErrorInfo? shown = null;
        busy.ErrorHandler = (_, e) => shown = e;
        var ex = new PdmApiException(409, "Сдача отклонена.", new ErrorResponse
        {
            Message = "Сдача отклонена.",
            Problems = { new ProblemDto { Code = ProblemCodes.StaleBaseVersion, Message = "Исходная версия устарела: последняя v3.", RelPath = "Детали/Вал.tcprt" } },
            Lock = new LockDto { UserDisplayName = "Петров П. П.", UserLogin = "petrov", At = DateTimeOffset.UtcNow },
        });
        Assert.False(await busy.RunAsync("Сдать", (_, _) => throw ex));
        Assert.NotNull(shown);
        Assert.StartsWith("Сдача отклонена.", shown!.Message);
        Assert.Contains("Петров П. П.", shown.Message);
        Assert.Equal("«Детали/Вал.tcprt»: Исходная версия устарела: последняя v3.", Assert.Single(shown.Details));

        Assert.Equal("Нет файла.", ErrorText.Describe(new WorkspaceException("Нет файла.")).Message);
        Assert.Contains("Непредвиденная ошибка", ErrorText.Describe(new InvalidOperationException("x")).Message);
    }
}
