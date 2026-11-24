using SolidBrew.Adapters.TestCad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace.Tests;

public class ImportTests : IDisposable
{
    private readonly TestEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static Dictionary<string, (string Hash, FileAttributes Attrs, DateTime Write)> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Client.Hash(f), File.GetAttributes(f), File.GetLastWriteTimeUtc(f)));

    [Fact]
    public async Task Import_StandardProject_DrawingsFoundByReferences_SourcesUntouched()
    {
        var src = _env.CreateSourceProject();
        File.SetAttributes(System.IO.Path.Combine(src, @"Детали\Вал.tcprt"), FileAttributes.ReadOnly);
        var before = Snapshot(src);
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(System.IO.Path.Combine(src, "Изделие.tcasm"), new[] { System.IO.Path.Combine(src, "Чертежи") });

        var included = report.IncludedDocuments.Select(d => d.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(new[]
        {
            "Детали/Болт.tcprt", "Детали/Вал.tcprt", "Детали/Корпус.tcprt", "Изделие.tcasm",
            "Сборки/Узел.tcasm", "Чертежи/Изделие.tcdrw", "Чертежи/Корпус.tcdrw",
        }, included);
        Assert.DoesNotContain(report.Documents, d => d.FileName == "Чужой.tcdrw");
        Assert.All(report.References, r => Assert.Equal(ImportReferenceCategory.Found, r.Category));
        Assert.True(report.CanExecute(false));
        Assert.All(report.Documents, d => Assert.StartsWith(System.IO.Path.Combine(c.Cache.Root, "staging"), d.StagingPath));

        var result = await c.Service.ExecuteImportAsync(report, _env.Project.Id, "Постановка");
        Assert.True(result.Success, result.Message);
        Assert.Equal(7, _env.Server.Docs.Count);
        Assert.Equal(DocumentKind.Drawing, _env.Server.DocByPath("Чертежи/Корпус.tcdrw").Latest.Kind);
        var drw = _env.Server.DocByPath("Чертежи/Корпус.tcdrw").Latest;
        Assert.Equal(_env.DocId("Детали/Корпус.tcprt"), drw.Refs.Single().Child);
        Assert.Equal(System.IO.Path.Combine(src, @"Детали\Корпус.tcprt"), drw.Refs.Single().RefPath);
        Assert.Equal("АБВ.000", _env.Server.Summary(_env.Server.Doc(result.HeadDocumentId)).Designation);

        // Исходники не изменены: хеши, атрибуты, время.
        Assert.Equal(before, Snapshot(src));
        Assert.False(Directory.Exists(report.StagingRoot));
    }

    [Fact]
    public async Task Import_AllFiveCategories_AndDecisions()
    {
        var src = _env.Path("src");
        var outside = TestCadWriter.Create(_env.Path("lib", "Стандарт.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "Детали", "Найден.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "Детали", "Шайба.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "A", "Гайка.tcprt"));
        TestCadWriter.Create(System.IO.Path.Combine(src, "B", "Гайка.tcprt"), geometry: "другая гайка");
        var head = TestCadWriter.Create(System.IO.Path.Combine(src, "Сборка.tcasm"), new[]
        {
            System.IO.Path.Combine(src, "Детали", "Найден.tcprt"),
            outside,
            @"Q:\Старый диск\Шайба.tcprt",
            @"Q:\Старый диск\Гайка.tcprt",
            @"Q:\Старый диск\Нет.tcprt",
        });
        var before = Snapshot(_env.Root);
        var c = _env.For("ivanov");

        using var report = await c.Service.PrepareImportAsync(head);
        var refs = report.Head.References.ToDictionary(r => r.FileName);
        Assert.Equal(ImportReferenceCategory.Found, refs["Найден.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.OutsideFolders, refs["Стандарт.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.NameOnly, refs["Шайба.tcprt"].Category);
        Assert.Equal(ImportReferenceCategory.Ambiguous, refs["Гайка.tcprt"].Category);
        Assert.Equal(2, refs["Гайка.tcprt"].Candidates.Count);
        Assert.Equal(ImportReferenceCategory.Missing, refs["Нет.tcprt"].Category);

        // По умолчанию: включён только найденный; неоднозначность блокирует даже с allowMissing.
        Assert.Equal(new[] { "Найден.tcprt", "Сборка.tcasm" }, report.IncludedDocuments.Select(d => d.FileName).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(report.CanExecute(false));
        Assert.False(report.CanExecute(true));
        Assert.Contains(report.Problems, p => p.Code == LocalProblemCodes.AmbiguousReference);
        var denied = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: true);
        Assert.False(denied.Success);
        Assert.Empty(_env.Server.Docs);

        // Решения: выбрать гайку, подтвердить шайбу; «вне папок» не подтверждаем.
        refs["Гайка.tcprt"].Chosen = refs["Гайка.tcprt"].Candidates.First(p => p.Contains(@"\B\"));
        refs["Шайба.tcprt"].Accept = true;
        report.Recompute();
        Assert.Equal(new[] { "B/Гайка.tcprt", "Детали/Найден.tcprt", "Детали/Шайба.tcprt", "Сборка.tcasm" }, report.IncludedDocuments.Select(d => d.RelPath).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(report.CanExecute(false)); // отсутствующая и неподтверждённая
        Assert.True(report.CanExecute(true));

        var refused = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: false);
        Assert.False(refused.Success);
        Assert.Empty(_env.Server.Docs);

        var ok = await c.Service.ExecuteImportAsync(report, _env.Project.Id, null, allowMissing: true);
        Assert.True(ok.Success, ok.Message + string.Join("\n", ok.Problems));
        Assert.Equal(4, _env.Server.Docs.Count);
        var asm = _env.Server.Doc(ok.HeadDocumentId).Latest;
        Assert.Equal(2, asm.Refs.Count(r => r.Child == null)); // Стандарт (не подтверждён) и Нет
        Assert.Equal(_env.DocId("B/Гайка.tcprt"), asm.Refs.Single(r => r.RefPath.EndsWith("Гайка.tcprt")).Child);

        Assert.Equal(before, Snapshot(_env.Root).Where(p => before.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public async Task Import_ThenOpen_DownloadsNothing()
    {
        var c = _env.For("ivanov");
        var (head, _) = await _env.ImportSourceAsync(c);
        var open = await c.OpenAsync(head, verify: true);
        Assert.Equal(0, open.Downloaded);
        Assert.Equal(0, _env.Server.DownloadCount);
        Assert.True(open.CadReport!.Ok, string.Join("\n", open.CadReport.Problems));
    }
}
