using System;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Core.Api
{
    /// <summary>
    /// Ошибка обращения к серверу PDM. <see cref="Exception.Message"/> — понятный пользователю текст на русском.
    /// </summary>
    public sealed class PdmApiException : Exception
    {
        public PdmApiException(int statusCode, string message, ErrorResponse? error = null, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            Error = error;
        }

        /// <summary>HTTP-код ответа; 0 — нет связи с сервером.</summary>
        public int StatusCode { get; }

        /// <summary>Разобранное тело ошибки, если сервер его прислал.</summary>
        public ErrorResponse? Error { get; }

        /// <summary>Ошибка сети: сервер не ответил.</summary>
        public bool IsNetworkError => StatusCode == 0;

        /// <summary>Стандартное сообщение по коду ответа, если сервер не прислал своё.</summary>
        public static string DefaultMessage(int statusCode)
        {
            switch (statusCode)
            {
                case 0: return "Нет связи с сервером PDM. Проверьте сеть и адрес сервера.";
                case 400: return "Сервер отклонил запрос: неверные данные.";
                case 401: return "Требуется вход: сессия не начата или истекла. Войдите заново.";
                case 403: return "Недостаточно прав для этой операции.";
                case 404: return "Объект не найден на сервере.";
                case 409: return "Конфликт: данные на сервере изменились. Обновите и повторите.";
                case 413: return "Файл слишком большой для сервера.";
            }
            if (statusCode >= 500) return $"Сервер недоступен или вернул ошибку (код {statusCode}). Повторите позже или обратитесь к администратору.";
            return $"Сервер вернул ошибку (код {statusCode}).";
        }
    }
}
