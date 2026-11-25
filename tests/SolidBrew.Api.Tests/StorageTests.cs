using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Npgsql;
using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using static SolidBrew.Api.Tests.TestCad;

namespace SolidBrew.Api.Tests;

[Collection(PdmCollection.Name)]
public sealed class StorageTests(PdmServerFixture fx)
{
    [Theory]
    [InlineData("UPDATE versions SET size = size + 1")]
    [InlineData("DELETE FROM versions")]
    [InlineData("TRUNCATE versions CASCADE")]
    [InlineData("UPDATE version_refs SET ref_path = 'x'")]
    [InlineData("DELETE FROM derived_files")]
    [InlineData("UPDATE checkins SET comment = 'x'")]
    [InlineData("DELETE FROM journal")]
    public async Task PublishedData_CannotBeChanged_ByAppRole_OrOwner(string sql)
    {
        await CheckinTests.PublishKitAsync(fx);

        await using (var app = new NpgsqlConnection(fx.AppConnectionString))
        {
            await app.OpenAsync();
            await using var command = new NpgsqlCommand(sql, app);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        await using (var owner = new NpgsqlConnection(fx.OwnerConnectionString))
        {
            await owner.OpenAsync();
            await using var command = new NpgsqlCommand(sql, owner);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.RaiseException, ex.SqlState);
            Assert.Contains("нельзя изменить или удалить", ex.MessageText);
        }
    }

