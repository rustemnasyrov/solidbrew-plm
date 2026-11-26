using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

/// <summary>
/// Постановка на учёт: отчёт — быстрым чтением (только ссылки, без открытия документов в CAD), свойства — потом и только у
/// загружаемых документов; причины, по которым нельзя поставить на учёт.
/// </summary>
public sealed class ImportFastReadTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private TestCadAdapter Cad => (TestCadAdapter)_env.Registry.Get(TestCadAdapter.Id)!;

    private string Src(string rel) => _env.Path("src", rel);

    [Fact]
    public async Task Report_FastReadOnly_PropertiesLater_OnlyForUploadedDocuments_HeadFirst()
    {
        var src = _env.CreateSourceProject();
        // Соседнее изделие в той же папке (сборка, ссылающаяся на комплект) — не чертёж, его не читают вовсе.
        TestCadWriter.Write(Src("Соседнее.tcasm"), new TestCadDocument().Prop("Обозначение", "ГДЕ.000").Ref(Src(@"Детали\Болт.tcprt")));
        Cad.PropertiesDelay = TimeSpan.FromMilliseconds(30); // «открытие документа» заметно дороже ссылок
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(Src("Изделие.tcasm"), options: new ImportOptions { DeferProperties = true });

        // Отчёт построен без единого открытия документа: свойств ещё нет, конфигурации и ссылки — есть.
        Assert.Equal(0, Cad.FullReadCount);
        Assert.Empty(Cad.PropertiesReads);
        Assert.Equal(7, report.IncludedDocuments.Count());
        Assert.All(report.Documents, d => Assert.True(d.HasStructure && !d.HasProperties && d.Designation == null));
        Assert.Equal(new[] { "Исп1", "Исп2" }, report.Head.Configurations);
        Assert.True(report.HasPendingProperties);
        var pending = Assert.Single(report.BlockingReasons(false));
        Assert.Equal(ImportBlockingCodes.PropertiesPending, pending.Code);
        Assert.Equal("свойства прочитаны у 0 из 7 загружаемых документов", pending.Text);
        Assert.True(report.CanExecute(false)); // проблем нет — ждём только свойства
        Assert.DoesNotContain(report.Documents, d => d.FileName is "Соседнее.tcasm" or "Чужой.tcdrw");

        // Вал оставляем ссылкой — его свойства не нужны.
        report.Documents.Single(d => d.FileName == "Вал.tcprt").Include = false;
        report.Recompute();
        var read = await c.Service.ReadPendingImportPropertiesAsync(report);

        Assert.Equal(6, read);
        Assert.Equal(6, Cad.PropertiesReads.Count);
        Assert.Equal(report.Head.StagingPath, Cad.PropertiesReads[0]); // головной — первым
        var readNames = Cad.PropertiesReads.Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("Вал.tcprt", readNames);
        Assert.DoesNotContain("Соседнее.tcasm", readNames);
        Assert.DoesNotContain("Чужой.tcdrw", readNames);
        Assert.All(Cad.PropertiesReads, p => Assert.StartsWith(report.StagingRoot, p)); // читается копия, не исходник
        Assert.Equal("АБВ.000", report.Head.Designation);
        Assert.Equal("АБВ.001 СБ", report.Documents.Single(d => d.FileName == "Корпус.tcdrw").Designation);
        Assert.Null(report.Documents.Single(d => d.FileName == "Вал.tcprt").Designation);
        Assert.False(report.HasPendingProperties);
        Assert.Empty(report.BlockingReasons(false));

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка");
        Assert.True(result.Success, result.Message);
        Assert.Equal("АБВ.000", _env.Server.Summary(_env.Server.Doc(result.HeadDocumentId)).Designation);
        Assert.Equal(6, Cad.PropertiesReads.Count); // постановка ничего не дочитывала
    }

    [Fact]
    public async Task Execute_ReadsPendingProperties_ItselfBeforeUpload()
    {
        var src = _env.CreateSourceProject();
        var c = _env.For("ivanov");
        using var report = await c.Service.PrepareImportAsync(Src("Изделие.tcasm"), options: new ImportOptions { DeferProperties = true });
        Assert.Empty(Cad.PropertiesReads);

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка");

        Assert.True(result.Success, result.Message);
        Assert.Equal(7, Cad.PropertiesReads.Count);
        Assert.Equal("АБВ.001", _env.Server.Summary(_env.Server.DocByPath("Детали/Корпус.tcprt")).Designation);
    }

    [Fact]
    public async Task SameNameOpenElsewhere_BlockingReason_RereadOnlyThatFile()
    {
        var src = _env.CreateSourceProject();
        // В CAD открыт «Корпус.tcprt» из другой папки: копию с тем же именем CAD не откроет.
        var other = TestCadWriter.Create(_env.Path("Другое", "Корпус.tcprt"));
        Cad.OpenDocuments.Add(other);
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(Src("Изделие.tcasm"));

        var body = report.Documents.Single(d => d.FileName == "Корпус.tcprt");
        Assert.NotNull(body.ReadError);
        Assert.Equal(other, body.SameNameOpenPath);
        Assert.Null(report.Head.ReadError);
        Assert.Equal("АБВ.000", report.Head.Designation);
        Assert.False(report.CanExecute(true)); // ошибку чтения флажок «Разрешить…» не снимает

        var reasons = report.BlockingReasons(allowMissing: true, projectSelected: false);
        Assert.Equal(new[] { ImportBlockingCodes.SameNameOpen, ImportBlockingCodes.NoProject }, reasons.Select(r => r.Code));
        var sameName = reasons[0];
        Assert.Equal($"1 файл не прочитан (в TestCad открыт одноимённый документ из другой папки: {other})", sameName.Text);
        Assert.Equal("закройте его в TestCad и нажмите «Прочитать заново» или снимите у него «Загружать»", sameName.Action);
        Assert.True(sameName.CanReread);
        Assert.Equal(ImportReportTab.Documents, sameName.Tab);
        Assert.Same(body, Assert.Single(sameName.Documents));
        Assert.Equal("не выбран проект — выберите проект", reasons[1].ToString());
        Assert.StartsWith("Нельзя поставить на учёт: 1 файл не прочитан", ImportReport.BlockingSummary(reasons));

        // Закрыли чужой документ — «Прочитать заново» только этот файл: остальные свойства не перечитываются.
        Cad.OpenDocuments.Remove(other);
        var before = Cad.PropertiesReads.Count;
        var again = await c.Service.RereadImportDocumentsAsync(report, sameName.Documents);
        Assert.Same(body, Assert.Single(again));
        Assert.Null(body.ReadError);
        Assert.True(body.NeedsProperties);
        Assert.Equal(1, await c.Service.ReadPendingImportPropertiesAsync(report));
        Assert.Equal(before + 1, Cad.PropertiesReads.Count);
        Assert.Equal("АБВ.001", body.Designation);
        Assert.Empty(report.BlockingReasons(false));
    }

    [Fact]
    public async Task BlockingReasons_MissingUnconfirmedAmbiguous_CountsTabsAndAllowMissing()
    {
        TestCadWriter.Create(Src(@"Детали\Найден.tcprt"));
        TestCadWriter.Create(Src(@"Детали\Шайба.tcprt"));
        TestCadWriter.Create(Src(@"A\Гайка.tcprt"));
        TestCadWriter.Create(Src(@"B\Гайка.tcprt"), geometry: "другая");
        var head = TestCadWriter.Create(Src("Сборка.tcasm"), new[]
        {
            Src(@"Детали\Найден.tcprt"), @"Q:\Старый\Шайба.tcprt", @"Q:\Старый\Гайка.tcprt", @"Q:\Старый\Нет1.tcprt", @"Q:\Старый\Нет2.tcprt",
        });
        var c = _env.For("ivanov");
        using var report = await c.Service.PrepareImportAsync(head);

        var reasons = report.BlockingReasons(false);
        Assert.Equal(new[] { LocalProblemCodes.AmbiguousReference, LocalProblemCodes.Unconfirmed, LocalProblemCodes.MissingReference }, reasons.Select(r => r.Code));
        Assert.Equal("1 неоднозначная ссылка — выберите вариант на вкладке «Неоднозначно»", reasons[0].ToString());
        Assert.Equal(ImportReportTab.Ambiguous, reasons[0].Tab);
        Assert.Equal("1 ссылка, совпавшая только по имени, не подтверждена", reasons[1].Text);
        Assert.Equal(ImportReportTab.NameOnly, reasons[1].Tab);
        Assert.Equal("2 отсутствующие ссылки", reasons[2].Text);
        Assert.Equal("найдите файлы (добавьте их папку в поиск и прочитайте ссылки заново) или отметьте «Разрешить…»", reasons[2].Action);
        Assert.Equal(ImportReportTab.Missing, reasons[2].Tab);
        Assert.Equal(new[] { "Нет1.tcprt", "Нет2.tcprt" }, reasons[2].References.Select(r => r.FileName));
        Assert.True(reasons[2].AllowMissingClears);
        Assert.Equal("добавьте папку с файлами: --extra <папка> или разрешите: --allow-missing", reasons[2].CliAction);

        // «Разрешить…» снимает отсутствующие и неподтверждённые, но не неоднозначную.
        Assert.Equal(LocalProblemCodes.AmbiguousReference, Assert.Single(report.BlockingReasons(true)).Code);
        var nut = report.Head.References.Single(r => r.FileName == "Гайка.tcprt");
        nut.Chosen = nut.Candidates[0];
        report.Recompute();
        await c.Service.ReadPendingImportPropertiesAsync(report);
        Assert.Empty(report.BlockingReasons(true));
        Assert.True(report.CanExecute(true));
    }

    [Fact]
    public void Plural_RussianForms()
    {
        Assert.Equal("1 файл", ImportReport.Plural(1, "файл", "файла", "файлов"));
        Assert.Equal("3 файла", ImportReport.Plural(3, "файл", "файла", "файлов"));
        Assert.Equal("5 файлов", ImportReport.Plural(5, "файл", "файла", "файлов"));
        Assert.Equal("11 файлов", ImportReport.Plural(11, "файл", "файла", "файлов"));
        Assert.Equal("21 файл", ImportReport.Plural(21, "файл", "файла", "файлов"));
        Assert.Equal("Свойства 1 из 4: «a»", ImportReport.PropertiesProgressText(0, 4, TimeSpan.Zero, "a"));
        Assert.Equal("Свойства 3 из 4, осталось ≈ 01:10: «a»", ImportReport.PropertiesProgressText(2, 4, TimeSpan.FromSeconds(70), "a"));
    }

    [Fact]
    public async Task Cancel_DuringProperties_StagingRemoved_SourcesUntouched()
    {
        var src = _env.CreateSourceProject();
        var before = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Client.Hash(f), File.GetAttributes(f), File.GetLastWriteTimeUtc(f)));
        Cad.PropertiesDelay = TimeSpan.FromMilliseconds(200);
        var c = _env.For("ivanov");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgressProbe(p =>
        {
            if (p.Message.StartsWith("Свойства 2 из", StringComparison.Ordinal)) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            c.Service.PrepareImportAsync(Src("Изделие.tcasm"), null, progress, cts.Token));

        var staging = Path.Combine(c.Cache.Root, "staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any());
        Assert.Equal(before, Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Client.Hash(f), File.GetAttributes(f), File.GetLastWriteTimeUtc(f))));
    }

    private sealed class SyncProgressProbe(Action<WorkProgress> action) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value) => action(value);
    }
}
