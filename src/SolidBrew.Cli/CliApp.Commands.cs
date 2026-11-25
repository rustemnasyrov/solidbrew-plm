namespace SolidBrew.Cli;

public sealed partial class CliApp
{
    private IEnumerable<CommandSpec> BuildCommands()
    {
        string[] none = Array.Empty<string>();
        yield return new("server", "server [<адрес>] [--trust <отпечаток>] [--cache <папка>] [--test-cad on|off]",
            "Показать или задать адрес сервера PDM (например https://10.0.1.9:8443/), папку кэша и тестовый CAD. " +
            "При смене адреса проверяет подключение; неизвестный сертификат сервера — печатает его отпечаток SHA-256 (код 2). " +
            "--trust — доверять сертификату с этим отпечатком (сверьте с администратором).",
            none, new[] { "trust", "cache", "test-cad" }, ServerAsync);
        yield return new("login", "login [--user <имя>] [--password-stdin]",
            "Войти в PDM. Пароль вводится скрыто с консоли или читается строкой из stdin (--password-stdin).",
            new[] { "password-stdin" }, new[] { "user" }, LoginAsync);
        yield return new("logout", "logout", "Выйти: завершить сессию и забыть сохранённый вход.", none, none, LogoutAsync);
        yield return new("whoami", "whoami", "Текущий пользователь и новые уведомления (например, о снятой блокировке).", none, none, WhoAmIAsync);
        yield return new("projects", "projects | projects create <имя> [--library]",
            "Список проектов или создание проекта (администратор).", new[] { "library" }, none, ProjectsAsync);
        yield return new("search", "search [текст] [--author <автор>] [--project <проект>] [--all] [--limit n]",
            "Поиск по обозначению, наименованию, имени файла и автору. --all — включая «не используется».",
            new[] { "all" }, new[] { "author", "project", "limit" }, SearchAsync);
        yield return new("products", "products [текст] [--all] [--project <проект>] [--limit n]",
            "Изделия: корневые сборки — не входящие в последние версии других документов, кроме чертежей. --all — все сборки (с числом применений). * — закреплённые.",
            new[] { "all" }, new[] { "project", "limit" }, ProductsAsync);
        yield return new("show", "show <документ> [--version n]",
            "Карточка документа: свойства, версии, ссылки, производные файлы, блокировка, журнал.",
            none, new[] { "version" }, ShowAsync);
        yield return new("where-used", "where-used <документ> [--version n]",
            "Где применяется версия документа (по умолчанию последняя); отмечает применения не последней версии.",
            none, new[] { "version" }, WhereUsedAsync);
        yield return new("import", "import <головной файл> --project <имя|id> [--extra <папка>]... [--accept-name-matches] [--accept-outside] [--choose <ссылка>=<путь>]... [--by-link <файл>]... [--allow-missing] [--accept-newer-cad] [--comment <текст>] [--yes]",
            "Поставить на учёт готовый проект: исходники только читаются. Файлы, уже учтённые в PDM (то же содержимое), берутся из хранилища; " +
            "--by-link — не загружать файл, оставить ссылкой на него. Без --yes печатает отчёт по ссылкам и выходит с кодом 2.",
            new[] { "accept-name-matches", "accept-outside", "allow-missing", "accept-newer-cad", "yes" },
            new[] { "extra", "project", "choose", "by-link", "comment" }, ImportAsync);
        yield return new("open", "open <документ> [--version n] [--to <папка>] [--with-drawings] [--replace-all|--keep-all] [--verify] [--yes]",
            "Открыть версию (по умолчанию последнюю): докачать в кэш и разложить комплект в рабочую папку. --with-drawings — вместе с чертежами документов комплекта. " +
            "Документы другого проекта кладутся в подпапку с именем проекта. При конфликтах без --yes печатает план и выходит с кодом 2.",
            new[] { "replace-all", "keep-all", "verify", "yes", "with-drawings" }, new[] { "version", "to" }, OpenAsync);
        yield return new("update", "update <файл>... [--replace-all|--keep-all] [--yes]",
            "Обновить документы рабочей папки до последней версии (вместе с их комплектами).",
            new[] { "replace-all", "keep-all", "yes" }, none, UpdateAsync);
        yield return new("status", "status [--offline]", "Состояние рабочей папки: изменения, черновики, блокировки, новые версии.",
            new[] { "offline" }, none, StatusAsync);
        yield return new("tree", "tree [<документ>] [--version n] [--no-drawings]",
            "Дерево комплекта с состояниями (по умолчанию — открытый в папке). Чертежи показываются под моделями; --no-drawings — без чертежей.",
            new[] { "no-drawings" }, new[] { "version" }, TreeAsync);
        yield return new("checkout", "checkout <файл>...", "Взять в работу: блокировка на сервере, файл становится доступным для записи.",
            none, none, CheckOutAsync);
        yield return new("undo", "undo <файл>... [--revert] [--yes]",
            "Отменить взятие. Правки остаются в файле (только для чтения); --revert — вернуть сданную версию (с правками — только с --yes).",
            new[] { "revert", "yes" }, none, UndoAsync);
        yield return new("checkin", "checkin [--comment <текст>] [--keep] [--include <файл>]... [--exclude <файл>]... [--accept-newer-cad] [--yes]",
            "Сдать изменённые взятые и новые документы одной операцией. Без --yes печатает план и выходит с кодом 2; блокирующие проблемы — код 1.",
            new[] { "keep", "accept-newer-cad", "yes" }, new[] { "comment", "include", "exclude" }, CheckinAsync);
        yield return new("rename", "rename <файл> <новый путь> [--comment <текст>] [--yes]",
            "Переименовать или переместить документ: ссылающиеся документы берутся в работу, исправляются и сдаются одной операцией.",
            new[] { "yes" }, new[] { "comment" }, RenameAsync);
        yield return new("unused", "unused <документ> [--undo]", "Пометить документ «не используется» (или снять пометку).",
            new[] { "undo" }, none, UnusedAsync);
        yield return new("export", "export <документ> [--version n] <zip> [--derived]",
            "Выгрузить комплект версии в ZIP с перечнем версий и хешей (manifest.json).",
            new[] { "derived" }, new[] { "version" }, ExportAsync);
        yield return new("admin", "admin users | user-add <login> <имя> --role r [--password-stdin] | user-set <login> [--role r] [--name n] [--disable|--enable] [--password-stdin] | locks | unlock <документ> | settings [ключ=значение]... | journal [--limit n]",
            "Администрирование: пользователи, блокировки, настройки, журнал.",
            new[] { "password-stdin", "disable", "enable" }, new[] { "role", "name", "limit" }, AdminAsync);
    }
}
