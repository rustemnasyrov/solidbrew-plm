using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Core.Api
{
    /// <summary>
    /// Операции HTTP API SolidBrew PDM. Все ошибки (ответы не 2xx, нет связи) приходят как <see cref="PdmApiException"/>.
    /// </summary>
    public interface IPdmApi
    {
        /// <summary>Токен сессии. Задаётся <see cref="LoginAsync"/> или снаружи (сохранённая сессия); null — не вошли.</summary>
        string? Token { get; set; }

        // --- Вход и текущий пользователь ---

        /// <summary>POST /api/auth/login. Запоминает полученный токен в <see cref="Token"/>.</summary>
        Task<LoginResponse> LoginAsync(string login, string password, CancellationToken ct = default);

        /// <summary>POST /api/auth/logout. Сбрасывает <see cref="Token"/>.</summary>
        Task LogoutAsync(CancellationToken ct = default);

        /// <summary>GET /api/me.</summary>
        Task<UserDto> GetMeAsync(CancellationToken ct = default);

        /// <summary>GET /api/me/notifications.</summary>
        Task<List<NotificationDto>> GetNotificationsAsync(CancellationToken ct = default);

        /// <summary>POST /api/me/notifications/seen (без тела) — отметить все уведомления прочитанными.</summary>
        Task MarkNotificationsSeenAsync(CancellationToken ct = default);

        // --- Проекты ---

        /// <summary>GET /api/projects.</summary>
        Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default);

        /// <summary>POST /api/projects.</summary>
        Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default);

        // --- Документы ---

        /// <summary>
        /// GET /api/documents?q=&amp;projectId=&amp;author=&amp;includeUnused=&amp;limit=&amp;roots=.
        /// <paramref name="roots"/> — только корневые изделия (активные сборки, не входящие в последние версии других документов, кроме чертежей).
        /// </summary>
        Task<List<DocumentSummaryDto>> SearchDocumentsAsync(string? q = null, Guid? projectId = null, string? author = null,
            bool includeUnused = false, int? limit = null, bool roots = false, CancellationToken ct = default);

        /// <summary>GET /api/documents/{id}.</summary>
        Task<DocumentDetailDto> GetDocumentAsync(Guid id, CancellationToken ct = default);

        /// <summary>
        /// GET /api/documents/{id}/versions/{version}/kit?includeDrawings=.
        /// <paramref name="includeDrawings"/> — добавить последние версии чертежей, ссылающихся на документы комплекта
        /// (<see cref="KitItemDto.IsAttachedDrawing"/>), и их недостающие зависимости.
        /// </summary>
        Task<KitDto> GetKitAsync(Guid id, int version, bool includeDrawings = false, CancellationToken ct = default);

        /// <summary>GET /api/documents/{id}/versions/{version}/where-used.</summary>
        Task<WhereUsedDto> GetWhereUsedAsync(Guid id, int version, CancellationToken ct = default);

        /// <summary>GET /api/documents/{id}/versions/{version}/export?derived=… — ZIP пишется в <paramref name="destination"/>.</summary>
        Task ExportAsync(Guid id, int version, bool includeDerived, Stream destination, CancellationToken ct = default);

        /// <summary>GET /api/documents/{id}/journal.</summary>
        Task<List<JournalEntryDto>> GetDocumentJournalAsync(Guid id, CancellationToken ct = default);

        /// <summary>POST /api/documents/{id}/lock — взять в работу. Возвращает блокировку.</summary>
        Task<LockDto> LockAsync(Guid id, CancellationToken ct = default);

        /// <summary>DELETE /api/documents/{id}/lock — отменить взятие (своя блокировка).</summary>
        Task UnlockAsync(Guid id, CancellationToken ct = default);

        /// <summary>POST /api/documents/{id}/unused — пометить «не используется» или вернуть. Возвращает обновлённый документ.</summary>
        Task<DocumentSummaryDto> MarkUnusedAsync(Guid id, bool unused, CancellationToken ct = default);

        // --- Архив содержимого ---

        /// <summary>POST /api/objects/missing — какие из хешей сервер ещё не хранит.</summary>
        Task<List<string>> GetMissingObjectsAsync(IEnumerable<string> hashes, CancellationToken ct = default);

        /// <summary>PUT /api/objects/{sha256} — потоковая загрузка содержимого.</summary>
        Task UploadObjectAsync(string sha256, Stream content, long size, IProgress<long>? progress, CancellationToken ct = default);

        /// <summary>GET /api/objects/{sha256} — потоковое скачивание в <paramref name="destination"/>.</summary>
        Task DownloadObjectAsync(string sha256, Stream destination, IProgress<long>? progress, CancellationToken ct = default);

        // --- Сдача ---

        /// <summary>POST /api/checkins.</summary>
        Task<CheckinResponse> CheckinAsync(CheckinRequest request, CancellationToken ct = default);

        // --- Администрирование ---

        /// <summary>GET /api/admin/users.</summary>
        Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default);

        /// <summary>POST /api/admin/users.</summary>
        Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);

        /// <summary>PUT /api/admin/users/{id}.</summary>
        Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default);

        /// <summary>GET /api/admin/locks.</summary>
        Task<List<LockDto>> GetLocksAsync(CancellationToken ct = default);

        /// <summary>DELETE /api/admin/locks/{documentId} — принудительно снять чужую блокировку.</summary>
        Task ForceUnlockAsync(Guid documentId, CancellationToken ct = default);

        /// <summary>GET /api/admin/settings. Администратор получает все настройки, остальные — только ключи «cad.*» (рабочие версии CAD).</summary>
        Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default);

        /// <summary>PUT /api/admin/settings. Возвращает сохранённые настройки.</summary>
        Task<SettingsDto> UpdateSettingsAsync(SettingsDto settings, CancellationToken ct = default);

        /// <summary>GET /api/admin/journal?limit=.</summary>
        Task<List<JournalEntryDto>> GetJournalAsync(int? limit = null, CancellationToken ct = default);
    }
}
