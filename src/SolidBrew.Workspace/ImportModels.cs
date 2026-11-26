using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace;

/// <summary>Категория ссылки при постановке на учёт (раздел 7.1).</summary>
public enum ImportReferenceCategory
{
    /// <summary>Сохранённый путь существует и лежит внутри указанных папок.</summary>
    Found,
    /// <summary>Сохранённый путь существует, но вне указанных папок — нужно подтверждение.</summary>
    OutsideFolders,
    /// <summary>Сохранённого пути нет; ровно один файл с таким именем в папках — нужно подтверждение.</summary>
    NameOnly,
    /// <summary>Сохранённого пути нет; несколько файлов с таким именем — нужно выбрать.</summary>
    Ambiguous,
    /// <summary>Файл не найден.</summary>
    Missing,
    /// <summary>
    /// Сохранённого пути нет, но ссылающийся документ перенесён (папку переименовали или переместили), и файл найден
    /// на том же месте относительно него, что и до переноса — нужно подтверждение.
    /// </summary>
    Relocated,
}

/// <summary>Ссылка документа-исходника. Решения пользователя — <see cref="Accept"/> и <see cref="Chosen"/>.</summary>
public sealed class ImportReference
{
    /// <summary>Полный путь ссылающегося исходника.</summary>
    public string SourceDocument { get; init; } = string.Empty;
    public string SavedPath { get; init; } = string.Empty;
    public string FileName => Texts.FileNameOf(SavedPath);
    public ImportReferenceCategory Category { get; init; }
    /// <summary>Найденный файл (Found, OutsideFolders, NameOnly, Relocated).</summary>
    public string? TargetPath { get; init; }
    /// <summary>Relocated: папка, где ссылающийся документ лежал при сохранении ссылки, и где он лежит сейчас.</summary>
    public string? MovedFrom { get; init; }
    public string? MovedTo { get; init; }
    /// <summary>Варианты для неоднозначной ссылки.</summary>
    public List<string> Candidates { get; init; } = new();
    /// <summary>Подтверждение для OutsideFolders, NameOnly и Relocated (по умолчанию — нет).</summary>
    public bool Accept { get; set; }
    /// <summary>Выбранный вариант для Ambiguous (один из <see cref="Candidates"/>).</summary>
    public string? Chosen { get; set; }

    /// <summary>Требуется решение пользователя.</summary>
    public bool RequiresDecision => Category is ImportReferenceCategory.OutsideFolders or ImportReferenceCategory.NameOnly
        or ImportReferenceCategory.Relocated or ImportReferenceCategory.Ambiguous;

    /// <summary>Найденный файл можно принять одним подтверждением (без выбора из вариантов).</summary>
    public bool IsConfirmable => Category is ImportReferenceCategory.OutsideFolders or ImportReferenceCategory.NameOnly or ImportReferenceCategory.Relocated;

    /// <summary>Файл, на который будет указывать ссылка при текущих решениях; null — ссылка отсутствующая.</summary>
    public string? EffectiveTarget => Category switch
    {
        ImportReferenceCategory.Found => TargetPath,
        ImportReferenceCategory.OutsideFolders or ImportReferenceCategory.NameOnly or ImportReferenceCategory.Relocated => Accept ? TargetPath : null,
        ImportReferenceCategory.Ambiguous => Chosen != null && Candidates.Contains(Chosen, StringComparer.OrdinalIgnoreCase) ? Chosen : null,
        _ => null,
    };

    public string CategoryText => Category switch
    {
        ImportReferenceCategory.Found => "найден",
        ImportReferenceCategory.OutsideFolders => "за пределами указанных папок",
        ImportReferenceCategory.NameOnly => "совпадение только по имени файла",
        ImportReferenceCategory.Ambiguous => "неоднозначно: несколько файлов с таким именем",
        ImportReferenceCategory.Relocated => "папка перенесена: найден на прежнем месте относительно документа",
        _ => "отсутствует",
    };

