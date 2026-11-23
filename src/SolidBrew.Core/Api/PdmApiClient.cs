using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;

namespace SolidBrew.Core.Api
{
    /// <summary>
    /// Клиент HTTP API поверх <see cref="HttpClient"/>. Адрес сервера задаётся снаружи через
    /// <see cref="HttpClient.BaseAddress"/> (если сервер не в корне, адрес должен оканчиваться на «/»).
    /// Большие файлы передаются потоком, без буферизации в памяти.
    /// </summary>
    public sealed class PdmApiClient : IPdmApi
    {
        private const int CopyBufferSize = 1024 * 1024;
        private static readonly MediaTypeHeaderValue JsonMediaType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        private static readonly MediaTypeHeaderValue OctetStreamMediaType = new MediaTypeHeaderValue("application/octet-stream");

        private readonly HttpClient _http;

        public PdmApiClient(HttpClient http, string? token = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            Token = token;
        }

        public string? Token { get; set; }

        // --- Вход и текущий пользователь ---

        public async Task<LoginResponse> LoginAsync(string login, string password, CancellationToken ct = default)
        {
            var response = await SendJsonAsync<LoginResponse>(HttpMethod.Post, ApiRoutes.Login,
                new LoginRequest { Login = login, Password = password }, ct).ConfigureAwait(false);
            Token = response.Token;
            return response;
        }

        public async Task LogoutAsync(CancellationToken ct = default)
        {
            try
            {
                await SendNoResultAsync(HttpMethod.Post, ApiRoutes.Logout, null, ct).ConfigureAwait(false);
            }
            finally
            {
                Token = null;
            }
        }

        public Task<UserDto> GetMeAsync(CancellationToken ct = default) =>
            SendJsonAsync<UserDto>(HttpMethod.Get, ApiRoutes.Me, null, ct);

        public Task<List<NotificationDto>> GetNotificationsAsync(CancellationToken ct = default) =>
            SendJsonAsync<List<NotificationDto>>(HttpMethod.Get, ApiRoutes.Notifications, null, ct);

        public Task MarkNotificationsSeenAsync(CancellationToken ct = default) =>
            SendNoResultAsync(HttpMethod.Post, ApiRoutes.NotificationsSeen, null, ct);

        // --- Проекты ---

        public Task<List<ProjectDto>> GetProjectsAsync(CancellationToken ct = default) =>
            SendJsonAsync<List<ProjectDto>>(HttpMethod.Get, ApiRoutes.Projects, null, ct);

        public Task<ProjectDto> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default) =>
            SendJsonAsync<ProjectDto>(HttpMethod.Post, ApiRoutes.Projects, Required(request, nameof(request)), ct);

        // --- Документы ---

        public Task<List<DocumentSummaryDto>> SearchDocumentsAsync(string? q = null, Guid? projectId = null, string? author = null,
            bool includeUnused = false, int? limit = null, CancellationToken ct = default) =>
            SendJsonAsync<List<DocumentSummaryDto>>(HttpMethod.Get,
                ApiRoutes.SearchDocuments(q, projectId, author, includeUnused, limit), null, ct);

        public Task<DocumentDetailDto> GetDocumentAsync(Guid id, CancellationToken ct = default) =>
            SendJsonAsync<DocumentDetailDto>(HttpMethod.Get, ApiRoutes.Document(id), null, ct);

        public Task<KitDto> GetKitAsync(Guid id, int version, CancellationToken ct = default) =>
            SendJsonAsync<KitDto>(HttpMethod.Get, ApiRoutes.Kit(id, version), null, ct);

        public Task<WhereUsedDto> GetWhereUsedAsync(Guid id, int version, CancellationToken ct = default) =>
            SendJsonAsync<WhereUsedDto>(HttpMethod.Get, ApiRoutes.WhereUsed(id, version), null, ct);

        public Task ExportAsync(Guid id, int version, bool includeDerived, Stream destination, CancellationToken ct = default) =>
            DownloadAsync(ApiRoutes.Export(id, version, includeDerived), Required(destination, nameof(destination)), null, ct);

        public Task<List<JournalEntryDto>> GetDocumentJournalAsync(Guid id, CancellationToken ct = default) =>
            SendJsonAsync<List<JournalEntryDto>>(HttpMethod.Get, ApiRoutes.DocumentJournal(id), null, ct);

