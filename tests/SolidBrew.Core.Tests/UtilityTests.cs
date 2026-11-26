using System.Text;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;
using SolidBrew.Core.Paths;

namespace SolidBrew.Core.Tests;

public class RelPathTests
{
    [Theory]
    [InlineData(@"Сборки\АИРТ.467124.016 Имитатор центрального пульта.SLDASM", "Сборки/АИРТ.467124.016 Имитатор центрального пульта.SLDASM")]
    [InlineData("a//b/./c.SLDPRT", "a/b/c.SLDPRT")]
    [InlineData("a/b/", "a/b")]
    public void Normalize_Works(string input, string expected) => Assert.Equal(expected, RelPath.Normalize(input));

    [Fact]
    public void Normalize_ProducesNfc()
    {
        var decomposed = "И\u0306мя.SLDPRT"; // «Й» из двух кодовых точек
        var result = RelPath.Normalize(decomposed);
        Assert.Equal("Ймя.SLDPRT", result);
        Assert.True(result.IsNormalized(NormalizationForm.FormC));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/a/b")]
    [InlineData(@"\a")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData(@"C:\a\b")]
    [InlineData("a/b?.txt")]
    [InlineData("a /b")]
    [InlineData("a./b")]
    public void Normalize_RejectsInvalid(string input)
    {
        Assert.Throws<ArgumentException>(() => RelPath.Normalize(input));
        Assert.False(RelPath.IsValid(input));
    }

    [Fact]
    public void IsValid_RequiresNormalizedForm()
    {
        Assert.True(RelPath.IsValid("Папка/Деталь 1.SLDPRT"));
        Assert.False(RelPath.IsValid(@"Папка\Деталь 1.SLDPRT"));
        Assert.False(RelPath.IsValid("Папка//Деталь.SLDPRT"));
        Assert.False(RelPath.IsValid(null));
    }

    [Fact]
    public void Combine_And_GetRelative_RoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "Рабочая папка");
        var rel = "Сборки/Узел 1/Деталь.SLDPRT";
        var full = RelPath.Combine(root, rel);
        Assert.Equal(Path.Combine(root, "Сборки", "Узел 1", "Деталь.SLDPRT"), full);
        Assert.Equal(rel, RelPath.GetRelative(root, full));
        // Корень с другим регистром и завершающим разделителем; путь берётся из полного пути как есть.
        Assert.Equal(rel, RelPath.GetRelative(root.ToUpperInvariant() + Path.DirectorySeparatorChar, full));
    }

    [Fact]
    public void GetRelative_OutsideRoot_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "work");
        Assert.Throws<ArgumentException>(() => RelPath.GetRelative(root, Path.Combine(Path.GetTempPath(), "work2", "a.SLDPRT")));
        Assert.Throws<ArgumentException>(() => RelPath.GetRelative(root, Path.Combine(root, "..", "a.SLDPRT")));
        Assert.Throws<ArgumentException>(() => RelPath.GetRelative(root, root));
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        Assert.Equal(RelPath.Key("Сборки/деталь.sldprt"), RelPath.Key(@"СБОРКИ\Деталь.SLDPRT"));
        Assert.NotEqual(RelPath.Key("a/b.sldprt"), RelPath.Key("a/c.sldprt"));
    }

    [Fact]
    public void FileName_IsLastSegment() => Assert.Equal("Деталь.SLDPRT", RelPath.FileName("a/b/Деталь.SLDPRT"));
}

public class Sha256HexTests
{
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Compute_KnownVector()
    {
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal(AbcHash, Sha256Hex.Compute(ms));
    }