    /// <summary>Одна строка для подтверждения: что найдено и почему.</summary>
    public string DecisionText => Category switch
    {
        ImportReferenceCategory.Relocated => $"{TargetPath} (документ перенесён из «{MovedFrom}» в «{MovedTo}»; ссылка записана как {SavedPath})",
        ImportReferenceCategory.NameOnly => $"{TargetPath} (по сохранённому пути {SavedPath} файла нет, совпало имя)",
        ImportReferenceCategory.OutsideFolders => $"{TargetPath} (за пределами папок поиска)",
        _ => TargetPath ?? SavedPath,
    };
}

/// <summary>Настройки постановки на учёт.</summary>
public sealed class ImportOptions
{
    /// <summary>Папка проекта для новых документов («Сборки/Узел»); пусто — корень проекта.</summary>
    public string? RelPathPrefix { get; init; }
    /// <summary>
    /// Постановка файла, оставленного ссылкой: головной документ, уже учтённый в PDM, берётся из хранилища (а не ошибка),
    /// и головной кладётся ровно в <see cref="RelPathPrefix"/> (рядом с родителем), остальные — относительно него.
    /// </summary>
    public bool AllowExistingHead { get; init; }
    /// <summary>
    /// Путь, под которым на головной документ ссылается родитель (где файл лежал, когда ссылку сохраняли). Если файл с тех пор
    /// перенесли (переименовали папку), его ссылки, которых нет по сохранённому пути, ищутся на том же месте относительно
    /// нового расположения (<see cref="ImportReferenceCategory.Relocated"/>).
    /// </summary>
    public string? HeadSavedPath { get; init; }
    /// <summary>
    /// Не дочитывать свойства в <see cref="WorkspaceService.PrepareImportAsync"/>: отчёт возвращается сразу после быстрого чтения
    /// ссылок, свойства загружаемых документов дочитывает вызывающий (окно — в фоне, пока пользователь разбирает отчёт):
    /// <see cref="WorkspaceService.ReadImportPropertiesAsync(ImportReport, ImportDocument, CancellationToken)"/> и
    /// <see cref="ImportReport.ApplyProperties"/>. <see cref="WorkspaceService.ExecuteImportAsync"/> дочитывает недостающие сам.
    /// </summary>
    public bool DeferProperties { get; init; }
    /// <summary>Сколько файлов копировать во временную папку одновременно (сетевой диск), пока CAD читает предыдущие.</summary>
    public int CopyParallelism { get; init; } = 4;
}

/// <summary>Что будет с документом при постановке на учёт.</summary>
public enum ImportDisposition
{
    /// <summary>Не попадает в постановку: на него не ведут ссылки включённых документов (или ссылка не подтверждена).</summary>
    NotUsed,
    /// <summary>Загружается в PDM новым документом.</summary>
    New,
    /// <summary>Такое же содержимое уже есть в PDM — ссылка привязывается к этой версии, файл не загружается.</summary>
    FromStorage,
    /// <summary>Галочка снята: в PDM не загружается, ссылка остаётся путём к файлу — CAD откроет его по ссылке.</summary>
    ByLink,
}