    [Fact]
    public async Task Upload_WrongHash_Is422_TooLarge_Is413_Existing_IsAccepted()
    {
        var (designer, _) = await fx.NewUserAsync(UserRole.Designer);
        var data = Encoding.UTF8.GetBytes("содержимое " + Guid.NewGuid());
        var wrongSha = Sha(Encoding.UTF8.GetBytes("другое"));
        using (var stream = new MemoryStream(data))
        {
            var ex = await Assert.ThrowsAsync<PdmApiException>(() => designer.UploadObjectAsync(wrongSha, stream, data.Length, null));
            Assert.Equal(422, ex.StatusCode);
            Assert.Equal(ProblemCodes.HashMismatch, ex.Error!.Problems.Single().Code);
        }
        Assert.Contains(wrongSha, await designer.GetMissingObjectsAsync([wrongSha]));

        var sha = Sha(data);
        using (var stream = new MemoryStream(data)) await designer.UploadObjectAsync(sha, stream, data.Length, null);
        using (var stream = new MemoryStream(data)) await designer.UploadObjectAsync(sha, stream, data.Length, null);
        Assert.Empty(await designer.GetMissingObjectsAsync([sha]));

        using var copy = new MemoryStream();
        await designer.DownloadObjectAsync(sha, copy, null);
        Assert.Equal(data, copy.ToArray());

        var big = new byte[1024 * 1024 + 1];
        using (var stream = new MemoryStream(big))
        {
            var ex = await Assert.ThrowsAsync<PdmApiException>(() => designer.UploadObjectAsync(Sha(big), stream, big.Length, null));
            Assert.Equal(413, ex.StatusCode);
        }

        var bad = await Assert.ThrowsAsync<PdmApiException>(() => designer.GetMissingObjectsAsync(["not-a-hash"]));
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task VersionsByHash_FindsPublishedVersions_UnknownEmpty_BadHash400()
    {
        var kit = await CheckinTests.PublishKitAsync(fx);
        var d = kit.Designer;
        var detail = await d.GetDocumentAsync(kit.Part1);
        var sha = detail.Versions.Single(v => v.Number == 1).Sha256;
        var unknown = Sha(Encoding.UTF8.GetBytes("нет такого " + Guid.NewGuid()));

        var found = await d.FindVersionsByHashAsync([sha, unknown, sha]);
        var hit = Assert.Single(found, f => f.Document.Id == kit.Part1);
        Assert.Equal(sha, hit.Sha256);
        Assert.Equal(1, hit.Version);
        Assert.Equal(kit.Project.Id, hit.Document.ProjectId);
        Assert.DoesNotContain(found, f => f.Sha256 == unknown);
        Assert.Empty(await d.FindVersionsByHashAsync([]));

        var bad = await Assert.ThrowsAsync<PdmApiException>(() => d.FindVersionsByHashAsync(["not-a-hash"]));
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task Kit_OldAssemblyKeepsOldPart_WhereUsedShowsBoth()
    {
        var kit = await CheckinTests.PublishKitAsync(fx);
        var d = kit.Designer;
        await d.LockAsync(kit.Part1);
        await d.CheckinAsync(Request(kit.Project.Id, await EditAsync(d, kit.Part1, 1, "Детали/Вал.prt", DocumentKind.Part, "вал v2 " + Guid.NewGuid())));

        var old = await d.GetKitAsync(kit.Asm, 1);
        var part = old.Items.Single(i => i.DocumentId == kit.Part1);
        Assert.Equal(1, part.Version);
        Assert.Equal(2, part.LatestVersion);

        // Новая сборка — с деталью v2.
        await d.LockAsync(kit.Asm);
        await d.CheckinAsync(Request(kit.Project.Id, await EditAsync(d, kit.Asm, 1, "Узел.asm", DocumentKind.Assembly, "узел v2 " + Guid.NewGuid(),
            RefPublished("Вал.prt", kit.Part1, 2), RefPublished("Втулка.prt", kit.Part2, 1))));
        var used = await d.GetWhereUsedAsync(kit.Part1, 2);
        Assert.Contains(used.Items, i => i.ParentDocumentId == kit.Asm && i.ParentVersion == 1 && i.ChildVersion == 1);
        Assert.Contains(used.Items, i => i.ParentDocumentId == kit.Asm && i.ParentVersion == 2 && i.ChildVersion == 2 && i.UsesLatestChild);

        // Подсборка с деталью v2 и верхняя сборка с деталью v1: в комплекте деталь в двух версиях.
        var sub = await NewAsync(d, "sub", "Подузел.asm", DocumentKind.Assembly, "sub " + Guid.NewGuid(), RefPublished("Вал.prt", kit.Part1, 2));
        var top = await NewAsync(d, "top", "Изделие.asm", DocumentKind.Assembly, "top " + Guid.NewGuid(), RefTemp("Подузел.asm", "sub"), RefPublished("Вал.prt", kit.Part1, 1));
        var result = await d.CheckinAsync(Request(kit.Project.Id, top, sub));
        var topId = result.Items.Single(i => i.TempKey == "top").DocumentId;
        var conflict = await d.GetKitAsync(topId, 1);
        Assert.Equal(2, conflict.Items.Count(i => i.DocumentId == kit.Part1));

        var export = await Assert.ThrowsAsync<PdmApiException>(() => d.ExportAsync(topId, 1, false, new MemoryStream()));
        Assert.Equal(409, export.StatusCode);
    }

    [Fact]
    public async Task Export_ZipWithManifest_ByReader_IsJournaled()
    {
        var kit = await CheckinTests.PublishKitAsync(fx);
        var (reader, readerUser) = await fx.NewUserAsync(UserRole.Reader, kit.Admin);

        using var zipBytes = new MemoryStream();
        await reader.ExportAsync(kit.Drawing, 1, includeDerived: true, zipBytes);
        zipBytes.Position = 0;
        using var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "manifest.json", "Детали/Вал.prt", "Детали/Втулка.prt", "Узел.asm", "Узел.drw", "Узел.pdf" }.OrderBy(n => n, StringComparer.Ordinal), names);

        ExportManifest manifest;
        await using (var stream = zip.GetEntry(ExportManifest.FileName)!.Open())
            manifest = (await JsonSerializer.DeserializeAsync<ExportManifest>(stream, PdmJson.Options))!;
        Assert.Equal(kit.Drawing, manifest.HeadDocumentId);
        Assert.Equal(readerUser.Login, manifest.CreatedBy);
        Assert.Equal(5, manifest.Files.Count);
        Assert.True(manifest.Files.Single(f => f.RelPath == "Узел.pdf").Derived);
        foreach (var file in manifest.Files)
        {
            using var content = new MemoryStream();
            await using (var entry = zip.GetEntry(file.RelPath)!.Open()) await entry.CopyToAsync(content);
            Assert.Equal(file.Sha256, Sha(content.ToArray()));
            Assert.Equal(file.Size, content.Length);
        }

        using var withoutDerived = new MemoryStream();
        await reader.ExportAsync(kit.Drawing, 1, includeDerived: false, withoutDerived);
        withoutDerived.Position = 0;
        using (var zip2 = new ZipArchive(withoutDerived, ZipArchiveMode.Read))
            Assert.DoesNotContain(zip2.Entries, e => e.FullName.EndsWith(".pdf", StringComparison.Ordinal));

        var journal = await reader.GetDocumentJournalAsync(kit.Drawing);
        Assert.Equal(2, journal.Count(j => j.Action == JournalActions.Export && j.UserLogin == readerUser.Login && j.Version == 1));
    }
}