    [Fact]
    public async Task ComputeFile_MatchesStream_AndAllowsOpenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sb-{Guid.NewGuid():N} Деталь.bin");
        try
        {
            var data = new byte[2 * 1024 * 1024 + 5];
            new Random(7).NextBytes(data);
            await File.WriteAllBytesAsync(path, data);

            // Файл открыт на запись другим процессом (как в CAD) — читать всё равно можно.
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var fromFile = await Sha256Hex.ComputeFileAsync(path);
            Assert.Equal(Sha256Hex.Compute(new MemoryStream(data)), fromFile);
            Assert.True(Sha256Hex.IsValid(fromFile));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(AbcHash, true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", false)]
    [InlineData("ba7816bf", false)]
    [InlineData("ga7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", false)]
    [InlineData(null, false)]
    public void IsValid(string? value, bool expected) => Assert.Equal(expected, Sha256Hex.IsValid(value));
}

public class CadAdapterRegistryTests
{
    private sealed class FakeAdapter(string id, params string[] extensions) : ICadAdapter
    {
        public string CadId => id;
        public string DisplayName => id;
        public IReadOnlyCollection<string> Extensions => extensions;
        public bool IsDocument(string path) => true;
        public DocumentKind KindOf(string path) => DocumentKind.Other;
        public Task<CadDocumentInfo> ReadAsync(string path, CancellationToken ct = default) => Task.FromResult(new CadDocumentInfo());
        public Task<List<CadReference>> ReadReferencesAsync(string path, CancellationToken ct = default) => Task.FromResult(new List<CadReference>());
        public Task ReplaceReferencesAsync(string documentPath, IReadOnlyDictionary<string, string> oldToNew, CancellationToken ct = default) => Task.CompletedTask;
        public Task WritePropertiesAsync(string documentPath, IReadOnlyList<CadPropertyChange> changes, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveReferencesAsync(string documentPath, IReadOnlyList<string> savedPaths, CancellationToken ct = default) => Task.CompletedTask;
        public Task<OpenReport> OpenAndVerifyAsync(string headPath, string workRoot, CancellationToken ct = default) => Task.FromResult(new OpenReport());
    }

    [Fact]
    public void FindFor_ByExtension_CaseInsensitive()
    {
        var registry = new CadAdapterRegistry();
        var sw = new FakeAdapter("solidworks", ".sldprt", ".sldasm", ".slddrw");
        var test = new FakeAdapter("testcad", ".tcad");
        registry.Register(sw);
        registry.Register(test);

        Assert.Same(sw, registry.FindFor(@"C:\Работа\АИРТ.467124.016 Имитатор центрального пульта.SLDASM"));
        Assert.Same(sw, registry.FindFor("a/b.SldPrt"));
        Assert.Same(test, registry.FindFor("x.TCAD"));
        Assert.Null(registry.FindFor("readme.txt"));
        Assert.Null(registry.FindFor("noext"));
        Assert.Null(registry.FindFor(@"C:\a.b\noext"));
        Assert.Same(test, registry.Get("TestCad"));
        Assert.Null(registry.Get("kompas"));
        Assert.Equal(2, registry.All.Count);
    }

    [Fact]
    public void Register_Duplicates_Throw()
    {
        var registry = new CadAdapterRegistry();
        registry.Register(new FakeAdapter("solidworks", ".sldprt"));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeAdapter("solidworks", ".x")));
        Assert.Throws<ArgumentException>(() => registry.Register(new FakeAdapter("other", ".SLDPRT")));
        Assert.Single(registry.All);
    }

    [Fact]
    public void CadReference_FileNameOf_HandlesForeignPaths()
    {
        Assert.Equal("Деталь.SLDPRT", CadReference.FileNameOf(@"D:\Чужой ПК\Проект\Деталь.SLDPRT"));
        Assert.Equal("b.sldprt", CadReference.FileNameOf("a/b.sldprt"));
        Assert.Equal("c.sldprt", CadReference.FileNameOf("c.sldprt"));
    }
}

public class DocumentPropertiesTests
{
    private static Dictionary<string, Dictionary<string, string>> Props(params (string config, string key, string value)[] items)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (config, key, value) in items)
        {
            if (!result.TryGetValue(config, out var d)) result[config] = d = new Dictionary<string, string>();
            d[key] = value;
        }
        return result;
    }

    [Fact]
    public void FileLevel_HasPriority()
    {
        var p = Props(("По умолчанию", "Обозначение", "CONF.1"), ("", "обозначение", "АИРТ.467124.016"), ("", "НАИМЕНОВАНИЕ", "Имитатор"));
        Assert.Equal("АИРТ.467124.016", DocumentProperties.Designation(p));
        Assert.Equal("Имитатор", DocumentProperties.Name(p));
    }

    [Fact]
    public void FallsBackToFirstConfigurationWithValue()
    {
        var p = Props(("", "Material", "Сталь"), ("Конф1", "Description", " "), ("Конф2", "description", "Плата"), ("Конф3", "Name", "Другое"), ("Конф2", "PartNo", "P-1"));
        Assert.Equal("Плата", DocumentProperties.Name(p));
        Assert.Equal("P-1", DocumentProperties.Designation(p));
    }

    [Fact]
    public void KeyPriority_WithinLevel()
    {
        var p = Props(("", "Номер", "N-1"), ("", "Designation", "D-1"));
        Assert.Equal("D-1", DocumentProperties.Designation(p));
    }

    [Fact]
    public void Missing_ReturnsNull()
    {
        Assert.Null(DocumentProperties.Designation(null));
        Assert.Null(DocumentProperties.Name(new Dictionary<string, Dictionary<string, string>>()));
        Assert.Null(DocumentProperties.Name(Props(("", "Material", "Сталь"))));
    }
}