/// <summary>Документ-исходник, найденный при постановке на учёт.</summary>
public sealed partial class ImportDocument
{
    /// <summary>Полный путь исходника (не меняется).</summary>
    public string SourcePath { get; init; } = string.Empty;
    /// <summary>Временная копия, с которой работает PDM.</summary>
    public string StagingPath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(SourcePath);
    /// <summary>Относительный путь в хранилище (от общего корня включённых файлов); пусто, пока не включён.</summary>
    public string RelPath { get; internal set; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Cad { get; init; } = string.Empty;
    public DocumentKind Kind { get; init; }
    public string? CadVersion { get; init; }
    public int? CadVersionMajor { get; init; }
    public bool IsNewerCadVersion { get; init; }
    /// <summary>Обозначение и наименование — из свойств; пока свойства не прочитаны (<see cref="HasProperties"/>), — пусто.</summary>
    public string? Designation { get; internal set; }
    public string? Name { get; internal set; }
    public List<string> Configurations { get; init; } = new();
    public Dictionary<string, Dictionary<string, string>> Properties { get; internal set; } = new();
    /// <summary>
    /// Свойства прочитаны. Отчёт строится быстрым чтением (только ссылки, конфигурации и версия CAD — без открытия документа),
    /// свойства дочитываются потом и только у загружаемых документов (<see cref="NeedsProperties"/>).
    /// </summary>
    public bool HasProperties { get; internal set; }
    /// <summary>Ссылки, конфигурации и версия CAD прочитаны (иначе <see cref="ReadError"/> — ошибка копирования или чтения ссылок).</summary>
    public bool HasStructure { get; internal set; }
    /// <summary>Свойства нужно дочитать: документ загружается новым, свойства ещё не читались, ошибки чтения нет.</summary>
    public bool NeedsProperties => Disposition == ImportDisposition.New && HasStructure && !HasProperties && ReadError == null;
    /// <summary>
    /// Свойства не прочитаны, потому что в CAD открыт одноимённый документ из другой папки (CAD не открывает два файла с одним
    /// именем): его полный путь. Закрыть его и «Прочитать заново» — <see cref="WorkspaceService.RereadImportDocumentsAsync"/>.
    /// </summary>
    public string? SameNameOpenPath { get; internal set; }
    /// <summary>Название CAD, в котором открыт <see cref="SameNameOpenPath"/>.</summary>
    public string? SameNameOpenCad { get; internal set; }
    public bool IsHead { get; init; }
    /// <summary>Найден в папке головного файла (с подпапками) или в дополнительных папках по своим ссылкам на комплект (чертёж, спецификация).</summary>
    public bool IsExtra { get; init; }
    /// <summary>
    /// Где документ лежал, когда на него сохранили ссылку (путь из ссылки родителя), если это не <see cref="SourcePath"/>;
    /// по нему ищутся ссылки документа после переноса папки.
    /// </summary>
    public string? SavedLocation { get; init; }
    /// <summary>
    /// Галочка «загружать»: по умолчанию — да. Снятая у модели или чертежа — файл не загружается в PDM, ссылки на него
    /// остаются путями (<see cref="ImportDisposition.ByLink"/>). У головного документа не снимается.
    /// </summary>
    public bool Include { get; set; } = true;
    /// <summary>Попадает в постановку (новым документом или из хранилища) при текущих решениях.</summary>
    public bool IsIncluded { get; internal set; }
    /// <summary>Опубликованная версия PDM с тем же содержимым (SHA-256), если есть.</summary>
    public VersionByHashDto? Existing { get; internal set; }
    /// <summary>Что будет с документом при текущих решениях.</summary>
    public ImportDisposition Disposition { get; internal set; }
    /// <summary>Загружается новым документом.</summary>
    public bool IsNew => Disposition == ImportDisposition.New;
    /// <summary>Ошибка чтения (файл не читается, нет адаптера CAD, не прочитаны свойства).</summary>
    public string? ReadError { get; internal set; }
    public List<ImportReference> References { get; } = new();
}

/// <summary>
/// Отчёт постановки на учёт. Решения по ссылкам задаются в <see cref="ImportReference.Accept"/>/<see cref="ImportReference.Chosen"/>,
/// по документам — галочкой <see cref="ImportDocument.Include"/>; затем <see cref="Recompute"/>.
/// Документ, содержимое которого уже есть в PDM, берётся из хранилища; документ без галочки остаётся ссылкой на файл.
/// Временные копии удаляются <see cref="Dispose"/> (или автоматически после успешной постановки).
/// </summary>
public sealed partial class ImportReport : IDisposable
{
    public string HeadFile { get; init; } = string.Empty;
    /// <summary>Папка проекта, под которую кладутся новые документы («Сборки/Узел»); пусто — корень проекта.</summary>
    public string RelPathPrefix { get; init; } = string.Empty;
    /// <summary>
    /// Головной документ, уже учтённый в PDM, не ошибка, а «из хранилища» (постановка файла, оставленного ссылкой:
    /// если такое содержимое уже есть, ссылка просто привязывается к этой версии).
    /// </summary>
    public bool AllowExistingHead { get; init; }
    public List<string> SearchFolders { get; init; } = new();
    public List<string> ExtraFolders { get; init; } = new();
    public string StagingRoot { get; init; } = string.Empty;
    /// <summary>Все найденные документы (включая неподтверждённые варианты).</summary>
    public List<ImportDocument> Documents { get; } = new();
    /// <summary>Общий корень включённых файлов; относительные пути считаются от него.</summary>
    public string? CommonRoot { get; private set; }
    public List<PlanProblem> Problems { get; } = new();
    public Dictionary<string, int> WorkingCadVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Состояние обхода (папки поиска, индекс имён, копии) — для «Прочитать заново».</summary>
    internal ImportScan? Scan { get; set; }

