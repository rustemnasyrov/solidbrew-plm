using SolidBrew.Client.Infrastructure;
using SolidBrew.Client.ViewModels;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class CommonTests
{
    [Fact]
    public void Tree_CtrlAndShiftClick_MultiSelect_PlainClickResets()
    {
        var tree = new ProductTreeViewModel();
        var group = new ProductNodeViewModel("Группа", tree);
        tree.Roots.Add(group);
        group.IsExpanded = true;
        var nodes = Enumerable.Range(1, 5).Select(i => new ProductNodeViewModel("Узел " + i, tree)).ToList();
        foreach (var n in nodes) group.Children.Add(n);

        tree.Select(nodes[0]);
        Assert.Single(tree.SelectedNodes);
        Assert.False(tree.HasMultiSelection);

        tree.ExtendSelection(nodes[2], toggle: true, range: false);   // Ctrl
        Assert.Equal(new[] { nodes[0], nodes[2] }, tree.SelectedNodes);
        Assert.Same(nodes[2], tree.SelectedNode);

        tree.ExtendSelection(nodes[4], toggle: false, range: true);   // Shift: от текущего (3-го) до 5-го
        Assert.Equal(new[] { nodes[2], nodes[3], nodes[4] }, tree.SelectedNodes);

        tree.ExtendSelection(nodes[3], toggle: true, range: false);   // Ctrl по выделенному — снять
        Assert.Equal(new[] { nodes[2], nodes[4] }, tree.SelectedNodes);

        tree.Select(nodes[1]);                                        // обычный щелчок — одно выделение
        Assert.Equal(new[] { nodes[1] }, tree.SelectedNodes);
        Assert.All(nodes.Where(n => n != nodes[1]), n => Assert.False(n.IsMultiSelected));
    }

    [Fact]
    public void LinkLocator_StalePath_FindsFileByNameUnderSurvivingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "sb-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Ссылка указывает в переименованную папку; файл лежит в новой папке с той же подпапкой «SF.02.00».
            var real = Path.Combine(root, "Новое имя", "SF.02.00 Кронштейн", "SF.02.01 Кронштейн.SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(real)!);
            File.WriteAllText(real, "x");
            // Тёзка в другой подпапке — менее похож на путь ссылки.
            var other = Path.Combine(root, "Архив", "SF.02.01 Кронштейн.SLDPRT");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            File.WriteAllText(other, "y");
            var stale = Path.Combine(root, "Старое имя", "SF.02.00 Кронштейн", "SF.02.01 Кронштейн.SLDPRT");

            LinkLocator.Reset();
            Assert.Equal(real, LinkLocator.Find(stale, ownerFolder: null));
            // Рядом с документом-родителем — в первую очередь.
            LinkLocator.Reset();
            Assert.Equal(other, LinkLocator.Find(stale, Path.GetDirectoryName(other)));
            // Файла с таким именем нигде нет.
            LinkLocator.Reset();
            Assert.Null(LinkLocator.Find(Path.Combine(root, "Старое имя", "Нет такого.SLDPRT"), null));
        }
        finally
        {
            LinkLocator.Reset();
            Directory.Delete(root, recursive: true);
        }
    }

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
