using Dapper;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using SolidBrew.Api;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Blobs;
using SolidBrew.Api.Db;
using SolidBrew.Api.Endpoints;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;

DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

// Общий предел тела — под самый большой объект архива; для JSON-запросов он понижается в промежуточном слое ниже.
builder.WebHost.ConfigureKestrel((context, kestrel) =>
    kestrel.Limits.MaxRequestBodySize = PdmOptions.FromConfiguration(context.Configuration).MaxObjectBytes);

builder.Services.ConfigureHttpJsonOptions(o => PdmJson.Apply(o.SerializerOptions));
builder.Services.AddSingleton(sp => PdmOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => NpgsqlDataSource.Create(Migrator.AppConnectionString(sp.GetRequiredService<PdmOptions>())));
builder.Services.AddSingleton<IBlobStore>(sp =>
{
    var options = sp.GetRequiredService<PdmOptions>();
    return options.Storage == "fs" ? new FileSystemBlobStore(options.BlobDir!) : new S3BlobStore(options);
});
builder.Services.AddSingleton<CheckinHooks>();
builder.Services.AddSingleton<CheckinService>();
builder.Services.AddSingleton<ExportService>();

var app = builder.Build();

// Подготовка БД: миграции под владельцем, затем начальные данные под ролью приложения.
{
    var options = app.Services.GetRequiredService<PdmOptions>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SolidBrew.Startup");
    await Migrator.MigrateAsync(options, logger);
    await Seeder.SeedAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), options, logger);
}

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException bad)
    {
        await ApiErrors.Error(bad.StatusCode, ApiErrors.DefaultMessage(bad.StatusCode)).ExecuteAsync(context);
        return;
    }
    await Results.Json(new ErrorResponse
    {
        Message = "Внутренняя ошибка сервера PDM. Операция не выполнена. Повторите позже; если ошибка повторяется — сообщите администратору (время ошибки есть в журнале сервера).",
    }, statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
}));

// Ответы 4xx без тела (маршрут не найден, неверный JSON и т. п.) — тоже в формате ErrorResponse.
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.StatusCode is >= 400 and < 500 && !response.HasStarted && response.ContentLength is null && response.ContentType is null)
        await ApiErrors.Error(response.StatusCode, ApiErrors.DefaultMessage(response.StatusCode)).ExecuteAsync(statusContext.HttpContext);
});

app.Use(async (context, next) =>
{
    var options = context.RequestServices.GetRequiredService<PdmOptions>();
    var isObjectUpload = HttpMethods.IsPut(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/objects");
    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        feature.MaxRequestBodySize = isObjectUpload ? options.MaxObjectBytes : options.MaxJsonBytes;
    await next(context);
});

app.UseMiddleware<AuthMiddleware>();

app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using var connection = await db.OpenConnectionAsync(ct);
    var schema = await connection.ExecuteScalarAsync<int>("SELECT coalesce(max(version), 0) FROM schema_version");
    return Results.Ok(new { status = "ok", postgres = "ok", schema });
});

AuthEndpoints.Map(app);
DocumentEndpoints.Map(app);
LockEndpoints.Map(app);
ObjectEndpoints.Map(app);
AdminEndpoints.Map(app);

await app.RunAsync();

/// <summary>Точка входа; partial — для WebApplicationFactory в тестах.</summary>
public partial class Program;