    public ImportDocument Head => Documents.First(d => d.IsHead);
    /// <summary>Документы постановки: новые и взятые из хранилища.</summary>
    public IEnumerable<ImportDocument> IncludedDocuments => Documents.Where(d => d.IsIncluded);
    /// <summary>Загружаются новыми документами.</summary>
    public IEnumerable<ImportDocument> NewDocuments => Documents.Where(d => d.IsNew);
    /// <summary>Уже есть в PDM — ссылки привязываются к их версиям.</summary>
    public IEnumerable<ImportDocument> FromStorageDocuments => Documents.Where(d => d.Disposition == ImportDisposition.FromStorage);
    /// <summary>Галочка снята — остаются ссылками на файлы.</summary>
    public IEnumerable<ImportDocument> ByLinkDocuments => Documents.Where(d => d.Disposition == ImportDisposition.ByLink);
    /// <summary>Ссылки загружаемых документов.</summary>
    public IEnumerable<ImportReference> References => NewDocuments.SelectMany(d => d.References);
    public IEnumerable<ImportReference> ByCategory(ImportReferenceCategory category) => References.Where(r => r.Category == category);
    /// <summary>Есть отсутствующие (или неподтверждённые) ссылки — постановка только с явным разрешением.</summary>
    public bool HasMissing => Problems.Any(p => p.Code is LocalProblemCodes.MissingReference or LocalProblemCodes.Unconfirmed);
    public bool HasNewerCadVersion => NewDocuments.Any(d => d.IsNewerCadVersion);

    /// <summary>Можно ли ставить на учёт; <paramref name="allowMissing"/> — разрешить отсутствующие ссылки.</summary>
    public bool CanExecute(bool allowMissing) => !Problems.Any(p => p.IsBlocking && !(allowMissing && (p.Code is LocalProblemCodes.MissingReference or LocalProblemCodes.Unconfirmed)));

