using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Blobs;
using SolidBrew.Api.Http;
using SolidBrew.Api.Services;
using SolidBrew.Core;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;

namespace SolidBrew.Api.Endpoints;

/// <summary>Архив содержимого: какие объекты отсутствуют, загрузка с проверкой хеша, скачивание. Плюс сдача.</summary>
internal static class ObjectEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ApiRoutes.Templates.MissingObjects, async (MissingObjectsRequest request, IBlobStore blobs, CancellationToken ct) =>
        {
            var hashes = request.Hashes ?? [];
            if (hashes.Count > 100_000) return ApiErrors.Validation("Слишком много хешей в одном запросе.");
            if (hashes.FirstOrDefault(h => !Sha256Hex.IsValid(h)) is { } bad)
                return ApiErrors.Validation($"Неверный хеш содержимого «{bad}»: нужно 64 шестнадцатеричных символа в нижнем регистре.");
            return Results.Ok(new MissingObjectsResponse { Missing = await blobs.FindMissingAsync(hashes, ct) });
        });

        app.MapPut(ApiRoutes.Templates.Object, UploadAsync);

        app.MapGet(ApiRoutes.Templates.Object, async (string sha, IBlobStore blobs, CancellationToken ct) =>
        {
            if (!Sha256Hex.IsValid(sha)) return ApiErrors.Validation($"Неверный хеш содержимого «{sha}».");
            var blob = await blobs.OpenReadAsync(sha, ct);
            if (blob is null) return ApiErrors.NotFound($"Объекта {sha} нет в архиве сервера.");
            return Results.Stream(blob.Stream, "application/octet-stream");
        });

        app.MapPost(ApiRoutes.Templates.Checkins, (CheckinRequest request, CurrentUser user, CheckinService service, CancellationToken ct) =>
            service.CheckinAsync(request, user, ct));
    }

    private static async Task<IResult> UploadAsync(string sha, HttpContext context, CurrentUser user, IBlobStore blobs, PdmOptions options, ILoggerFactory loggers)
    {
        var ct = context.RequestAborted;
        if (user.RequireWriter("загрузка файлов") is { } denied) return denied;
        if (!Sha256Hex.IsValid(sha))
            return ApiErrors.Validation($"Неверный хеш содержимого «{sha}»: нужно 64 шестнадцатеричных символа в нижнем регистре.");

        var limit = options.MaxObjectBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = limit;
        if (context.Request.ContentLength > limit)
            return TooLarge(limit);

        if (await blobs.ExistsAsync(sha, ct))
        {
            // Объект уже в архиве: тело дочитываем без записи, чтобы соединение осталось пригодным.
            await context.Request.Body.CopyToAsync(Stream.Null, ct);
            return Results.NoContent();
        }

        Directory.CreateDirectory(options.TempDir);
        var tempPath = Path.Combine(options.TempDir, $"pdm-upload-{Guid.NewGuid():N}.tmp");
        await using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long total = 0;
        try
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > limit) return TooLarge(limit);
                hash.AppendData(buffer, 0, read);
                await temp.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(limit);
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (actual != sha)
            return ApiErrors.Unprocessable(ProblemCodes.HashMismatch,
                $"Файл повреждён при передаче: хеш полученного содержимого ({actual[..12]}…) не совпадает с заявленным ({sha[..12]}…). " +
                "Повторите сдачу; если ошибка повторяется — проверьте, не изменяется ли файл во время отправки (закройте его в CAD).");

        await temp.FlushAsync(ct);
        temp.Position = 0;
        await blobs.PutAsync(sha, temp, total, ct);
        loggers.CreateLogger(typeof(ObjectEndpoints)).LogInformation("Объект {Sha} ({Bytes} байт) загружен пользователем {Login}", sha, total, user.Login);
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private static IResult TooLarge(long limit) =>
        ApiErrors.Problem(StatusCodes.Status413PayloadTooLarge, ProblemCodes.Validation,
            $"Файл больше допустимого на сервере размера ({limit / (1024 * 1024)} МиБ). Обратитесь к администратору PDM (параметр PDM_MAX_OBJECT_BYTES).");
}
