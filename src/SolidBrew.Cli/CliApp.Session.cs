using System.Text;
using System.Text.Json;
using SolidBrew.Core.Contracts;
using SolidBrew.Workspace;

namespace SolidBrew.Cli;

public sealed partial class CliApp
{
    // ------------------------------------------------------------------ server, login, logout, whoami

    private async Task<int> ServerAsync()
    {
        var p = Profile;
        var changed = false;
        var probe = false;
        string? trust = null;
        if (_args.Value("trust") is { } trustText)
        {
            try
            {
                trust = ServerCertificateValidator.NormalizeThumbprint(trustText);
            }
            catch (WorkspaceException ex)
            {
                throw new CliException(ex.Message);
            }
            probe = true;
        }
        if (_args.At(0) is { } url)
        {
            var text = url.Trim();
            if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new CliException($"Неверный адрес сервера PDM «{url}». Пример: https://10.0.1.9:8443/");
            var normalized = uri.ToString();
            if (!string.Equals(normalized, p.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                p.ServerUrl = normalized;
                p.Token = null;
                p.TokenExpiresAt = null;
                probe = true;
            }
            changed = true;
        }
        if (_args.Value("cache") is { } cache)
        {
            p.CacheRoot = cache.Length == 0 ? null : FullPath(cache);
            changed = true;
        }
        if (_args.Value("test-cad") is { } tc)
        {
            p.EnableTestCad = tc.Trim().ToLowerInvariant() switch
            {
                "on" or "yes" or "true" or "1" or "да" => true,
                "off" or "no" or "false" or "0" or "нет" => false,
                _ => throw new CliException("--test-cad: укажите on или off."),
            };
            changed = true;
        }
        if (changed) SaveProfile();

        // Проверка подключения и сертификата: отпечаток из --trust сохраняется, только если сервер предъявил именно его.
        string? warning = null;
        if (probe)
        {
            var thumbprint = trust ?? p.TrustedCertificateThumbprint;
            try
            {
                await _env.ApiFactory(p.ServerUrl, null, thumbprint).GetMeAsync(_ct).ConfigureAwait(false);
            }
            catch (Core.Api.PdmApiException ex) when (ex.StatusCode is 401 or 403)
            {
                // Сервер ответил — соединение и сертификат в порядке.
            }
            catch (Core.Api.PdmApiException ex) when (UntrustedServerCertificateException.Find(ex) is { } untrusted)
            {
                if (trust == null) throw;
                Err.WriteLine("Отпечаток из --trust не совпадает с сертификатом сервера и не сохранён.");
                Err.WriteLine($"  указан:           {ServerCertificateValidator.FormatThumbprint(trust)}");
                Err.WriteLine($"  предъявил сервер: {untrusted.DisplayThumbprint}");
                Err.WriteLine("Сверьте отпечаток с администратором PDM.");
                if (Json)
                    Out.WriteLine(JsonSerializer.Serialize(new { error = untrusted.Message, exitCode = NeedsAnswer, server = p.ServerUrl, thumbprint = untrusted.Thumbprint }, JsonOut));
                return NeedsAnswer;
            }
            catch (Core.Api.PdmApiException ex)
            {
                warning = "Сервер не проверен: " + ex.Message;
            }
            if (warning == null && trust != null && !string.Equals(trust, p.TrustedCertificateThumbprint, StringComparison.Ordinal))
            {
                p.TrustedCertificateThumbprint = trust;
                SaveProfile();
            }
            if (warning != null) Err.WriteLine(warning);
        }

        Emit(new
        {
            serverUrl = p.ServerUrl, login = p.Login, loggedIn = p.HasValidToken, cacheRoot = p.CacheRoot, enableTestCad = p.EnableTestCad,
            trustedCertificateThumbprint = p.TrustedCertificateThumbprint, warning,
        }, () =>
        {
            Out.WriteLine($"Сервер PDM: {p.ServerUrl}");
            if (p.TrustedCertificateThumbprint != null)
                Out.WriteLine($"Доверенный сертификат: SHA-256 {ServerCertificateValidator.FormatThumbprint(p.TrustedCertificateThumbprint)}");
            Out.WriteLine(p.HasValidToken ? $"Вход: {p.Login}" : "Вход не выполнен (sbpdm login).");
            Out.WriteLine($"Кэш: {p.CacheRoot ?? Workspace.LocalCache.DefaultRoot}");
            if (p.EnableTestCad) Out.WriteLine("Тестовый CAD: включён.");
        });
        return Ok;
    }

    private async Task<int> LoginAsync()
    {
        var p = Profile;
        var login = _args.Value("user") ?? p.Login;
        if (string.IsNullOrWhiteSpace(login))
        {
            Err.Write("Пользователь: ");
            login = _env.In.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(login)) throw new CliException("Не указан пользователь. Пример: sbpdm login --user ivanov");
        }
        var password = ReadPassword("Пароль: ");

        var api = _env.ApiFactory(p.ServerUrl, null, p.TrustedCertificateThumbprint);
        var response = await api.LoginAsync(login.Trim(), password, _ct).ConfigureAwait(false);
        p.Login = response.User.Login;
        p.Token = response.Token;
        p.TokenExpiresAt = response.ExpiresAt;
        SaveProfile();

        Emit(new { user = response.User, serverUrl = p.ServerUrl, expiresAt = response.ExpiresAt },
            () => Out.WriteLine($"Вход выполнен: {response.User.DisplayName} ({response.User.Login}), роль — {RoleText(response.User.Role)}. Сервер: {p.ServerUrl}"));
        return Ok;
    }

