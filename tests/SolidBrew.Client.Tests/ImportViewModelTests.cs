using SolidBrew.Adapters.TestCad;
using SolidBrew.Client.ViewModels;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Tests;

public sealed class ImportViewModelTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Decisions_ByCategory_GoIntoReport_AndImport()
    {
        var src = _env.Path("src");
        var outside = TestCadWriter.Create(_env.Path("lib", "Стандарт.tcprt"));
        TestCadWriter.Create(Path.Combine(src, "Детали", "Найден.tcprt"));
        TestCadWriter.Create(Path.Combine(src, "Детали", "Шайба.tcprt"));
        TestCadWriter.Create(Path.Combine(src, "A", "Гайка.tcprt"));
        TestCadWriter.Create(Path.Combine(src, "B", "Гайка.tcprt"), geometry: "другая гайка");
        var head = TestCadWriter.Create(Path.Combine(src, "Сборка.tcasm"), new[]
        {
            Path.Combine(src, "Детали", "Найден.tcprt"),
            outside,
            @"Q:\Старый диск\Шайба.tcprt",
            @"Q:\Старый диск\Гайка.tcprt",
            @"Q:\Старый диск\Нет.tcprt",
        });
        var s = _env.Session("ivanov");
        var vm = new ImportViewModel(s, _env.Project.Id);
        await vm.InitializeAsync();
        Assert.Equal(_env.Project.Id, vm.SelectedProject?.Id);

        _env.Dialogs.FileToPick = head;
        vm.BrowseHeadCommand.Execute(null);
        Assert.Equal(head, vm.HeadFile);
        await vm.AnalyzeCommand.ExecuteAsync(null);

        // Отчёт по категориям.
        Assert.Single(vm.Found);
        var outsideRow = Assert.Single(vm.Outside);
        var nameRow = Assert.Single(vm.NameOnly);
        var ambRow = Assert.Single(vm.Ambiguous);
        Assert.Single(vm.Missing);
        Assert.Equal(2, ambRow.Candidates.Count);
        Assert.Equal("Нет.tcprt", vm.Missing[0].FileName);
        Assert.Contains("Неоднозначно (1)", vm.AmbiguousHeader);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Contains(vm.Problems, p => p.IsBlocking);

        // Решения пользователя попадают в отчёт.
        nameRow.Accept = true;
        outsideRow.Accept = true;
        ambRow.Chosen = ambRow.Candidates.First(c => c.Contains(@"\B\"));
        var report = vm.Report!;
        var refs = report.Head.References.ToDictionary(r => r.FileName);
        Assert.True(refs["Шайба.tcprt"].Accept);
        Assert.True(refs["Стандарт.tcprt"].Accept);
        Assert.EndsWith(@"B\Гайка.tcprt", refs["Гайка.tcprt"].Chosen);
        Assert.Equal(5, report.IncludedDocuments.Count());
        Assert.Contains(vm.Documents, d => d.IsIncluded && d.FileName == "Стандарт.tcprt");

        // Отсутствующая ссылка — только с явным разрешением.
        Assert.False(vm.ImportCommand.CanExecute(null));
        vm.AllowMissing = true;
        Assert.True(vm.ImportCommand.CanExecute(null));

        _env.Dialogs.ConfirmAnswers.Enqueue(true);   // поставить с отсутствующими ссылками
        _env.Dialogs.ConfirmAnswers.Enqueue(false);  // «Открыть в рабочую папку?» — не сейчас
        vm.Comment = "Постановка с сетевого диска";
        await vm.ImportCommand.ExecuteAsync(null);

        Assert.True(vm.DialogResult, _env.Dialogs.AllErrors);
        Assert.True(vm.Result!.Success);
        Assert.False(vm.OpenAfter);
        Assert.Equal(2, _env.Dialogs.Confirms.Count);
        Assert.Contains("Открыть", _env.Dialogs.Confirms[1].Message);
        Assert.Equal(5, _env.Server.Docs.Count);
        var req = _env.Server.Checkins.Single();
        Assert.Equal("Постановка с сетевого диска", req.Comment);
        Assert.True(req.AllowUnresolvedReferences);
        var asm = _env.Server.Doc(vm.Result.HeadDocumentId).Latest;
        Assert.Equal(_env.Server.Docs.Values.Single(d => d.Latest.RelPath.EndsWith("B/Гайка.tcprt")).Id, asm.Refs.Single(r => r.RefPath.EndsWith("Гайка.tcprt")).Child);
        Assert.Null(asm.Refs.Single(r => r.RefPath.EndsWith("Нет.tcprt")).Child);
        Assert.NotNull(asm.Refs.Single(r => r.RefPath.EndsWith("Стандарт.tcprt")).Child);
    }

    [Fact]
    public async Task LinkedFile_FolderRenamed_RelocatedTab_ConfirmEnablesImport()
    {
        var model = TestCadWriter.Create(_env.Path("Новая", "Models", "Модель.tcprt"));
        var part = TestCadWriter.Create(_env.Path("Новая", "Узел", "Деталь.tcprt"), new[] { _env.Path("Старая", "Models", "Модель.tcprt") });
        var s = _env.Session("ivanov");
        var vm = new ImportViewModel(s, _env.Project.Id, new ImportPreset(part, string.Empty, "Узел", _env.Path("Старая", "Узел", "Деталь.tcprt")));
        await vm.InitializeAsync(); // отчёт строится сразу

        var row = Assert.Single(vm.Relocated);
        Assert.Empty(vm.NameOnly);
        Assert.Empty(vm.Missing);
        Assert.Equal(model, row.TargetPath);
        Assert.Contains("Старая", row.MovedText);
        Assert.Contains("Папка перенесена (1)", vm.RelocatedHeader);
        Assert.False(vm.ImportCommand.CanExecute(null));

        row.Accept = true;
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.Contains(vm.Documents, d => d.IsIncluded && d.RelPath == "Models/Модель.tcprt");
        Assert.Contains(vm.Documents, d => d.IsIncluded && d.RelPath == "Деталь.tcprt");
        vm.CancelCommand.Execute(null);
        vm.OnClosed();
    }

    [Fact]
    public async Task UnconfirmedNameOnly_StaysMissing_UntilAccepted()
    {
        var src = _env.Path("src");
        TestCadWriter.Create(Path.Combine(src, "Детали", "Шайба.tcprt"));
        var head = TestCadWriter.Create(Path.Combine(src, "Сборка.tcasm"), new[] { @"Q:\Старый диск\Шайба.tcprt" });
        var s = _env.Session("ivanov");
        var vm = new ImportViewModel(s, _env.Project.Id);
        await vm.InitializeAsync();
        vm.HeadFile = head;
        await vm.AnalyzeCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.NameOnly);
        Assert.False(row.Accept);
        Assert.Contains("отсутствующей", row.EffectiveText);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.Single(vm.Documents, d => d.IsIncluded);

        row.Accept = true;
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.Equal(2, vm.Documents.Count(d => d.IsIncluded));
        Assert.Equal(ImportReferenceCategory.NameOnly, row.Reference.Category);
        Assert.Equal(row.TargetPath, row.Reference.EffectiveTarget);

        var staging = vm.Report!.StagingRoot;
        vm.CancelCommand.Execute(null);
        vm.OnClosed();
        Assert.False(vm.DialogResult);
        Assert.Empty(_env.Server.Docs);
        Assert.False(Directory.Exists(staging));
    }
}
