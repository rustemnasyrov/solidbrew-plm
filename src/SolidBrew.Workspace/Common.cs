using SolidBrew.Core.Api;

namespace SolidBrew.Workspace;

/// <summary>Этап длительной операции.</summary>
public enum WorkStage
{
    Preparing,
    Scanning,
    Reading,
    Hashing,
    Downloading,
    Uploading,
    Placing,
    Locking,
    Publishing,
    Verifying,
    Exporting,
    Done,
}

/// <summary>Ход выполнения для индикатора: этап, позиция, всего, текст.</summary>
public sealed record WorkProgress(WorkStage Stage, long Current, long Total, string Message)
{
    /// <summary>Доля выполнения 0..1 или null, если всего неизвестно.</summary>
    public double? Fraction => Total > 0 ? Math.Min(1.0, (double)Current / Total) : null;
}

/// <summary>Ошибка операции рабочей папки; <see cref="Exception.Message"/> — понятный пользователю текст.</summary>
public class WorkspaceException : Exception
{
    public WorkspaceException(string message) : base(message) { }

    public WorkspaceException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Серьёзность проблемы плана.</summary>
public enum ProblemSeverity
{
    /// <summary>Блокирует выполнение.</summary>
    Error,
    /// <summary>Требует внимания или подтверждения, но не блокирует.</summary>
    Warning,
    /// <summary>Для сведения.</summary>
    Info,
}

/// <summary>Коды локальных проблем (дополняют <see cref="SolidBrew.Core.Contracts.ProblemCodes"/> сервера).</summary>
public static class LocalProblemCodes
{
    public const string MissingReference = "local.missingReference";
    public const string AmbiguousReference = "local.ambiguousReference";
    public const string DraftNotIncluded = "local.draftNotIncluded";
    public const string ChangedNotIncluded = "local.changedNotIncluded";
    public const string ChangedNotLocked = "local.changedNotLocked";
    public const string UnsupportedFile = "local.unsupportedFile";
    public const string ReadError = "local.readError";
    public const string InvalidPath = "local.invalidPath";
    public const string NewerCadVersion = "local.newerCadVersion";
    public const string LockLost = "local.lockLost";
    public const string NotLatest = "local.notLatest";
    public const string FileChanged = "local.fileChanged";
    public const string Unconfirmed = "local.unconfirmed";
    public const string OutsideFolders = "local.outsideFolders";
    public const string NoCommonRoot = "local.noCommonRoot";
    public const string VersionConflict = "local.versionConflict";
    public const string UnresolvedInKit = "local.unresolvedInKit";
    public const string LockedByOther = "local.lockedByOther";
    public const string LocalChanges = "local.localChanges";
    public const string PathTaken = "local.pathTaken";
    public const string Server = "server";
}

/// <summary>Проблема, найденная при подготовке или выполнении операции.</summary>
public sealed class PlanProblem
{
    public PlanProblem(ProblemSeverity severity, string code, string message, string? relPath = null, Guid? documentId = null)
    {
        Severity = severity;
        Code = code;
        Message = message;
        RelPath = relPath;
        DocumentId = documentId;
    }

    public ProblemSeverity Severity { get; }
    /// <summary>Код: <see cref="LocalProblemCodes"/> или код сервера из <see cref="SolidBrew.Core.Contracts.ProblemCodes"/>.</summary>
    public string Code { get; }
    /// <summary>Понятный текст: что за документ, что случилось, что делать.</summary>
    public string Message { get; }
    public string? RelPath { get; }
    public Guid? DocumentId { get; }
    public bool IsBlocking => Severity == ProblemSeverity.Error;

    public override string ToString() => (Severity == ProblemSeverity.Error ? "Ошибка: " : Severity == ProblemSeverity.Warning ? "Предупреждение: " : "") + Message;

    internal static List<PlanProblem> FromServer(PdmApiException ex)
    {
        var list = new List<PlanProblem>();
        if (ex.Error?.Problems != null)
        {
            foreach (var p in ex.Error.Problems)
            {
                var text = string.IsNullOrEmpty(p.RelPath) ? p.Message : $"«{p.RelPath}»: {p.Message}";
                list.Add(new PlanProblem(ProblemSeverity.Error, p.Code, text, p.RelPath, p.DocumentId));
            }
        }
        if (list.Count == 0)
        {
            var text = ex.Message;
            if (ex.Error?.Lock is { } l)
                text += $" Документ взят в работу: {l.UserDisplayName} ({l.UserLogin}), с {Texts.Time(l.At)}.";
            list.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Server, text));
        }
        return list;
    }
}

/// <summary>Общий итог операции.</summary>
public class OperationResult
{
    public bool Success { get; set; }
    /// <summary>Итог одной фразой для строки состояния.</summary>
    public string Message { get; set; } = string.Empty;
    public List<PlanProblem> Problems { get; } = new();
}

/// <summary>Прогресс без перехода в контекст синхронизации (внешний IProgress сам решает, куда доставлять).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

internal static class Texts
{
    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    public static string Doc(string relPath, string? designation = null)
    {
        var name = FileNameOf(relPath);
        return string.IsNullOrWhiteSpace(designation) ? $"«{name}»" : $"«{designation}» ({name})";
    }

    public static string FileNameOf(string path)
    {
        var i = path.LastIndexOfAny(new[] { '/', '\\' });
        return i < 0 ? path : path.Substring(i + 1);
    }

    public static string Bytes(long size)
    {
        if (size < 1024) return $"{size} Б";
        if (size < 1024 * 1024) return $"{size / 1024.0:0.#} КБ";
        if (size < 1024L * 1024 * 1024) return $"{size / 1024.0 / 1024.0:0.#} МБ";
        return $"{size / 1024.0 / 1024.0 / 1024.0:0.##} ГБ";
    }
}
