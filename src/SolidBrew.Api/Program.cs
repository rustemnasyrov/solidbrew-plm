using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = CadZip.MaxArchiveBytes);
builder.Services.AddProblemDetails();
var token = Required("PDM_API_TOKEN");
if (token.Length < 32) throw new InvalidOperationException("PDM_API_TOKEN must have at least 32 characters.");
var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
var dbConfig = new NpgsqlConnectionStringBuilder
{
    Host = Environment.GetEnvironmentVariable("PDM_DB_HOST") ?? "localhost",
    Port = int.TryParse(Environment.GetEnvironmentVariable("PDM_DB_PORT"), out var port) ? port : 5432,
    Database = "pdm", Username = "pdm", Password = Required("PDM_DB_PASSWORD")
};
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(dbConfig.ConnectionString));
builder.Services.AddSingleton<Catalog>();
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(Required("PDM_S3_ACCESS_KEY"), Required("PDM_S3_SECRET_KEY")),
    new AmazonS3Config { ServiceURL = Required("PDM_S3_ENDPOINT"), ForcePathStyle = true, AuthenticationRegion = "us-east-1",
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED }));

var app = builder.Build();
const string bucket = "pdm-snapshots";
await app.Services.GetRequiredService<Catalog>().InitializeAsync();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
    }
    await next(context);
});

app.MapGet("/healthz", async (Catalog catalog, CancellationToken ct) =>
{
    await catalog.CheckAsync(ct);
    return Results.Ok(new { status = "ok", postgres = "ok" });
});
app.MapGet("/api/items", async (Catalog catalog, CancellationToken ct) => Results.Ok(await catalog.ItemsAsync(ct)));
app.MapGet("/api/items/{id:guid}", async (Guid id, Catalog catalog, CancellationToken ct) =>
    await catalog.GetItemAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
app.MapPost("/api/items", async (NewItem body, Catalog catalog, CancellationToken ct) =>
{
    var designation = body.Designation?.Trim().Normalize();
    var name = body.Name?.Trim().Normalize();
    var kind = body.Kind?.Trim().ToLowerInvariant();
    if (designation is not { Length: > 0 and <= 120 } || name is not { Length: > 0 and <= 240 } ||
        designation.Any(char.IsControl) || name.Any(char.IsControl) ||
        kind is not ("part" or "assembly" or "complex" or "kit" or "undetermined"))
        return Results.BadRequest(new { error = "Supply a designation, name, and a valid item kind." });
    var result = new Item(Guid.NewGuid(), designation, name, kind);
    return await catalog.CreateItemAsync(result, ct)
        ? Results.Created($"/api/items/{result.Id}", result)
        : Results.Conflict(new { error = "Designation already exists." });
});

app.MapGet("/api/items/{id:guid}/snapshots", async (Guid id, Catalog catalog, CancellationToken ct) =>
    await catalog.GetItemAsync(id, ct) is null ? Results.NotFound() : Results.Ok(await catalog.SnapshotsAsync(id, ct)));
app.MapGet("/api/items/{id:guid}/snapshots/{version:int}", async (Guid id, int version, Catalog catalog, CancellationToken ct) =>
    await catalog.GetSnapshotAsync(id, version, ct) is { } snapshot ? Results.Ok(snapshot.PublicDetail()) : Results.NotFound());
app.MapGet("/api/items/{id:guid}/snapshots/{version:int}/archive", async (Guid id, int version, Catalog catalog, IAmazonS3 s3, HttpContext context, CancellationToken ct) =>
{
    var snapshot = await catalog.GetSnapshotAsync(id, version, ct);
    if (snapshot is null) return Results.NotFound();
    var response = await s3.GetObjectAsync(bucket, snapshot.ObjectKey, ct);
    context.Response.RegisterForDispose(response);
    context.Response.ContentLength = snapshot.Info.ArchiveBytes;
    return Results.Stream(response.ResponseStream, "application/zip", $"{id:N}-v{version}.zip");
});
app.MapPost("/api/items/{id:guid}/snapshots", async (Guid id, string assembly, string drawing, HttpRequest request, Catalog catalog, IAmazonS3 s3, CancellationToken ct) =>
{
    if (await catalog.GetItemAsync(id, ct) is null) return Results.NotFound();
    if (!string.Equals(request.ContentType?.Split(';', 2)[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (request.ContentLength > CadZip.MaxArchiveBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    await using var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"pdm-{Guid.NewGuid():N}.zip"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    IReadOnlyList<CadFile> files;
    try
    {
        await CadZip.CopyBoundedAsync(request.Body, temp, CadZip.MaxArchiveBytes, hash: null, ct);
        temp.Position = 0;
        files = await CadZip.ReadAsync(temp, assembly, drawing, ct);
    }
    catch (ArchiveLimitException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
    catch (NotSupportedException) { return Results.BadRequest(new { error = "Unsupported ZIP data." }); }
    temp.Position = 0;
    var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(temp, ct));
    var bytes = temp.Length;
    temp.Position = 0;
    var key = $"snapshots/{id:N}/{Guid.NewGuid():N}.zip";
    await s3.PutObjectAsync(new PutObjectRequest
    {
        BucketName = bucket, Key = key, InputStream = temp, ContentType = "application/zip",
        AutoCloseStream = false, UseChunkEncoding = false
    }, ct);
    var snapshot = await catalog.AddSnapshotAsync(id, assembly, drawing, bytes, sha256, key, files, ct);
    return Results.Created($"/api/items/{id}/snapshots/{snapshot.Version}", snapshot.PublicDetail());
});
await app.RunAsync();

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Set {name}.");

internal sealed record NewItem(string? Designation, string? Name, string? Kind);
