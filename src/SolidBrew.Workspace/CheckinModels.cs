using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Как найдена цель ссылки.</summary>
public enum ReferenceResolution
{
    /// <summary>Сохранённый абсолютный путь внутри рабочей папки.</summary>
    ByAbsolutePath,
    /// <summary>Сохранённый относительный путь от папки документа.</summary>
    ByRelativePath,
    /// <summary>Единственный файл с таким именем в рабочей папке.</summary>
    ByFileName,
    /// <summary>Несколько файлов с таким именем — выбрать нельзя.</summary>
    Ambiguous,
    /// <summary>Файл не найден.</summary>
    Missing,
}

/// <summary>Ссылка документа, разрешённая в рабочей папке.</summary>
public sealed class ResolvedReference
{
    /// <summary>Путь, как записан в файле.</summary>
    public string SavedPath { get; init; } = string.Empty;
    public string FileName => Texts.FileNameOf(SavedPath);
    public ReferenceResolution Resolution { get; init; }
    /// <summary>Найденный файл (относительный путь в рабочей папке).</summary>
    public string? TargetRelPath { get; init; }
    /// <summary>Для неоднозначной ссылки — все подходящие файлы.</summary>
    public List<string> Candidates { get; init; } = new();
    public bool IsResolved => TargetRelPath != null;
}

