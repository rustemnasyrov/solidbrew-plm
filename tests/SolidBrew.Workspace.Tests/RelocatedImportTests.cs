using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

/// <summary>
/// Папку проекта переименовали («Старая» → «Новая»): в файлах остались пути старой папки. Файл по ссылке ставится на учёт
/// с путём, под которым на него ссылается родитель, — его ссылки находятся на прежнем месте относительно него.
/// </summary>
public class RelocatedImportTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private string OldRoot => _env.Path("Старая");
    private string NewRoot => _env.Path("Новая");
    private string Model => System.IO.Path.Combine(NewRoot, "Models", "Светофильтры.tcprt");

    /// <summary>Две детали ссылаются на общую исходную модель по пути старой папки; рядом с деталями — одноимённая старая копия.</summary>
    private void CreateRenamedProject()
    {
        TestCadWriter.Create(Model, geometry: "исходная модель");
        var oldModel = System.IO.Path.Combine(OldRoot, "Models", "Светофильтры.tcprt");
        TestCadWriter.Create(System.IO.Path.Combine(NewRoot, "Козырек", "Деталь1.tcprt"), new[] { oldModel }, geometry: "деталь 1");
        TestCadWriter.Create(System.IO.Path.Combine(NewRoot, "Козырек", "Деталь2.tcprt"), new[] { oldModel }, geometry: "деталь 2");
        TestCadWriter.Create(System.IO.Path.Combine(NewRoot, "Козырек", "Архив", "Светофильтры.tcprt"), geometry: "старая копия");
    }

    private ImportOptions Linked(string part) => new()
    {
        RelPathPrefix = "Сборки",
        AllowExistingHead = true,
        HeadSavedPath = System.IO.Path.Combine(OldRoot, "Козырек", part),
    };

    [Fact]
    public async Task RenamedFolder_ReferenceFoundAtSamePlace_ConfirmedOnce_PlacedRelativeToHead()
    {
        CreateRenamedProject();
        var c = _env.For("ivanov");
        var part = System.IO.Path.Combine(NewRoot, "Козырек", "Деталь1.tcprt");

        using var report = await c.Service.PrepareImportAsync(part, null, null, default, Linked("Деталь1.tcprt"));

        // Найден файл на прежнем месте, а не одноимённая копия в папке поиска.
        var r = Assert.Single(report.Head.References);
        Assert.Equal(ImportReferenceCategory.Relocated, r.Category);
        Assert.Equal(Model, r.TargetPath);
        Assert.Equal(System.IO.Path.Combine(OldRoot, "Козырек"), r.MovedFrom);
        Assert.Equal(System.IO.Path.Combine(NewRoot, "Козырек"), r.MovedTo);
        Assert.Null(r.EffectiveTarget);

        // PDM не принимает его сама.
        Assert.False(report.CanExecute(false));
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.Unconfirmed && p.Message.Contains("перенесён"));
        Assert.Single(report.IncludedDocuments);

        var accepted = report.AcceptConfirmable();
        Assert.Same(r, Assert.Single(accepted));
        Assert.True(report.CanExecute(false));
        // Головной — ровно рядом с родителем, модель — на том же месте относительно головного, что и на диске.
        Assert.Equal(new[] { "Models/Светофильтры.tcprt", "Сборки/Деталь1.tcprt" },
            report.IncludedDocuments.Select(d => d.RelPath).OrderBy(p => p, StringComparer.Ordinal));

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null);
        Assert.True(result.Success, result.Message + string.Join("\n", result.Problems));
        var head = _env.Server.Doc(result.HeadDocumentId).Latest;
        Assert.Equal(_env.DocId("Models/Светофильтры.tcprt"), head.Refs.Single().Child);
    }

    [Fact]
    public async Task WithoutSavedPath_NoGuessByPlace_OnlyNameMatch()
    {
        CreateRenamedProject();
        var c = _env.For("ivanov");
        var part = System.IO.Path.Combine(NewRoot, "Козырек", "Деталь1.tcprt");

        using var report = await c.Service.PrepareImportAsync(part, null, null, default, new ImportOptions { RelPathPrefix = "Сборки", AllowExistingHead = true });

        var r = Assert.Single(report.Head.References);
        Assert.Equal(ImportReferenceCategory.NameOnly, r.Category);
        Assert.Contains(@"\Архив\", r.TargetPath);
    }

    [Fact]
    public async Task SavedPathOnAnotherDisk_NotRelocated()
    {
        CreateRenamedProject();
        var c = _env.For("ivanov");
        var part = System.IO.Path.Combine(NewRoot, "Козырек", "Деталь1.tcprt");

        using var report = await c.Service.PrepareImportAsync(part, null, null, default,
            new ImportOptions { AllowExistingHead = true, HeadSavedPath = @"Q:\Другой диск\Козырек\Деталь1.tcprt" });

        Assert.NotEqual(ImportReferenceCategory.Relocated, Assert.Single(report.Head.References).Category);
    }

    [Fact]
    public async Task TwoPartsWithSharedModel_PreparedTogether_SecondTakesModelFromStorage()
    {
        CreateRenamedProject();
        var c = _env.For("ivanov");
        var reports = new List<ImportReport>();
        try
        {
            foreach (var name in new[] { "Деталь1.tcprt", "Деталь2.tcprt" })
            {
                var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(NewRoot, "Козырек", name), null, null, default, Linked(name));
                Assert.Single(report.AcceptConfirmable());
                Assert.True(report.CanExecute(false));
                reports.Add(report);
            }
            // Подготовлены до постановки — обе собираются загрузить модель.
            Assert.All(reports, r => Assert.Equal(ImportDisposition.New, r.Documents.Single(d => d.FileName == "Светофильтры.tcprt").Disposition));

            var first = await c.Service.ExecuteImportAsync(reports[0], _env.Project.Id, null);
            Assert.True(first.Success, first.Message);

            await c.Service.RefreshExistingAsync(reports[1]);
            Assert.Equal(ImportDisposition.FromStorage, reports[1].Documents.Single(d => d.FileName == "Светофильтры.tcprt").Disposition);
            var second = await c.Service.ExecuteImportAsync(reports[1], _env.Project.Id, null);
            Assert.True(second.Success, second.Message + string.Join("\n", second.Problems));
        }
        finally
        {
            foreach (var r in reports) r.Dispose();
        }

        Assert.Equal(3, _env.Server.Docs.Count); // модель — один документ
        var modelId = _env.DocId("Models/Светофильтры.tcprt");
        Assert.Equal(modelId, _env.Server.DocByPath("Сборки/Деталь1.tcprt").Latest.Refs.Single().Child);
        Assert.Equal(modelId, _env.Server.DocByPath("Сборки/Деталь2.tcprt").Latest.Refs.Single().Child);
    }
}
