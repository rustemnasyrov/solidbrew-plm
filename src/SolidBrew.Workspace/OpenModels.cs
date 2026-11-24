using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace;

/// <summary>Что произойдёт с документом комплекта при открытии.</summary>
public enum OpenAction
{
    /// <summary>Файла нет — будет разложен.</summary>
    Place,
    /// <summary>Лежит другая версия или другой файл — будет заменён (если конфликты разрешены «Заменить»).</summary>
    Replace,
    /// <summary>Нужная версия уже лежит и не изменена.</summary>
    AlreadyInPlace,
    /// <summary>Неучтённый файл с тем же путём совпадает по содержимому — будет учтён без копирования.</summary>
    Adopt,
}

/// <summary>Вид конфликта при раскладке.</summary>
public enum OpenConflictKind
{
    /// <summary>Лежит другая версия того же документа, без правок: «заменить vX на vY?».</summary>
    OtherVersion,
    /// <summary>Лежит другой учтённый документ без правок.</summary>
    OtherDocument,
    /// <summary>В файле несданные локальные правки.</summary>
    LocalChanges,
    /// <summary>Неучтённый файл с тем же путём и другим содержимым.</summary>
    UnknownFile,
    /// <summary>Документ уже лежит в рабочей папке под другим путём (переименован между версиями).</summary>
    MovedDocument,
}

/// <summary>Решение по конфликту.</summary>
public enum ConflictResolution
{
    /// <summary>Заменить локальный файл файлом комплекта.</summary>
    Replace,
    /// <summary>Оставить локальный файл как есть; документ комплекта не раскладывается.</summary>
    Keep,
}

/// <summary>Конфликт раскладки. <see cref="Resolution"/> задаёт пользователь (по умолчанию — <see cref="DefaultResolution"/>).</summary>
public sealed class OpenConflict
{
    public OpenConflictKind Kind { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public Guid DocumentId { get; init; }
    /// <summary>Версия, лежащая в папке (если учтена).</summary>
    public int? LocalVersion { get; init; }
    /// <summary>Версия из комплекта.</summary>
    public int NewVersion { get; init; }
    /// <summary>Для <see cref="OpenConflictKind.MovedDocument"/>: где документ лежит сейчас.</summary>
    public string? OldRelPath { get; init; }
    /// <summary>Вопрос пользователю.</summary>
    public string Message { get; init; } = string.Empty;
    public ConflictResolution DefaultResolution { get; init; }
    public ConflictResolution Resolution { get; set; }
}

/// <summary>Документ комплекта в плане открытия.</summary>
public sealed class OpenPlanItem
{
    public Guid DocumentId { get; init; }
    public int Version { get; init; }
    public int LatestVersion { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string FileName => Texts.FileNameOf(RelPath);
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Cad { get; init; } = string.Empty;
    public DocumentKind Kind { get; init; }
    public string? CadVersion { get; init; }
    public int? CadVersionMajor { get; init; }
    public string? Designation { get; init; }
    public string? Name { get; init; }
    public LockDto? LockInfo { get; init; }
    /// <summary>Документ взят в работу мной — файл будет без атрибута «только для чтения».</summary>
    public bool LockedByMe { get; init; }
    public OpenAction Action { get; internal set; }
    /// <summary>Версия, лежащая сейчас в папке по этому пути (если учтена).</summary>
    public int? LocalVersion { get; internal set; }
    /// <summary>Объект уже есть в кэше — скачивать не нужно.</summary>
    public bool InCache { get; internal set; }
    public List<OpenConflict> Conflicts { get; } = new();
    /// <summary>Будет ли файл разложен при текущих решениях по конфликтам.</summary>
    public bool WillWrite => Action != OpenAction.AlreadyInPlace && Action != OpenAction.Adopt && Conflicts.All(c => c.Resolution == ConflictResolution.Replace);
    internal List<WorkspaceDependency> Deps { get; } = new();
    /// <summary>Хеш файла по этому пути на момент подготовки (null — файла не было).</summary>
    internal string? ExpectedLocalSha { get; set; }
    internal string? MovedFromSha { get; set; }
}

/// <summary>План открытия версии (или обновления документов до последней версии).</summary>
public sealed class OpenPlan
{
    public string WorkRoot { get; init; } = string.Empty;
    public Guid ProjectId { get; init; }
    public Guid HeadDocumentId { get; init; }
    public int HeadVersion { get; init; }
    public string HeadRelPath { get; init; } = string.Empty;
    public string HeadCad { get; init; } = string.Empty;
    /// <summary>«Обновить до последней версии» в существующей рабочей папке.</summary>
    public bool IsUpdate { get; init; }
    /// <summary>Рабочая папка будет создана.</summary>
    public bool CreatesWorkspace { get; init; }
    public List<OpenPlanItem> Items { get; } = new();
    /// <summary>Ошибки (блокируют выполнение) и предупреждения.</summary>
    public List<PlanProblem> Problems { get; } = new();
    /// <summary>Исходные комплекты (для дерева).</summary>
    public List<KitDto> Kits { get; } = new();

    public IEnumerable<OpenConflict> Conflicts => Items.SelectMany(i => i.Conflicts);
    public bool HasConflicts => Items.Any(i => i.Conflicts.Count > 0);
    public bool CanExecute => !Problems.Any(p => p.IsBlocking);
    public int DownloadCount => Items.Where(i => i.WillWrite && !i.InCache).Select(i => i.Sha256).Distinct().Count();
    public long DownloadBytes => Items.Where(i => i.WillWrite && !i.InCache).GroupBy(i => i.Sha256).Sum(g => g.First().Size);

    /// <summary>Краткое описание плана для подтверждения.</summary>
    public string Summary
    {
        get
        {
            var write = Items.Count(i => i.WillWrite);
            var inPlace = Items.Count(i => i.Action == OpenAction.AlreadyInPlace);
            var kept = Items.Count(i => i.Action != OpenAction.AlreadyInPlace && i.Action != OpenAction.Adopt && !i.WillWrite);
            var s = $"Документов в комплекте: {Items.Count}. Будет разложено: {write}, уже на месте: {inPlace}";
            if (kept > 0) s += $", оставлено как есть: {kept}";
            s += DownloadCount > 0 ? $". Скачать: {DownloadCount} ({Texts.Bytes(DownloadBytes)})." : ". Скачивать ничего не нужно.";
            if (HasConflicts) s += $" Конфликтов: {Conflicts.Count()}.";
            return s;
        }
    }
}

/// <summary>Итог открытия.</summary>
public sealed class OpenResult : OperationResult
{
    public List<string> Placed { get; } = new();
    public List<string> Kept { get; } = new();
    /// <summary>
    /// Документы (относительные пути), в которых ссылки переписаны на пути рабочей папки: CAD (SOLIDWORKS) ищет компоненты
    /// по сохранённым абсолютным путям, а не относительно документа. Не удалось переписать — предупреждение в <see cref="OperationResult.Problems"/>.
    /// </summary>
    public List<string> ReferencesRewritten { get; } = new();
    public int Downloaded { get; set; }
    public string HeadFullPath { get; set; } = string.Empty;
    /// <summary>Отчёт проверки в CAD (если запрошена).</summary>
    public OpenReport? CadReport { get; set; }
}
