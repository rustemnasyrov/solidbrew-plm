using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace;

/// <summary>Локальное состояние файла.</summary>
public enum LocalFileState
{
    /// <summary>Совпадает с разложенной версией.</summary>
    Unchanged,
    /// <summary>Содержимое отличается от разложенной версии.</summary>
    ModifiedLocally,
    /// <summary>Новый файл известного CAD, ещё не сданный (черновой).</summary>
    Draft,
    /// <summary>Учтён в рабочей папке, но файла нет на диске.</summary>
    MissingLocally,
}

/// <summary>Состояние блокировки на сервере.</summary>
public enum LockState
{
    /// <summary>Сервер не опрашивался или документ не учтён (черновик).</summary>
    Unknown,
    Free,
    LockedByMe,
    LockedByOther,
}

/// <summary>Состояние одного файла рабочей папки.</summary>
public sealed class FileStatus
{
    public string RelPath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string FileName => Texts.FileNameOf(RelPath);
    public Guid? DocumentId { get; set; }
    /// <summary>Подпапка проекта документа чужого проекта (см. <see cref="WorkspaceEntry.ProjectFolder"/>); null — документ проекта рабочей папки или черновик.</summary>
    public string? ProjectFolder { get; set; }
    public string Cad { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; }
    public string? Designation { get; set; }
    public string? Name { get; set; }
    /// <summary>Версия, разложенная в рабочей папке; null — черновик.</summary>
    public int? LocalVersion { get; set; }
    /// <summary>Последняя версия на сервере; null — сервер не опрашивался.</summary>
    public int? LatestVersion { get; set; }
    public LocalFileState Local { get; set; }
    public LockState Lock { get; set; }
    /// <summary>Блокировка (своя или чужая): кто и с какого времени.</summary>
    public LockDto? LockInfo { get; set; }
    /// <summary>По состоянию папки документ был взят мной, а на сервере блокировки нет — её сняли принудительно.</summary>
    public bool LockRemovedByOthers { get; set; }
    public DocumentStatus? ServerStatus { get; set; }
    public bool IsReadOnly { get; set; }
    /// <summary>В рабочей папке не последняя версия.</summary>
    public bool IsOutdated => LocalVersion != null && LatestVersion != null && LocalVersion < LatestVersion;

    /// <summary>Краткое описание состояния по-русски для списка.</summary>
    public string StateText
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Local switch
            {
                LocalFileState.Unchanged => "не изменён",
                LocalFileState.ModifiedLocally => "изменён локально",
                LocalFileState.Draft => "черновой",
                LocalFileState.MissingLocally => "нет файла в рабочей папке",
                _ => string.Empty,
            });
            switch (Lock)
            {
                case LockState.Free: parts.Add("свободен"); break;
                case LockState.LockedByMe: parts.Add("взят мной"); break;
                case LockState.LockedByOther: parts.Add($"взят: {LockInfo?.UserDisplayName}"); break;
            }
            if (IsOutdated) parts.Add($"есть новая версия v{LatestVersion}");
            if (LockRemovedByOthers) parts.Add("блокировку сняли принудительно");
            return string.Join(", ", parts.Where(p => p.Length > 0));
        }
    }
}

/// <summary>Состояние рабочей папки.</summary>
public sealed class WorkspaceStatus
{
    public string WorkRoot { get; set; } = string.Empty;
    public Guid ProjectId { get; set; }
    public List<FileStatus> Files { get; } = new();
    /// <summary>Сообщения пользователю (например, о принудительно снятых блокировках).</summary>
    public List<string> Messages { get; } = new();
    /// <summary>Сервер опрошен успешно.</summary>
    public bool ServerChecked { get; set; }
    /// <summary>Ошибка опроса сервера (нет связи и т. п.), если была.</summary>
    public string? ServerError { get; set; }

    public FileStatus? FindByDocument(Guid documentId) => Files.FirstOrDefault(f => f.DocumentId == documentId);

    public FileStatus? FindByPath(string relPath) =>
        Files.FirstOrDefault(f => string.Equals(f.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
}
