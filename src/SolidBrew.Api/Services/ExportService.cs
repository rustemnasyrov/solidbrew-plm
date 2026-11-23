using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Net.Http.Headers;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Blobs;
using SolidBrew.Api.Db;
using SolidBrew.Api.Http;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using SolidBrew.Core.Paths;

namespace SolidBrew.Api.Services;

/// <summary>
/// Выгрузка комплекта в ZIP потоком: исходные файлы по относительным путям, производные — рядом с исходным файлом версии,
/// <c>manifest.json</c> в корне. Архив не буферизуется в памяти.
/// </summary>
public sealed class ExportService(NpgsqlDataSource db, IBlobStore blobs, ILogger<ExportService> logger)
{
    private static readonly JsonSerializerOptions ManifestJson = new(PdmJson.Options) { WriteIndented = true };

    public async Task<IResult> ExportAsync(HttpContext context, Guid id, int version, bool includeDerived, CurrentUser user)
    {
        var ct = context.RequestAborted;
        List<(ExportManifestFile File, DateTimeOffset At)> files;
        string headName;
        await using (var connection = await db.OpenConnectionAsync(ct))
        {
            if (!await KitQueries.VersionExistsAsync(connection, id, version))
                return ApiErrors.NotFound($"Версия v{version} документа {id} не найдена. Обновите карточку документа.");

            var (kit, versionIds) = await KitQueries.GetKitAsync(connection, id, version);
            var times = (await connection.QueryAsync<(long Id, DateTime At)>(
                "SELECT id, at FROM versions WHERE id = ANY(@ids)", new { ids = versionIds.ToArray() })).ToDictionary(r => r.Id, r => DbText.Time(r.At));

            files = [];
            for (var i = 0; i < kit.Items.Count; i++)
            {
                var item = kit.Items[i];
                files.Add((new ExportManifestFile { RelPath = item.RelPath, DocumentId = item.DocumentId, Version = item.Version, Sha256 = item.Sha256, Size = item.Size }, times[versionIds[i]]));
            }
            if (includeDerived)
            {
                var derived = await connection.QueryAsync<DocumentQueries.DerivedRow>(
                    "SELECT version_id, file_name, sha256, size FROM derived_files WHERE version_id = ANY(@ids) ORDER BY file_name",
                    new { ids = versionIds.ToArray() });
                var byVersion = versionIds.Select((vid, i) => (vid, item: kit.Items[i])).ToDictionary(p => p.vid, p => p.item);
                foreach (var d in derived)
                {
                    var owner = byVersion[d.VersionId];
                    var slash = owner.RelPath.LastIndexOf(RelPath.Separator);
                    var path = slash < 0 ? d.FileName : owner.RelPath[..(slash + 1)] + d.FileName;
                    files.Add((new ExportManifestFile { RelPath = path, DocumentId = owner.DocumentId, Version = owner.Version, Sha256 = d.Sha256, Size = d.Size, Derived = true }, times[d.VersionId]));
                }
            }

            // Один путь — один файл. Одинаковое содержимое по одному пути выгружается один раз.
            var conflicts = new List<ProblemDto>();
            var unique = new List<(ExportManifestFile File, DateTimeOffset At)>();
            var seen = new Dictionary<string, ExportManifestFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in files)
            {
                if (string.Equals(entry.File.RelPath, ExportManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                    (seen.TryGetValue(entry.File.RelPath, out var previous) && previous.Sha256 != entry.File.Sha256))
                {
                    var other = seen.GetValueOrDefault(entry.File.RelPath);
                    conflicts.Add(new ProblemDto
                    {
                        Code = ProblemCodes.DuplicatePath,
                        DocumentId = entry.File.DocumentId,
                        RelPath = entry.File.RelPath,
                        Message = other is null
                            ? $"Файл «{entry.File.RelPath}» совпадает по имени с перечнем выгрузки."
                            : $"По пути «{entry.File.RelPath}» в комплект входят разные файлы (v{other.Version} и v{entry.File.Version}). " +
                              "Обычно это значит, что сборки ссылаются на разные версии одной детали — обновите зависимость и сдайте сборку.",
                    });
                    continue;
                }
                if (seen.TryAdd(entry.File.RelPath, entry.File)) unique.Add(entry);
            }
            if (conflicts.Count > 0)
                return ApiErrors.Error(StatusCodes.Status409Conflict,
                    "Комплект нельзя выгрузить в ZIP: несколько разных файлов претендуют на один путь. " + conflicts[0].Message, conflicts);
            files = unique;

            var missing = await blobs.FindMissingAsync(files.Select(f => f.File.Sha256), ct);
            if (missing.Count > 0)
            {
                logger.LogError("Выгрузка {Id} v{Version}: в архиве нет объектов {Missing}", id, version, string.Join(", ", missing));
                return Results.Json(new ErrorResponse
                {
                    Message = $"В архиве сервера нет содержимого {missing.Count} файл(ов) комплекта. Сообщите администратору PDM — нужна проверка резервной копии.",
                }, statusCode: StatusCodes.Status500InternalServerError);
            }

            headName = kit.Items[0].Designation is { Length: > 0 } designation ? designation : Path.GetFileNameWithoutExtension(RelPath.FileName(kit.Items[0].RelPath));
            await Journal.WriteAsync(connection, user.Id, JournalActions.Export, id, version,
                new { includeDerived, files = files.Count, bytes = files.Sum(f => f.File.Size) });
        }

        var manifest = new ExportManifest
        {
            HeadDocumentId = id,
            HeadVersion = version,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = user.Login,
            Files = files.Select(f => f.File).ToList(),
        };

        var response = context.Response;
        response.ContentType = "application/zip";
        var fileName = $"{headName} v{version}.zip";
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = AsciiFallback(fileName),
            FileNameStar = fileName,
        }.ToString();

        var body = new AsyncWriteStream(response.Body);
        await using (var zip = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, ct))
        {
            var manifestEntry = zip.CreateEntry(ExportManifest.FileName, CompressionLevel.Optimal);
            await using (var stream = await manifestEntry.OpenAsync(ct))
                await JsonSerializer.SerializeAsync(stream, manifest, ManifestJson, ct);
            await body.FlushAsync(ct);

            foreach (var (file, at) in files)
            {
                var entry = zip.CreateEntry(file.RelPath, CompressionLevel.Fastest);
                entry.LastWriteTime = at;
                await using var blob = await blobs.OpenReadAsync(file.Sha256, ct)
                    ?? throw new InvalidOperationException($"Объект {file.Sha256} исчез из архива во время выгрузки.");
                await using var target = await entry.OpenAsync(ct);
                await blob.Stream.CopyToAsync(target, 1 << 20, ct);
                await target.DisposeAsync();
                await body.FlushAsync(ct);
            }
        }
        await body.FlushAsync(ct);
        return Results.Empty;
    }

    private static string AsciiFallback(string name)
    {
        var chars = name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray();
        return new string(chars);
    }
}
