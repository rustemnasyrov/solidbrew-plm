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
}

/// <summary>Ссылка документа-исходника. Решения пользователя — <see cref="Accept"/> и <see cref="Chosen"/>.</summary>
public sealed class ImportReference
{
    /// <summary>Полный путь ссылающегося исходника.</summary>
    public string SourceDocument { get; init; } = string.Empty;
    public string SavedPath { get; init; } = string.Empty;
    public string FileName => Texts.FileNameOf(SavedPath);
    public ImportReferenceCategory Category { get; init; }
    /// <summary>Найденный файл (Found, OutsideFolders, NameOnly).</summary>
    public string? TargetPath { get; init; }
    /// <summary>Варианты для неоднозначной ссылки.</summary>
    public List<string> Candidates { get; init; } = new();
    /// <summary>Подтверждение для OutsideFolders и NameOnly (по умолчанию — нет).</summary>
    public bool Accept { get; set; }
    /// <summary>Выбранный вариант для Ambiguous (один из <see cref="Candidates"/>).</summary>
    public string? Chosen { get; set; }

    /// <summary>Требуется решение пользователя.</summary>
    public bool RequiresDecision => Category is ImportReferenceCategory.OutsideFolders or ImportReferenceCategory.NameOnly or ImportReferenceCategory.Ambiguous;

    /// <summary>Файл, на который будет указывать ссылка при текущих решениях; null — ссылка отсутствующая.</summary>
    public string? EffectiveTarget => Category switch
    {
        ImportReferenceCategory.Found => TargetPath,
        ImportReferenceCategory.OutsideFolders or ImportReferenceCategory.NameOnly => Accept ? TargetPath : null,
        ImportReferenceCategory.Ambiguous => Chosen != null && Candidates.Contains(Chosen, StringComparer.OrdinalIgnoreCase) ? Chosen : null,
        _ => null,
    };

    public string CategoryText => Category switch
    {
        ImportReferenceCategory.Found => "найден",
        ImportReferenceCategory.OutsideFolders => "за пределами указанных папок",
        ImportReferenceCategory.NameOnly => "совпадение только по имени файла",
        ImportReferenceCategory.Ambiguous => "неоднозначно: несколько файлов с таким именем",
        _ => "отсутствует",
    };
}

/// <summary>Документ-исходник, найденный при постановке на учёт.</summary>
public sealed class ImportDocument
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
    public string? Designation { get; init; }
    public string? Name { get; init; }
    public List<string> Configurations { get; init; } = new();
    public Dictionary<string, Dictionary<string, string>> Properties { get; init; } = new();
    public bool IsHead { get; init; }
    /// <summary>Найден в папке головного файла (с подпапками) или в дополнительных папках по своим ссылкам на комплект (чертёж, спецификация).</summary>
    public bool IsExtra { get; init; }
    /// <summary>Для документов из дополнительных папок: включить в постановку (по умолчанию — да).</summary>
    public bool Include { get; set; } = true;
    /// <summary>Попадает в постановку при текущих решениях.</summary>
    public bool IsIncluded { get; internal set; }
    /// <summary>Ошибка чтения (файл не читается, нет адаптера CAD).</summary>
    public string? ReadError { get; init; }
    public List<ImportReference> References { get; } = new();
}

/// <summary>
/// Отчёт постановки на учёт. Решения по ссылкам задаются в <see cref="ImportReference.Accept"/>/<see cref="ImportReference.Chosen"/>,
/// по документам доп. папок — в <see cref="ImportDocument.Include"/>; затем <see cref="Recompute"/>.
/// Временные копии удаляются <see cref="Dispose"/> (или автоматически после успешной постановки).
/// </summary>
public sealed class ImportReport : IDisposable
{
    public string HeadFile { get; init; } = string.Empty;
    public List<string> SearchFolders { get; init; } = new();
    public List<string> ExtraFolders { get; init; } = new();
    public string StagingRoot { get; init; } = string.Empty;
    /// <summary>Все найденные документы (включая неподтверждённые варианты).</summary>
    public List<ImportDocument> Documents { get; } = new();
    /// <summary>Общий корень включённых файлов; относительные пути считаются от него.</summary>
    public string? CommonRoot { get; private set; }
    public List<PlanProblem> Problems { get; } = new();
    public Dictionary<string, int> WorkingCadVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public ImportDocument Head => Documents.First(d => d.IsHead);
    public IEnumerable<ImportDocument> IncludedDocuments => Documents.Where(d => d.IsIncluded);
    /// <summary>Ссылки включённых документов.</summary>
    public IEnumerable<ImportReference> References => IncludedDocuments.SelectMany(d => d.References);
    public IEnumerable<ImportReference> ByCategory(ImportReferenceCategory category) => References.Where(r => r.Category == category);
    /// <summary>Есть отсутствующие (или неподтверждённые) ссылки — постановка только с явным разрешением.</summary>
    public bool HasMissing => Problems.Any(p => p.Code is LocalProblemCodes.MissingReference or LocalProblemCodes.Unconfirmed);
    public bool HasNewerCadVersion => IncludedDocuments.Any(d => d.IsNewerCadVersion);

    /// <summary>Можно ли ставить на учёт; <paramref name="allowMissing"/> — разрешить отсутствующие ссылки.</summary>
    public bool CanExecute(bool allowMissing) => !Problems.Any(p => p.IsBlocking && !(allowMissing && (p.Code is LocalProblemCodes.MissingReference or LocalProblemCodes.Unconfirmed)));

    internal ImportDocument? Find(string fullPath) =>
        Documents.FirstOrDefault(d => string.Equals(d.SourcePath, fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Пересчитать состав, относительные пути и проблемы по текущим решениям.</summary>
    public void Recompute()
    {
        foreach (var d in Documents) d.IsIncluded = false;
        var queue = new Queue<ImportDocument>();
        void Add(ImportDocument d)
        {
            if (d.IsIncluded) return;
            d.IsIncluded = true;
            queue.Enqueue(d);
        }
        void Drain()
        {
            while (queue.Count > 0)
            {
                var d = queue.Dequeue();
                foreach (var r in d.References)
                {
                    var t = r.EffectiveTarget;
                    if (t != null && Find(t) is { } td) Add(td);
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

        Problems.Clear();
        var included = IncludedDocuments.ToList();
        CommonRoot = CommonDirectory(included.Select(d => d.SourcePath));
        if (CommonRoot == null)
        {
            Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.NoCommonRoot,
                "Включённые файлы лежат на разных дисках — у них нет общей папки, относительные пути построить нельзя. Не подтверждайте ссылки за пределами папок или скопируйте эти файлы к проекту."));
        }
        foreach (var d in included)
        {
            d.RelPath = string.Empty;
            if (CommonRoot != null)
            {
                var rel = Path.GetRelativePath(CommonRoot, d.SourcePath).Replace('\\', '/');
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
                    case ImportReferenceCategory.Ambiguous when r.EffectiveTarget == null:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.AmbiguousReference,
                            $"{where}: по сохранённому пути ({r.SavedPath}) файла нет, а файлов с таким именем несколько: {string.Join("; ", r.Candidates)}. Выберите нужный.", d.RelPath));
                        break;
                }
            }
        }
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
