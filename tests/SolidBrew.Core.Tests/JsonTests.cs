using System.Text.Json;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;

namespace SolidBrew.Core.Tests;

public class JsonTests
{
    private static T RoundTrip<T>(T value, out string json)
    {
        json = JsonSerializer.Serialize(value, PdmJson.Options);
        return JsonSerializer.Deserialize<T>(json, PdmJson.Options)!;
    }

    [Fact]
    public void Enums_AreCamelCaseStrings_AndNullsAreOmitted()
    {
        var dto = new DocumentSummaryDto
        {
            Id = Guid.NewGuid(),
            ProjectKind = ProjectKind.Library,
            Kind = DocumentKind.Assembly,
            Status = DocumentStatus.Unused,
            RelPath = "Сборки/АИРТ.467124.016 Имитатор центрального пульта.SLDASM",
            Designation = null,
        };

        var back = RoundTrip(dto, out var json);

        Assert.Contains("\"kind\":\"assembly\"", json);
        Assert.Contains("\"projectKind\":\"library\"", json);
        Assert.Contains("\"status\":\"unused\"", json);
        Assert.DoesNotContain("designation", json);
        Assert.DoesNotContain("\"lock\"", json);
        Assert.Contains("Имитатор центрального пульта", json); // кириллица без \uXXXX
        Assert.Equal(dto.RelPath, back.RelPath);
        Assert.Equal(DocumentKind.Assembly, back.Kind);
        Assert.Null(back.Lock);
    }

    [Fact]
    public void Version_PropertiesKeepDictionaryKeysAsIs()
    {
        var v = new VersionDto
        {
            DocumentId = Guid.NewGuid(),
            Number = 3,
            At = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.FromHours(3)),
            Configurations = { "По умолчанию", "Исп. 01" },
            Properties =
            {
                [""] = new Dictionary<string, string> { ["Обозначение"] = "АИРТ.467124.016" },
                ["Исп. 01"] = new Dictionary<string, string> { ["Material"] = "Сталь 20" },
            },
            References = { new VersionRefDto { RefPath = @"C:\old\Деталь.SLDPRT" } },
            Derived = { new DerivedFileDto { FileName = "Деталь.pdf", Sha256 = new string('a', 64), Size = 10 } },
        };

        var back = RoundTrip(v, out var json);

        Assert.Contains("\"Обозначение\"", json);
        Assert.Contains("\"Material\"", json);
        Assert.Equal("АИРТ.467124.016", back.Properties[""]["Обозначение"]);
        Assert.Equal("Сталь 20", back.Properties["Исп. 01"]["Material"]);
        Assert.Equal(v.At, back.At);
        Assert.Null(back.References[0].ChildDocumentId);
        Assert.Equal(2, back.Configurations.Count);
    }

    [Fact]
    public void Checkin_RoundTrip()
    {
        var docId = Guid.NewGuid();
        var req = new CheckinRequest
        {
            ProjectId = Guid.NewGuid(),
            Comment = "Первая сдача",
            KeepLocks = true,
            Items =
            {
                new CheckinItem
                {
                    TempKey = "a1",
                    RelPath = "Сборка.SLDASM",
                    Kind = DocumentKind.Assembly,
                    References =
                    {
                        new CheckinReference { RefPath = "Деталь.SLDPRT", DocumentId = docId, Version = 2 },
                        new CheckinReference { RefPath = "Новая.SLDPRT", TempKey = "a2" },
                        new CheckinReference { RefPath = "Нет.SLDPRT" },
                    },
                },
            },
        };

        var back = RoundTrip(req, out var json);

        Assert.DoesNotContain("baseVersion", json);
        Assert.True(back.KeepLocks);
        var refs = back.Items[0].References;
        Assert.Equal(docId, refs[0].DocumentId);
        Assert.Equal(2, refs[0].Version);
        Assert.Equal("a2", refs[1].TempKey);
        Assert.Null(refs[2].DocumentId);
        Assert.Null(refs[2].TempKey);
    }

    [Fact]
    public void Reading_IsCaseInsensitive()
    {
        var user = JsonSerializer.Deserialize<UserDto>("{\"LOGIN\":\"ivanov\",\"Role\":\"admin\",\"active\":true}", PdmJson.Options)!;
        Assert.Equal("ivanov", user.Login);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.True(user.Active);
    }

    [Fact]
    public void ErrorResponse_WithLock_RoundTrip()
    {
        var err = new ErrorResponse
        {
            Message = "Документ взят другим",
            Problems = { new ProblemDto { Code = ProblemCodes.LockedByOther, Message = "Занят", RelPath = "a.SLDPRT" } },
            Lock = new LockDto { DocumentId = Guid.NewGuid(), UserLogin = "petrov", UserDisplayName = "Петров П." },
        };
        var back = RoundTrip(err, out var json);
        Assert.Contains("\"code\":\"lockedByOther\"", json);
        Assert.Equal("Петров П.", back.Lock!.UserDisplayName);
        Assert.Equal(ProblemCodes.LockedByOther, back.Problems[0].Code);
    }

    [Fact]
    public void ExportManifest_And_Settings_RoundTrip()
    {
        var m = new ExportManifest
        {
            HeadDocumentId = Guid.NewGuid(),
            HeadVersion = 5,
            CreatedBy = "ivanov",
            Files = { new ExportManifestFile { RelPath = "a/b.pdf", Derived = true, Size = 42 } },
        };
        var back = RoundTrip(m, out _);
        Assert.True(back.Files[0].Derived);
        Assert.Equal(5, back.HeadVersion);

        var s = new SettingsDto { Values = { [SettingKeys.WorkingVersion("solidworks")] = "2022" } };
        var sb = RoundTrip(s, out var sjson);
        Assert.Contains("cad.solidworks.workingVersion", sjson);
        Assert.Equal("2022", sb.Values["cad.solidworks.workingVersion"]);
    }

    [Fact]
    public void Options_AreReadOnly()
    {
        Assert.True(PdmJson.Options.IsReadOnly);
        var copy = new JsonSerializerOptions();
        PdmJson.Apply(copy);
        Assert.Equal("\"drawing\"", JsonSerializer.Serialize(DocumentKind.Drawing, copy));
    }
}
