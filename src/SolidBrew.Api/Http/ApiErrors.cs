using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Http;

/// <summary>Ответы 4xx в едином формате <see cref="ErrorResponse"/>.</summary>
internal static class ApiErrors
{
    public static IResult Error(int status, string message, IEnumerable<ProblemDto>? problems = null, LockDto? lockDto = null) =>
        Results.Json(new ErrorResponse { Message = message, Problems = problems?.ToList() ?? [], Lock = lockDto }, statusCode: status);

    public static IResult Problem(int status, string code, string message, Guid? documentId = null, string? relPath = null, LockDto? lockDto = null) =>
        Error(status, message, [new ProblemDto { Code = code, Message = message, DocumentId = documentId, RelPath = relPath }], lockDto);

    public static IResult Validation(string message) =>
        Problem(StatusCodes.Status400BadRequest, ProblemCodes.Validation, message);

    public static IResult Unprocessable(string code, string message, Guid? documentId = null, string? relPath = null) =>
        Problem(StatusCodes.Status422UnprocessableEntity, code, message, documentId, relPath);

    public static IResult NotFound(string message) =>
        Problem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, message);

    public static IResult Forbidden(string message, string code = ProblemCodes.Forbidden) =>
        Problem(StatusCodes.Status403Forbidden, code, message);

    public static IResult Conflict(string code, string message, Guid? documentId = null, LockDto? lockDto = null) =>
        Problem(StatusCodes.Status409Conflict, code, message, documentId, lockDto: lockDto);

    public static IResult Unauthorized(string message) =>
        Error(StatusCodes.Status401Unauthorized, message);

    public static IResult DocumentNotFound(Guid id) =>
        NotFound($"Документ {id} не найден. Возможно, ссылка устарела — обновите список документов.");

    public static string LockedByOtherMessage(string relPath, LockDto lockDto) =>
        $"Документ «{relPath}» взят в работу пользователем {Owner(lockDto)} с {Time(lockDto.At)}. " +
        "Дождитесь, пока он сдаст документ или отменит взятие, либо обратитесь к администратору.";

    public static string Owner(LockDto lockDto) =>
        string.IsNullOrWhiteSpace(lockDto.UserDisplayName) ? lockDto.UserLogin : $"{lockDto.UserDisplayName} ({lockDto.UserLogin})";

    /// <summary>Время для текстов сообщений: МСК не навязываем, пишем UTC явно.</summary>
    public static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Стандартное сообщение для ответа без тела (маршрут не найден, неверный JSON и т. п.).</summary>
    public static string DefaultMessage(int status) => status switch
    {
        400 => "Сервер не смог разобрать запрос: неверный формат данных. Обновите клиент PDM или сообщите администратору.",
        401 => "Требуется вход: сессия не начата или истекла. Войдите заново.",
        403 => "Недостаточно прав для этой операции.",
        404 => "Адрес не найден на сервере PDM. Возможно, версии клиента и сервера не совпадают.",
        405 => "Метод запроса не поддерживается для этого адреса. Возможно, версии клиента и сервера не совпадают.",
        413 => "Запрос слишком большой для сервера PDM.",
        415 => "Неподдерживаемый формат тела запроса.",
        _ => $"Сервер отклонил запрос (код {status}).",
    };
}
