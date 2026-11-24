using SolidBrew.Core.Api;
using SolidBrew.Core.Cad;
using SolidBrew.Workspace;

namespace SolidBrew.Client.Infrastructure;

/// <summary>Ошибка для показа пользователю: что случилось и подробности (проблемы по документам).</summary>
public sealed record ErrorInfo(string Message, IReadOnlyList<string> Details)
{
    public ErrorInfo(string message) : this(message, Array.Empty<string>()) { }

    /// <summary>Сервер предъявил неизвестный сертификат: отпечаток для диалога доверия.</summary>
    public UntrustedServerCertificateException? UntrustedCertificate { get; init; }

    public override string ToString() => Details.Count == 0 ? Message : Message + Environment.NewLine + string.Join(Environment.NewLine, Details);
}

/// <summary>Перевод исключений в понятный текст — без стека и технических подробностей.</summary>
public static class ErrorText
{
    public static ErrorInfo Describe(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
        if (UntrustedServerCertificateException.Find(ex) is { } untrusted)
            return new ErrorInfo(untrusted.Message) { UntrustedCertificate = untrusted };
        switch (ex)
        {
            case OperationCanceledException:
                return new ErrorInfo("Операция отменена.");
            case PdmApiException api:
                return DescribeApi(api);
            case WorkspaceException:
            case CadException:
                return new ErrorInfo(ex.Message);
            case UnauthorizedAccessException:
                return new ErrorInfo("Нет доступа к файлу или папке: " + ex.Message + " Проверьте права и что файл не открыт в другой программе.");
            case IOException:
                return new ErrorInfo("Ошибка работы с файлом: " + ex.Message + " Проверьте, что файл не открыт в другой программе, и повторите.");
            default:
                return new ErrorInfo("Непредвиденная ошибка: " + ex.Message + " Повторите операцию; если ошибка повторяется — сообщите администратору PDM.");
        }
    }

    private static ErrorInfo DescribeApi(PdmApiException ex)
    {
        var details = new List<string>();
        if (ex.Error?.Problems is { Count: > 0 } problems)
        {
            foreach (var p in problems)
                details.Add(string.IsNullOrEmpty(p.RelPath) ? p.Message : $"«{p.RelPath}»: {p.Message}");
        }
        var message = string.IsNullOrWhiteSpace(ex.Message) ? PdmApiException.DefaultMessage(ex.StatusCode) : ex.Message;
        if (ex.Error?.Lock is { } l)
            message += $" Документ взят в работу: {l.UserDisplayName} ({l.UserLogin}), с {Format.Time(l.At)}.";
        return new ErrorInfo(message, details);
    }

    /// <summary>Проблемы плана или итога операции — строками для списка.</summary>
    public static ErrorInfo FromProblems(string message, IEnumerable<PlanProblem> problems) =>
        new(message, problems.Select(p => p.ToString()).Distinct().ToList());
}

/// <summary>Форматирование для показа.</summary>
public static class Format
{
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public static string Time(DateTimeOffset? at) => at == null ? string.Empty : Time(at.Value);

    public static string Bytes(long size)
    {
        if (size < 1024) return $"{size} Б";
        if (size < 1024 * 1024) return $"{size / 1024.0:0.#} КБ";
        if (size < 1024L * 1024 * 1024) return $"{size / 1024.0 / 1024.0:0.#} МБ";
        return $"{size / 1024.0 / 1024.0 / 1024.0:0.##} ГБ";
    }

    public static string Kind(Core.Contracts.DocumentKind kind) => kind switch
    {
        Core.Contracts.DocumentKind.Part => "деталь",
        Core.Contracts.DocumentKind.Assembly => "сборка",
        Core.Contracts.DocumentKind.Drawing => "чертёж",
        _ => "прочее",
    };

    public static string Role(Core.Contracts.UserRole role) => role switch
    {
        Core.Contracts.UserRole.Admin => "администратор",
        Core.Contracts.UserRole.Designer => "конструктор",
        _ => "читатель",
    };

    public static string Doc(string relPath, string? designation)
    {
        var name = FileName(relPath);
        return string.IsNullOrWhiteSpace(designation) ? $"«{name}»" : $"«{designation}» ({name})";
    }

    public static string FileName(string path)
    {
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i < 0 ? path : path.Substring(i + 1);
    }

    public static string Lock(Core.Contracts.LockDto? l, Guid? me)
    {
        if (l == null) return "свободен";
        if (me != null && l.UserId == me) return "взят мной";
        return $"взят: {(string.IsNullOrWhiteSpace(l.UserDisplayName) ? l.UserLogin : l.UserDisplayName)}, с {Time(l.At)}";
    }
}
