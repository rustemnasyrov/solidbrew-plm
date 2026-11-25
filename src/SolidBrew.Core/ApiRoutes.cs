using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolidBrew.Core
{
    /// <summary>
    /// Пути HTTP API. <see cref="Templates"/> — шаблоны маршрутов ASP.NET Core для сервера,
    /// методы этого класса — конкретные адреса для клиента (тоже начинаются с <c>/api</c>).
    /// </summary>
    public static class ApiRoutes
    {
        public const string Prefix = "/api";

        /// <summary>Шаблоны маршрутов в стиле ASP.NET Core.</summary>
        public static class Templates
        {
            public const string Login = "/api/auth/login";                                   // POST
            public const string Logout = "/api/auth/logout";                                 // POST
            public const string Me = "/api/me";                                              // GET
            public const string Notifications = "/api/me/notifications";                     // GET
            public const string NotificationsSeen = "/api/me/notifications/seen";            // POST
            public const string Projects = "/api/projects";                                  // GET, POST
            public const string Documents = "/api/documents";                                // GET (поиск) ?roots=
            public const string Document = "/api/documents/{id:guid}";                       // GET
            public const string Kit = "/api/documents/{id:guid}/versions/{version:int}/kit";               // GET ?includeDrawings=
            public const string WhereUsed = "/api/documents/{id:guid}/versions/{version:int}/where-used";  // GET
            public const string Export = "/api/documents/{id:guid}/versions/{version:int}/export";         // GET ?derived=
            public const string DocumentJournal = "/api/documents/{id:guid}/journal";        // GET
            public const string DocumentLock = "/api/documents/{id:guid}/lock";              // POST, DELETE
            public const string DocumentUnused = "/api/documents/{id:guid}/unused";          // POST
            public const string MissingObjects = "/api/objects/missing";                     // POST
            public const string Object = "/api/objects/{sha}";                               // PUT, GET
            public const string VersionsByHash = "/api/versions/by-hash";                    // POST
            public const string Checkins = "/api/checkins";                                  // POST
            public const string AdminUsers = "/api/admin/users";                             // GET, POST
            public const string AdminUser = "/api/admin/users/{id:guid}";                    // PUT
            public const string AdminLocks = "/api/admin/locks";                             // GET
            public const string AdminLock = "/api/admin/locks/{documentId:guid}";            // DELETE
            public const string AdminSettings = "/api/admin/settings";                       // GET, PUT
            public const string AdminJournal = "/api/admin/journal";                         // GET ?limit=
        }

        /// <summary>Имена параметров строки запроса.</summary>
        public static class Query
        {
            public const string Q = "q";
            public const string ProjectId = "projectId";
            public const string Author = "author";
            public const string IncludeUnused = "includeUnused";
            public const string Limit = "limit";
            public const string Derived = "derived";
            public const string IncludeDrawings = "includeDrawings";
            public const string Roots = "roots";
        }

        public const string Login = Templates.Login;
        public const string Logout = Templates.Logout;
        public const string Me = Templates.Me;
        public const string Notifications = Templates.Notifications;
        public const string NotificationsSeen = Templates.NotificationsSeen;
        public const string Projects = Templates.Projects;
        public const string MissingObjects = Templates.MissingObjects;
        public const string VersionsByHash = Templates.VersionsByHash;
        public const string Checkins = Templates.Checkins;
        public const string AdminUsers = Templates.AdminUsers;
        public const string AdminLocks = Templates.AdminLocks;
        public const string AdminSettings = Templates.AdminSettings;

        /// <param name="roots">Только корневые изделия: активные сборки, на которые не ссылается последняя версия другого активного документа (кроме чертежей).</param>
        public static string SearchDocuments(string? q = null, Guid? projectId = null, string? author = null, bool includeUnused = false, int? limit = null,
            bool roots = false)
        {
            var query = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrWhiteSpace(q)) query.Add(Pair(Query.Q, q!));
            if (projectId.HasValue) query.Add(Pair(Query.ProjectId, G(projectId.Value)));
            if (!string.IsNullOrWhiteSpace(author)) query.Add(Pair(Query.Author, author!));
            if (includeUnused) query.Add(Pair(Query.IncludeUnused, "true"));
            if (limit.HasValue) query.Add(Pair(Query.Limit, limit.Value.ToString(CultureInfo.InvariantCulture)));
            if (roots) query.Add(Pair(Query.Roots, "true"));
            return WithQuery(Templates.Documents, query);
        }

        public static string Document(Guid id) => $"{Prefix}/documents/{G(id)}";

        /// <param name="includeDrawings">Добавить последние версии чертежей, ссылающихся на документы комплекта, и их зависимости.</param>
        public static string Kit(Guid id, int version, bool includeDrawings = false) =>
            includeDrawings ? $"{Version(id, version)}/kit?{Query.IncludeDrawings}=true" : $"{Version(id, version)}/kit";

        public static string WhereUsed(Guid id, int version) => $"{Version(id, version)}/where-used";

        public static string Export(Guid id, int version, bool includeDerived) =>
            $"{Version(id, version)}/export?{Query.Derived}={(includeDerived ? "true" : "false")}";

        public static string DocumentJournal(Guid id) => $"{Document(id)}/journal";

        public static string DocumentLock(Guid id) => $"{Document(id)}/lock";

        public static string DocumentUnused(Guid id) => $"{Document(id)}/unused";

        public static string Object(string sha256) => $"{Prefix}/objects/{Uri.EscapeDataString(sha256)}";

        public static string AdminUser(Guid id) => $"{Prefix}/admin/users/{G(id)}";

        public static string AdminLock(Guid documentId) => $"{Prefix}/admin/locks/{G(documentId)}";

        public static string AdminJournal(int? limit = null) =>
            limit.HasValue
                ? $"{Templates.AdminJournal}?{Query.Limit}={limit.Value.ToString(CultureInfo.InvariantCulture)}"
                : Templates.AdminJournal;

        private static string Version(Guid id, int version) =>
            $"{Document(id)}/versions/{version.ToString(CultureInfo.InvariantCulture)}";

        private static string G(Guid id) => id.ToString("D");

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);

        private static string WithQuery(string path, List<KeyValuePair<string, string>> query)
        {
            if (query.Count == 0) return path;
            var sb = new StringBuilder(path);
            for (var i = 0; i < query.Count; i++)
            {
                sb.Append(i == 0 ? '?' : '&');
                sb.Append(Uri.EscapeDataString(query[i].Key)).Append('=').Append(Uri.EscapeDataString(query[i].Value));
            }
            return sb.ToString();
        }
    }
}