    private string ReadPassword(string prompt)
    {
        string? password;
        if (_args.Flag("password-stdin"))
        {
            password = _env.In.ReadLine();
        }
        else if (_env.ReadSecret != null)
        {
            password = _env.ReadSecret(prompt);
        }
        else
        {
            if (Console.IsInputRedirected)
                throw new CliException("Ввод перенаправлен — скрыто ввести пароль нельзя. Передайте его строкой через --password-stdin.");
            Err.Write(prompt);
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Escape) throw new CliException("Ввод пароля отменён.");
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
            Err.WriteLine();
            password = sb.ToString();
        }
        password = password?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(password)) throw new CliException("Пароль не введён.");
        return password;
    }

    private async Task<int> LogoutAsync()
    {
        var p = Profile;
        if (!string.IsNullOrEmpty(p.Token))
        {
            try
            {
                await _env.ApiFactory(p.ServerUrl, p.Token, p.TrustedCertificateThumbprint).LogoutAsync(_ct).ConfigureAwait(false);
            }
            catch (Core.Api.PdmApiException)
            {
                // Сессия уже недействительна или нет связи — локальный вход всё равно забываем.
            }
        }
        p.Token = null;
        p.TokenExpiresAt = null;
        SaveProfile();
        Emit(new { loggedOut = true }, () => Out.WriteLine("Выход выполнен."));
        return Ok;
    }

    private async Task<int> WhoAmIAsync()
    {
        var me = await Api.GetMeAsync(_ct).ConfigureAwait(false);
        var notifications = (await Api.GetNotificationsAsync(_ct).ConfigureAwait(false)).Where(n => !n.Seen).ToList();
        Emit(new { user = me, serverUrl = Profile.ServerUrl, notifications }, () =>
        {
            Out.WriteLine($"{me.DisplayName} ({me.Login}), роль — {RoleText(me.Role)}. Сервер: {Profile.ServerUrl}");
            foreach (var n in notifications)
                Out.WriteLine($"Уведомление {Time(n.At)}: {n.Text}");
        });
        if (notifications.Count > 0) await Api.MarkNotificationsSeenAsync(_ct).ConfigureAwait(false);
        return Ok;
    }

    // ------------------------------------------------------------------ проекты

    private async Task<int> ProjectsAsync()
    {
        if (string.Equals(_args.At(0), "create", StringComparison.OrdinalIgnoreCase))
        {
            var name = Need(1, "название проекта");
            var project = await Api.CreateProjectAsync(new CreateProjectRequest
            {
                Name = name,
                Kind = _args.Flag("library") ? ProjectKind.Library : ProjectKind.Project,
            }, _ct).ConfigureAwait(false);
            Emit(project, () => Out.WriteLine($"Создан {(project.Kind == ProjectKind.Library ? "раздел библиотеки" : "проект")} «{project.Name}», ID {project.Id}."));
            return Ok;
        }
        if (_args.At(0) != null) throw new CliException($"Неизвестная подкоманда «{_args.At(0)}». Использование: sbpdm projects [create <имя> [--library]].");

        var projects = await Api.GetProjectsAsync(_ct).ConfigureAwait(false);
        Emit(projects, () => Table(new[] { "Проект", "Вид", "ID" },
            projects.Select(p => new[] { p.Name, p.Kind == ProjectKind.Library ? "библиотека" : "проект", p.Id.ToString() })));
        return Ok;
    }

    // ------------------------------------------------------------------ администрирование

    private async Task<int> AdminAsync()
    {
        var sub = Need(0, "раздел администрирования (users, user-add, user-set, locks, unlock, settings, journal)").ToLowerInvariant();
        switch (sub)
        {
            case "users":
            {
                var users = await Api.GetUsersAsync(_ct).ConfigureAwait(false);
                Emit(users, () => Table(new[] { "Логин", "Имя", "Роль", "Активен" },
                    users.Select(u => new[] { u.Login, u.DisplayName, RoleText(u.Role), u.Active ? "да" : "нет" })));
                return Ok;
            }
            case "user-add":
            {
                var login = Need(1, "логин");
                var name = Need(2, "имя пользователя");
                var role = ParseRole(_args.Value("role") ?? throw new CliException("Укажите роль: --role admin|designer|reader."));
                var password = ReadPassword($"Пароль для {login}: ");
                var user = await Api.CreateUserAsync(new CreateUserRequest { Login = login, DisplayName = name, Password = password, Role = role }, _ct).ConfigureAwait(false);
                Emit(user, () => Out.WriteLine($"Создан пользователь {user.DisplayName} ({user.Login}), роль — {RoleText(user.Role)}."));
                return Ok;
            }
            case "user-set":
            {
                var login = Need(1, "логин");
                var users = await Api.GetUsersAsync(_ct).ConfigureAwait(false);
                var target = users.FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase))
                             ?? throw new CliException($"Пользователь «{login}» не найден.");
                if (_args.Flag("disable") && _args.Flag("enable")) throw new CliException("Укажите что-то одно: --disable или --enable.");
                var request = new UpdateUserRequest
                {
                    DisplayName = _args.Value("name"),
                    Role = _args.Value("role") is { } r ? ParseRole(r) : null,
                    Active = _args.Flag("disable") ? false : _args.Flag("enable") ? true : null,
                    Password = _args.Flag("password-stdin") ? ReadPassword("Новый пароль: ") : null,
                };
                if (request.DisplayName == null && request.Role == null && request.Active == null && request.Password == null)
                    throw new CliException("Нечего менять: укажите --role, --name, --disable, --enable или --password-stdin.");
                var user = await Api.UpdateUserAsync(target.Id, request, _ct).ConfigureAwait(false);
                Emit(user, () => Out.WriteLine($"Пользователь {user.DisplayName} ({user.Login}): роль — {RoleText(user.Role)}, {(user.Active ? "активен" : "отключён")}."));
                return Ok;
            }
            case "locks":
            {
                var locks = await Api.GetLocksAsync(_ct).ConfigureAwait(false);
                var docs = new Dictionary<Guid, DocumentSummaryDto>();
                foreach (var l in locks)
                {
                    try
                    {
                        docs[l.DocumentId] = (await Api.GetDocumentAsync(l.DocumentId, _ct).ConfigureAwait(false)).Document;
                    }
                    catch (Core.Api.PdmApiException ex) when (ex.StatusCode == 404)
                    {
                    }
                }
                Emit(locks, () =>
                {
                    if (locks.Count == 0) Out.WriteLine("Взятых в работу документов нет.");
                    else Table(new[] { "Документ", "Кем", "С", "ID" }, locks.Select(l => new[]
                    {
                        docs.TryGetValue(l.DocumentId, out var d) ? Label(d.RelPath, d.Designation) : "?",
                        $"{l.UserDisplayName} ({l.UserLogin})", Time(l.At), l.DocumentId.ToString(),
                    }));
                });
                return Ok;
            }
            case "unlock":
            {
                var doc = await ResolveDocumentAsync(Need(1, "документ")).ConfigureAwait(false);
                await Api.ForceUnlockAsync(doc.Id, _ct).ConfigureAwait(false);
                Emit(new { documentId = doc.Id, unlocked = true }, () =>
                    Out.WriteLine($"Блокировка {Label(doc.RelPath, doc.Designation)} снята{(doc.Lock != null ? $" (была: {doc.Lock.UserDisplayName})" : "")}. Владелец получит уведомление."));
                return Ok;
            }
            case "settings":
            {
                SettingsDto settings;
                if (_args.Positionals.Count > 1)
                {
                    var update = new SettingsDto();
                    foreach (var pair in _args.Positionals.Skip(1))
                    {
                        var eq = pair.IndexOf('=');
                        if (eq <= 0) throw new CliException($"Ожидается ключ=значение, указано «{pair}». Пустое значение удаляет ключ.");
                        update.Values[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
                    }
                    settings = await Api.UpdateSettingsAsync(update, _ct).ConfigureAwait(false);
                }
                else
                {
                    settings = await Api.GetSettingsAsync(_ct).ConfigureAwait(false);
                }
                Emit(settings, () =>
                {
                    if (settings.Values.Count == 0) Out.WriteLine("Настроек нет.");
                    foreach (var kv in settings.Values.OrderBy(k => k.Key, StringComparer.Ordinal))
                        Out.WriteLine($"{kv.Key} = {kv.Value}");
                });
                return Ok;
            }
            case "journal":
            {
                var entries = await Api.GetJournalAsync(_args.Int("limit") ?? 100, _ct).ConfigureAwait(false);
                Emit(entries, () => PrintJournal(entries));
                return Ok;
            }
            default:
                throw new CliException($"Неизвестный раздел «{sub}». Разделы: users, user-add, user-set, locks, unlock, settings, journal.");
        }
    }

    private void PrintJournal(IEnumerable<JournalEntryDto> entries) =>
        Table(new[] { "Время", "Кто", "Действие", "Документ", "Версия", "Подробности" }, entries.Select(e => new[]
        {
            Time(e.At), e.UserLogin, ActionText(e.Action), e.DocumentId?.ToString(), e.Version is { } v ? "v" + v : null, e.Details,
        }));

    internal static UserRole ParseRole(string text) => text.Trim().ToLowerInvariant() switch
    {
        "admin" or "администратор" => UserRole.Admin,
        "designer" or "конструктор" => UserRole.Designer,
        "reader" or "читатель" => UserRole.Reader,
        _ => throw new CliException($"Неизвестная роль «{text}». Роли: admin (администратор), designer (конструктор), reader (читатель)."),
    };

    internal static string RoleText(UserRole role) => role switch
    {
        UserRole.Admin => "администратор",
        UserRole.Designer => "конструктор",
        _ => "читатель",
    };

    internal static string ActionText(string action) => action switch
    {
        JournalActions.Login => "вход",
        JournalActions.Checkin => "сдача",
        JournalActions.Lock => "взят в работу",
        JournalActions.Unlock => "отмена взятия",
        JournalActions.ForceUnlock => "блокировка снята администратором",
        JournalActions.MarkUnused => "пометка «не используется»",
        JournalActions.Export => "выгрузка",
        JournalActions.UserCreated => "пользователь создан",
        JournalActions.UserUpdated => "пользователь изменён",
        JournalActions.SettingsChanged => "настройки изменены",
        JournalActions.ProjectCreated => "проект создан",
        _ => action,
    };
}