/// <summary>Кандидат на сдачу: взятый и изменённый документ, взятый неизменённый или черновик.</summary>
public sealed class CheckinCandidate
{
    public string RelPath { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string FileName => Texts.FileNameOf(RelPath);
    /// <summary>Новый документ (черновой).</summary>
    public bool IsDraft { get; init; }
    public Guid? DocumentId { get; init; }
    /// <summary>Версия, от которой шла правка (= разложенная).</summary>
    public int? BaseVersion { get; init; }
    /// <summary>Ключ нового документа в сдаче.</summary>
    public string? TempKey { get; init; }
    /// <summary>Отметка «включить в сдачу».</summary>
    public bool Include { get; set; }
    /// <summary>Изменились байты файла.</summary>
    public bool ContentChanged { get; init; }
    /// <summary>Изменился набор привязанных версий зависимостей (новая привязка — новая версия, 7.6).</summary>
    public bool ReferencesChanged { get; init; }
    public bool IsChanged => IsDraft || ContentChanged || ReferencesChanged;
    /// <summary>Почему документ в списке — для показа.</summary>
    public string Reason { get; init; } = string.Empty;
    public string Cad { get; init; } = string.Empty;
    public DocumentKind Kind { get; init; }
    public string? CadVersion { get; init; }
    public int? CadVersionMajor { get; init; }
    /// <summary>Сохранён в версии CAD новее рабочей.</summary>
    public bool IsNewerCadVersion { get; init; }
    public string? Designation { get; init; }
    public string? Name { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
    public List<string> Configurations { get; init; } = new();
    public Dictionary<string, Dictionary<string, string>> Properties { get; init; } = new();
    public List<ResolvedReference> References { get; init; } = new();
    internal List<PlanProblem> OwnProblems { get; } = new();
}

/// <summary>
/// План сдачи. Отметки <see cref="CheckinCandidate.Include"/> меняет пользователь; после изменения вызовите
/// <see cref="Revalidate"/> — список проблем и <see cref="CanExecute"/> пересчитываются.
/// </summary>
public sealed class CheckinPlan
{
    public string WorkRoot { get; init; } = string.Empty;
    public Guid ProjectId { get; init; }
    public List<CheckinCandidate> Items { get; } = new();
    /// <summary>Проблемы при текущих отметках (блокирующие и предупреждения).</summary>
    public List<PlanProblem> Problems { get; } = new();
    /// <summary>Проблемы, которые вернул сервер при последней попытке сдачи.</summary>
    public List<PlanProblem> ServerProblems { get; } = new();
    /// <summary>Рабочие версии CAD (cadId → основной номер), если известны.</summary>
    public Dictionary<string, int> WorkingCadVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    internal List<PlanProblem> GeneralProblems { get; } = new();
    internal Dictionary<string, TargetInfo> Targets { get; } = new(StringComparer.Ordinal);

    public IEnumerable<CheckinCandidate> Included => Items.Where(i => i.Include);
    public bool HasBlockingProblems => Problems.Any(p => p.IsBlocking);
    /// <summary>Среди отмеченных есть файлы, сохранённые в версии CAD новее рабочей (нужно подтверждение).</summary>
    public bool HasNewerCadVersion => Included.Any(i => i.IsNewerCadVersion);
    public bool CanExecute => !HasBlockingProblems && Included.Any();

    public string Summary
    {
        get
        {
            var inc = Included.ToList();
            var s = $"К сдаче: {inc.Count} (новых: {inc.Count(i => i.IsDraft)}, изменённых: {inc.Count(i => !i.IsDraft)}).";
            var errors = Problems.Count(p => p.IsBlocking);
            if (errors > 0) s += $" Блокирующих ошибок: {errors} — сдача невозможна.";
            var warnings = Problems.Count(p => p.Severity == ProblemSeverity.Warning);
            if (warnings > 0) s += $" Предупреждений: {warnings}.";
            return s;
        }
    }

    internal sealed class TargetInfo
    {
        public string RelPath = string.Empty;
        public WorkspaceEntry? Entry;
        public bool IsDraft;
        public bool ContentModified;
        public bool HasAdapter;
    }

    internal CheckinCandidate? CandidateFor(string relPath)
    {
        var key = SolidBrew.Core.Paths.RelPath.Key(relPath);
        return Items.FirstOrDefault(i => SolidBrew.Core.Paths.RelPath.Key(i.RelPath) == key);
    }

    /// <summary>Пересчитать проблемы с учётом отметок «включить».</summary>
    public void Revalidate()
    {
        Problems.Clear();
        Problems.AddRange(GeneralProblems);
        foreach (var c in Included)
        {
            Problems.AddRange(c.OwnProblems);
            var label = Texts.Doc(c.RelPath, c.Designation);
            if (c.IsNewerCadVersion)
            {
                WorkingCadVersions.TryGetValue(c.Cad, out var w);
                Problems.Add(new PlanProblem(ProblemSeverity.Warning, LocalProblemCodes.NewerCadVersion,
                    $"{label} сохранён в {c.CadVersion}, новее рабочей версии ({w}). Коллеги с рабочей версией CAD не смогут его открыть. Сдать можно только с подтверждением.",
                    c.RelPath, c.DocumentId));
            }

            foreach (var r in c.References)
            {
                switch (r.Resolution)
                {
                    case ReferenceResolution.Missing:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.MissingReference,
                            $"{label}: ссылка на «{r.FileName}» ({r.SavedPath}) — файл не найден в рабочей папке. Положите файл в рабочую папку (откройте его из хранилища) или исправьте ссылку в CAD.",
                            c.RelPath, c.DocumentId));
                        continue;
                    case ReferenceResolution.Ambiguous:
                        Problems.Add(new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.AmbiguousReference,
                            $"{label}: ссылка на «{r.FileName}» неоднозначна — в рабочей папке несколько файлов с таким именем: {string.Join(", ", r.Candidates)}. " +
                            "Оставьте один файл или переименуйте лишние, затем подготовьте сдачу заново.",
                            c.RelPath, c.DocumentId));
                        continue;
                }

                var key = SolidBrew.Core.Paths.RelPath.Key(r.TargetRelPath!);
                if (key == SolidBrew.Core.Paths.RelPath.Key(c.RelPath)) continue;
                var cand = CandidateFor(r.TargetRelPath!);
                if (cand != null && cand.Include) continue;
                if (!Targets.TryGetValue(key, out var t)) continue;
                var tLabel = Texts.Doc(t.RelPath, t.Entry?.Designation);
                if (t.Entry == null)
                {
                    Problems.Add(t.HasAdapter
                        ? new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.DraftNotIncluded,
                            $"{label} ссылается на новый документ {tLabel}, который не отмечен к сдаче. Отметьте его или уберите ссылку.", c.RelPath, c.DocumentId)
                        : new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.UnsupportedFile,
                            $"{label} ссылается на файл «{t.RelPath}», который не учтён в PDM и не поддерживается ни одним адаптером CAD.", c.RelPath, c.DocumentId));
                }
                else if (t.ContentModified)
                {
                    Problems.Add(cand != null
                        ? new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.ChangedNotIncluded,
                            $"{label} ссылается на {tLabel}, который изменён, но не отмечен к сдаче. Отметьте его, иначе сборка сослалась бы на версию, которой нет в хранилище.", c.RelPath, c.DocumentId)
                        : new PlanProblem(ProblemSeverity.Error, LocalProblemCodes.ChangedNotLocked,
                            $"{label} ссылается на {tLabel}, который изменён локально, но не взят в работу. Возьмите его в работу или отмените правки (откройте заново).", c.RelPath, c.DocumentId));
                }
            }
        }
    }

    /// <summary>Ссылка сдачи для разрешённой ссылки отмеченного документа.</summary>
    internal CheckinReference ToCheckinReference(ResolvedReference r)
    {
        var cr = new CheckinReference { RefPath = r.SavedPath };
        if (r.TargetRelPath == null) return cr;
        var cand = CandidateFor(r.TargetRelPath);
        if (cand != null && cand.Include)
        {
            if (cand.IsDraft) cr.TempKey = cand.TempKey;
            else cr.DocumentId = cand.DocumentId;
            return cr;
        }
        if (Targets.TryGetValue(SolidBrew.Core.Paths.RelPath.Key(r.TargetRelPath), out var t) && t.Entry != null)
        {
            cr.DocumentId = t.Entry.DocumentId;
            cr.Version = t.Entry.Version;
        }
        return cr;
    }
}

/// <summary>Итог сдачи.</summary>
public sealed class CheckinResult : OperationResult
{
    public CheckinResponse? Response { get; set; }
    /// <summary>Опубликованные версии: путь → (документ, версия).</summary>
    public List<CheckinResultItem> Published { get; } = new();
}