    internal ImportDocument? Find(string fullPath) =>
        Documents.FirstOrDefault(d => string.Equals(d.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Пересчитать состав, относительные пути и проблемы по текущим решениям.</summary>
    public void Recompute()
    {
        foreach (var d in Documents)
        {
            d.IsIncluded = false;
            d.Disposition = ImportDisposition.NotUsed;
        }
        var queue = new Queue<ImportDocument>();
        void Add(ImportDocument d)
        {
            if (d.IsIncluded) return;
            d.IsIncluded = true;
            var fromStorage = d.Existing != null && (!d.IsHead || AllowExistingHead);
            d.Disposition = fromStorage ? ImportDisposition.FromStorage : ImportDisposition.New;
            // Зависимости документа из хранилища уже зафиксированы в PDM его версией — дальше не идём.
            if (!fromStorage) queue.Enqueue(d);
        }
        void Drain()
        {
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                foreach (var r in d.References)
                {
                    var t = r.EffectiveTarget;
                    if (t == null || Find(t) is not { } td || td == d) continue;
                    if (td.Include || td.IsHead) Add(td);
                    else if (!td.IsIncluded) td.Disposition = ImportDisposition.ByLink;
                }
            }
        }

        Add(Head);
        Drain();
        bool changed;
        do
        {
            changed = false;
            foreach (var extra in Documents.Where(d => d.IsExtra && d.Include && !d.IsIncluded).ToList())
            {
                if (extra.References.Any(r => r.EffectiveTarget != null && Find(r.EffectiveTarget) is { IsIncluded: true }))
                {
                    Add(extra);
                    Drain();
                    changed = true;
                }
            }
        } while (changed);
        // Чертёж с комплекта, с которого сняли галочку, — тоже «по ссылке» (в постановку не попадёт).
        foreach (var extra in Documents.Where(d => d.IsExtra && !d.Include && !d.IsIncluded))
        {
            if (extra.References.Any(r => r.EffectiveTarget != null && Find(r.EffectiveTarget) is { IsIncluded: true }))
                extra.Disposition = ImportDisposition.ByLink;
        }

        Problems.Clear();
        if (Head.Existing is { } headExisting && !AllowExistingHead)
        {
            Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.AlreadyInStorage,
                $"Головной документ «{Head.FileName}» уже учтён в PDM: {Describe(headExisting)}. Откройте его из PDM, а не ставьте на учёт повторно."));
        }
        var included = NewDocuments.ToList();
        var byLink = ByLinkDocuments.ToList();
        if (byLink.Count > 0)
        {
            Problems.Add(new PlanProblem(ProblemSeverity.Info, LocalProblemCodes.ByLink,
                $"Не загружаются в PDM (галочка снята), останутся ссылками на файлы — CAD откроет их по ссылке: {byLink.Count}. " +
                string.Join(", ", byLink.Take(5).Select(d => $"«{d.FileName}»")) + (byLink.Count > 5 ? " …" : string.Empty)));
        }
        foreach (var d in Documents.Where(d => !d.IsNew)) d.RelPath = string.Empty;
        CommonRoot = included.Count == 0 ? null : CommonDirectory(included.Select(d => d.SourcePath));
        // Файл по ссылке: головной — ровно рядом с родителем (в RelPathPrefix), остальные — относительно головного.
        var headDir = AllowExistingHead ? Path.GetDirectoryName(Path.GetFullPath(HeadFile)) : null;
        var noRoot = headDir != null
            ? included.Any(d => Path.IsPathRooted(Path.GetRelativePath(headDir, d.SourcePath)))
            : CommonRoot == null && included.Count > 0;
        if (noRoot)
        {
            Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NoCommonRoot,
                "Включённые файлы лежат на разных дисках — у них нет общей папки, относительные пути построить нельзя. Не подтверждайте ссылки за пределами папок или скопируйте эти файлы к проекту."));
        }
        foreach (var d in included)
        {
            d.RelPath = string.Empty;
            string? rel = null;
            if (headDir != null)
            {
                var fromHead = Path.GetRelativePath(headDir, d.SourcePath);
                if (!Path.IsPathRooted(fromHead)) rel = Anchor(RelPathPrefix, fromHead);
            }
            else if (CommonRoot != null)
            {
                rel = Path.GetRelativePath(CommonRoot, d.SourcePath).Replace('\\', '/');
                if (RelPathPrefix.Length > 0) rel = RelPathPrefix.TrimEnd('/') + "/" + rel;
            }
            if (rel != null)
            {
                if (SolidBrew.Core.Paths.RelPath.TryNormalize(rel, out var norm, out var err)) d.RelPath = norm;
                else Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.InvalidPath, err, rel));
            }
            if (d.ReadError != null)
                Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.ReadError, d.ReadError, d.RelPath));
            if (d.IsNewerCadVersion)
            {
                WorkingCadVersions.TryGetValue(d.Cad, out var w);
                Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.NewerCadVersion,
                    $"«{d.FileName}» сохранён в {d.CadVersion}, новее рабочей версии ({w}). Постановка — только с подтверждением.", d.RelPath));
            }
            foreach (var r in d.References)
            {
                var where = $"«{d.FileName}» → «{r.FileName}»";
                switch (r.Category)
                {
                    case ImportReferenceCategory.Missing:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.MissingReference,
                            $"{where}: файл не найден ({r.SavedPath}). Добавьте папку с ним в поиск или разрешите постановку с отсутствующими ссылками.", d.RelPath));
                        break;
                    case ImportReferenceCategory.OutsideFolders when !r.Accept:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Unconfirmed,
                            $"{where}: файл найден за пределами указанных папок ({r.TargetPath}). Подтвердите его или добавьте папку в поиск.", d.RelPath));
                        break;
                    case ImportReferenceCategory.NameOnly when !r.Accept:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Unconfirmed,
                            $"{where}: по сохранённому пути ({r.SavedPath}) файла нет, совпадение только по имени — {r.TargetPath}. Подтвердите, что это тот файл.", d.RelPath));
                        break;
                    case ImportReferenceCategory.Relocated when !r.Accept:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.Unconfirmed,
                            $"{where}: по сохранённому пути ({r.SavedPath}) файла нет; документ перенесён из «{r.MovedFrom}» в «{r.MovedTo}», " +
                            $"и на прежнем месте относительно него найден {r.TargetPath}. Подтвердите, что это тот файл.", d.RelPath));
                        break;
                    case ImportReferenceCategory.Ambiguous when r.EffectiveTarget == null:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.AmbiguousReference,
                            $"{where}: по сохранённому пути ({r.SavedPath}) файла нет, а файлов с таким именем несколько: {string.Join("; ", r.Candidates)}. Выберите нужный.", d.RelPath));
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Путь в проекте для файла, лежащего по <paramref name="relativeToHead"/> от головного (головной — в <paramref name="prefix"/>):
    /// структура папок повторяет диск; подъём выше корня проекта обрезается («../Models/X» при пустом префиксе — «Models/X»).
    /// </summary>
    internal static string Anchor(string prefix, string relativeToHead)
    {
        var parts = new List<string>(prefix.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in relativeToHead.Split('\\', '/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    /// <summary>
    /// Подтвердить все ссылки, где найден ровно один файл (за пределами папок, только по имени, после переноса папки),
    /// включая ссылки документов, которые попадают в постановку благодаря этим подтверждениям. Возвращает подтверждённые
    /// ссылки (снять подтверждение — <c>Accept = false</c> и <see cref="Recompute"/>).
    /// </summary>
    public List<ImportReference> AcceptConfirmable()
    {
        var accepted = new List<ImportReference>();
        while (true)
        {
            Recompute();
            var next = References.Where(r => r.IsConfirmable && !r.Accept).ToList();
            if (next.Count == 0) return accepted;
            foreach (var r in next) r.Accept = true;
            accepted.AddRange(next);
        }
    }

    /// <summary>«АБВ.001 v3, проект „Изделие“ (Детали/Корпус.sldprt)».</summary>
    public static string Describe(VersionByHashDto v)
    {
        var d = v.Document;
        var name = string.IsNullOrWhiteSpace(d.Designation) ? d.FileName : d.Designation;
        return $"{name} v{v.Version}, проект «{d.ProjectName}» ({d.RelPath})";
    }

    internal static string? CommonDirectory(IEnumerable<string> files)
    {
        string[]? common = null;
        foreach (var f in files)
        {
            var parts = Path.GetDirectoryName(Path.GetFullPath(f))!.TrimEnd('\\').Split('\\');
            if (common == null)
            {
                common = parts;
                continue;
            }
            var n = 0;
            while (n < common.Length && n < parts.Length && string.Equals(common[n], parts[n], StringComparison.OrdinalIgnoreCase)) n++;
            common = common.Take(n).ToArray();
        }
        if (common == null || common.Length == 0 || common.All(p => p.Length == 0)) return null;
        var path = string.Join("\\", common);
        if (path.EndsWith(":", StringComparison.Ordinal)) path += "\\";
        return path.StartsWith("\\\\", StringComparison.Ordinal) && common.Length < 4 ? null : path;
    }

    public void Dispose() => FileOps.TryDeleteDirectory(StagingRoot);
}

/// <summary>Итог постановки на учёт.</summary>
public sealed class ImportResult : OperationResult
{
    public CheckinResponse? Response { get; set; }
    public Guid HeadDocumentId { get; set; }
    public int HeadVersion { get; set; }
}