        public Task<LockDto> LockAsync(Guid id, CancellationToken ct = default) =>
            SendJsonAsync<LockDto>(HttpMethod.Post, ApiRoutes.DocumentLock(id), null, ct);

        public Task UnlockAsync(Guid id, CancellationToken ct = default) =>
            SendNoResultAsync(HttpMethod.Delete, ApiRoutes.DocumentLock(id), null, ct);

        public Task<DocumentSummaryDto> MarkUnusedAsync(Guid id, bool unused, CancellationToken ct = default) =>
            SendJsonAsync<DocumentSummaryDto>(HttpMethod.Post, ApiRoutes.DocumentUnused(id), new MarkUnusedRequest { Unused = unused }, ct);

        // --- Архив содержимого ---

        public async Task<List<string>> GetMissingObjectsAsync(IEnumerable<string> hashes, CancellationToken ct = default)
        {
            var request = new MissingObjectsRequest { Hashes = Required(hashes, nameof(hashes)).ToList() };
            var response = await SendJsonAsync<MissingObjectsResponse>(HttpMethod.Post, ApiRoutes.MissingObjects, request, ct).ConfigureAwait(false);
            return response.Missing;
        }

        public async Task UploadObjectAsync(string sha256, Stream content, long size, IProgress<long>? progress, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sha256)) throw new ArgumentException("Не задан хеш содержимого.", nameof(sha256));
            Required(content, nameof(content));
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

            // Обёртка не закрывает поток вызывающего и сообщает о ходе передачи.
            using (var body = new ProgressReadStream(content, progress))
            using (var streamContent = new StreamContent(body, CopyBufferSize))
            using (var request = CreateRequest(HttpMethod.Put, ApiRoutes.Object(sha256)))
            {
                streamContent.Headers.ContentType = OctetStreamMediaType;
                streamContent.Headers.ContentLength = size;
                request.Content = streamContent;
                using (await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                }
            }
        }

        public Task DownloadObjectAsync(string sha256, Stream destination, IProgress<long>? progress, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sha256)) throw new ArgumentException("Не задан хеш содержимого.", nameof(sha256));
            return DownloadAsync(ApiRoutes.Object(sha256), Required(destination, nameof(destination)), progress, ct);
        }

        // --- Сдача ---

        public Task<CheckinResponse> CheckinAsync(CheckinRequest request, CancellationToken ct = default) =>
            SendJsonAsync<CheckinResponse>(HttpMethod.Post, ApiRoutes.Checkins, Required(request, nameof(request)), ct);

        // --- Администрирование ---

        public Task<List<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
            SendJsonAsync<List<UserDto>>(HttpMethod.Get, ApiRoutes.AdminUsers, null, ct);

        public Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default) =>
            SendJsonAsync<UserDto>(HttpMethod.Post, ApiRoutes.AdminUsers, Required(request, nameof(request)), ct);

        public Task<UserDto> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) =>
            SendJsonAsync<UserDto>(HttpMethod.Put, ApiRoutes.AdminUser(id), Required(request, nameof(request)), ct);

        public Task<List<LockDto>> GetLocksAsync(CancellationToken ct = default) =>
            SendJsonAsync<List<LockDto>>(HttpMethod.Get, ApiRoutes.AdminLocks, null, ct);

        public Task ForceUnlockAsync(Guid documentId, CancellationToken ct = default) =>
            SendNoResultAsync(HttpMethod.Delete, ApiRoutes.AdminLock(documentId), null, ct);

        public Task<SettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
            SendJsonAsync<SettingsDto>(HttpMethod.Get, ApiRoutes.AdminSettings, null, ct);

        public Task<SettingsDto> UpdateSettingsAsync(SettingsDto settings, CancellationToken ct = default) =>
            SendJsonAsync<SettingsDto>(HttpMethod.Put, ApiRoutes.AdminSettings, Required(settings, nameof(settings)), ct);

        public Task<List<JournalEntryDto>> GetJournalAsync(int? limit = null, CancellationToken ct = default) =>
            SendJsonAsync<List<JournalEntryDto>>(HttpMethod.Get, ApiRoutes.AdminJournal(limit), null, ct);

        // --- Внутреннее ---

        private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) where T : class
        {
            using (var request = CreateRequest(method, path, body))
            using (var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                T? result;
                try
                {
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        result = await JsonSerializer.DeserializeAsync<T>(stream, PdmJson.Options, ct).ConfigureAwait(false);
                    }
                }
                catch (JsonException ex)
                {
                    throw new PdmApiException((int)response.StatusCode, "Сервер вернул ответ в неожиданном формате. Возможно, версии клиента и сервера не совпадают.", null, ex);
                }
                catch (HttpRequestException ex)
                {
                    throw NetworkError(ex);
                }
                catch (IOException ex)
                {
                    throw NetworkError(ex);
                }

                if (result == null)
                    throw new PdmApiException((int)response.StatusCode, "Сервер вернул пустой ответ.");
                return result;
            }
        }

        private async Task SendNoResultAsync(HttpMethod method, string path, object? body, CancellationToken ct)
        {
            using (var request = CreateRequest(method, path, body))
            using (await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
            }
        }

        private async Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            using (var request = CreateRequest(HttpMethod.Get, path))
            using (var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                Stream source;
                try
                {
                    source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw NetworkError(ex);
                }

                using (source)
                {
                    var buffer = new byte[CopyBufferSize];
                    long total = 0;
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await source.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                        }
                        catch (HttpRequestException ex)
                        {
                            throw NetworkError(ex);
                        }
                        catch (IOException ex)
                        {
                            throw NetworkError(ex);
                        }

                        if (read == 0) break;
                        // Ошибки записи (например, нет места на диске) не маскируем под сетевые.
                        await destination.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                        total += read;
                        progress?.Report(total);
                    }
                }
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body = null)
        {
            // Путь без ведущего «/», чтобы сервер мог стоять не в корне (BaseAddress вида https://host/pdm/).
            var request = new HttpRequestMessage(method, new Uri(path.TrimStart('/'), UriKind.Relative));
            if (!string.IsNullOrEmpty(Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (body != null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), PdmJson.Options);
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = JsonMediaType;
                request.Content = content;
            }
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
        {
            if (_http.BaseAddress == null)
                throw new InvalidOperationException("Не задан адрес сервера PDM (HttpClient.BaseAddress).");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw NetworkError(ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // Отмена не по нашему токену — это тайм-аут HttpClient.
                throw new PdmApiException(0, "Нет связи с сервером PDM: истекло время ожидания ответа.", null, ex);
            }

            if (response.IsSuccessStatusCode)
                return response;

            using (response)
            {
                throw await CreateErrorAsync(response).ConfigureAwait(false);
            }
        }

        private static async Task<PdmApiException> CreateErrorAsync(HttpResponseMessage response)
        {
            var status = (int)response.StatusCode;
            ErrorResponse? error = null;
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length > 0)
                    error = JsonSerializer.Deserialize<ErrorResponse>(bytes, PdmJson.Options);
            }
            catch (JsonException)
            {
                // Тело не в формате ErrorResponse (HTML прокси и т. п.) — используем сообщение по коду.
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }

            string message;
            if (error != null && !string.IsNullOrWhiteSpace(error.Message))
                message = error.Message;
            else if (error?.Lock != null)
                message = $"Документ взят в работу пользователем {LockOwner(error.Lock)}.";
            else
                message = PdmApiException.DefaultMessage(status);

            return new PdmApiException(status, message, error);
        }

        private static string LockOwner(LockDto lockDto) =>
            string.IsNullOrWhiteSpace(lockDto.UserDisplayName) ? lockDto.UserLogin : lockDto.UserDisplayName;

        private static PdmApiException NetworkError(Exception ex) =>
            new PdmApiException(0, $"Нет связи с сервером PDM: {ex.Message}", null, ex);

        private static T Required<T>(T value, string name) where T : class =>
            value ?? throw new ArgumentNullException(name);

        /// <summary>Поток только для чтения: считает прочитанные байты и не закрывает исходный поток.</summary>
        private sealed class ProgressReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly IProgress<long>? _progress;
            private long _total;

            public ProgressReadStream(Stream inner, IProgress<long>? progress)
            {
                _inner = inner;
                _progress = progress;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _total; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Report(_inner.Read(buffer, offset, count));

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Report(await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

            private int Report(int read)
            {
                if (read > 0)
                {
                    _total += read;
                    _progress?.Report(_total);
                }
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
