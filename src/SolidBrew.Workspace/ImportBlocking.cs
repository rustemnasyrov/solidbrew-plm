using SolidBrew.Core.Cad;

namespace SolidBrew.Workspace;

/// <summary>Где в окне постановки разбирается причина: вкладка отчёта или поле проекта.</summary>
public enum ImportReportTab
{
    Documents,
    Found,
    OutsideFolders,
    NameOnly,
    Relocated,
    Ambiguous,
    Missing,
    /// <summary>Не вкладка: выбор проекта внизу окна.</summary>
    Project,
}

/// <summary>Коды причин, которые не являются проблемами отчёта (<see cref="ImportReport.Problems"/>).</summary>
public static class ImportBlockingCodes
{
    /// <summary>Не выбран проект.</summary>
    public const string NoProject = "local.noProject";
    /// <summary>Свойства загружаемых документов ещё читаются.</summary>
    public const string PropertiesPending = "local.propertiesPending";
    /// <summary>Свойства не прочитаны: в CAD открыт одноимённый документ из другой папки (код проблемы — <see cref="LocalProblemCodes.ReadError"/>).</summary>
    public const string SameNameOpen = "local.sameNameOpen";
}

/// <summary>
/// Причина, по которой нельзя поставить на учёт, сгруппированная для пользователя: сколько, что это, что сделать и где.
/// Одни и те же причины показывают окно (рядом с кнопкой «Поставить на учёт») и CLI.
/// </summary>
public sealed class ImportBlockingReason
{
    /// <summary><see cref="LocalProblemCodes"/> или <see cref="ImportBlockingCodes"/>.</summary>
    public string Code { get; init; } = string.Empty;
    public int Count { get; init; }
    /// <summary>Что мешает: «1 файл не прочитан (в SOLIDWORKS открыт одноимённый документ: …)».</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>Что сделать в окне: «закройте его и нажмите «Прочитать заново» или снимите у него «Загружать»».</summary>
    public string Action { get; init; } = string.Empty;
    /// <summary>То же для командной строки (ключи sbpdm); пусто — как <see cref="Action"/>.</summary>
    public string CliAction { get; init; } = string.Empty;
    /// <summary>Где разбирать: вкладка окна.</summary>
    public ImportReportTab Tab { get; init; }
    /// <summary>Причину снимает «Разрешить постановку с отсутствующими и неподтверждёнными ссылками».</summary>
    public bool AllowMissingClears { get; init; }
    /// <summary>Для причины <see cref="ImportBlockingCodes.SameNameOpen"/>: можно перечитать только эти файлы.</summary>
    public bool CanReread { get; init; }
    public List<ImportDocument> Documents { get; init; } = new();
    public List<ImportReference> References { get; init; } = new();

    /// <summary>«текст — что сделать».</summary>
    public override string ToString() => Action.Length > 0 ? $"{Text} — {Action}" : Text;
}

/// <summary>Свойства одного документа, прочитанные после отчёта (или ошибка). Применить — <see cref="ImportReport.ApplyProperties"/>.</summary>
public sealed record ImportPropertiesRead(
    ImportDocument Document,
    Dictionary<string, Dictionary<string, string>>? Properties,
    string? Error,
    string? SameNameOpenPath,
    string? SameNameOpenCad,
    TimeSpan Elapsed);

public sealed partial class ImportReport
{
    /// <summary>Загружаемые документы, свойства которых ещё не прочитаны.</summary>
    public IEnumerable<ImportDocument> PendingProperties => Documents.Where(d => d.NeedsProperties);

    /// <summary>Свойства ещё не дочитаны у части загружаемых документов.</summary>
    public bool HasPendingProperties => Documents.Any(d => d.NeedsProperties);

    /// <summary>Следующий документ для чтения свойств: головной — первым, дальше — в порядке обхода ссылок.</summary>
    public ImportDocument? NextForProperties() =>
        Documents.FirstOrDefault(d => d.IsHead && d.NeedsProperties) ?? Documents.FirstOrDefault(d => d.NeedsProperties);

    /// <summary>
    /// Применить прочитанные свойства (или ошибку их чтения) и пересчитать отчёт. Ссылки, убранные через PDM (свойство
    /// <see cref="DocumentProperties.RemovedReferencesKey"/>), из документа удаляются. Документ, которого уже нет в отчёте
    /// (перечитан заново), пропускается.
    /// </summary>
    public void ApplyProperties(ImportPropertiesRead read)
    {
        if (read == null) throw new ArgumentNullException(nameof(read));
        var d = read.Document;
        if (!Documents.Contains(d)) return;
        if (read.Properties != null)
        {
            d.SetProperties(read.Properties);
            var removed = DocumentProperties.RemovedReferences(read.Properties);
            if (removed.Count > 0) d.References.RemoveAll(r => removed.Contains(r.FileName, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            d.ReadError = read.Error ?? $"Не удалось прочитать свойства «{d.SourcePath}».";
            d.SameNameOpenPath = read.SameNameOpenPath;
            d.SameNameOpenCad = read.SameNameOpenCad;
        }
        Recompute();
    }

    /// <summary>«Свойства 13 из 140, осталось ≈ 01:20: «Корпус.sldprt»» (оценка — по среднему времени прочитанных).</summary>
    public static string PropertiesProgressText(int done, int total, TimeSpan elapsed, string? currentFile)
    {
        var text = $"Свойства {Math.Min(done + 1, Math.Max(total, 1))} из {Math.Max(total, 1)}";
        if (done > 0 && total > done)
        {
            var left = TimeSpan.FromTicks(elapsed.Ticks / done * (total - done));
            text += $", осталось ≈ {(int)left.TotalMinutes:00}:{left.Seconds:00}";
        }
        return string.IsNullOrEmpty(currentFile) ? text : $"{text}: «{currentFile}»";
    }

    /// <summary>
    /// Почему нельзя поставить на учёт при текущих решениях, по группам: что мешает, сколько, что сделать и где.
    /// <paramref name="allowMissing"/> — отмечено «Разрешить постановку с отсутствующими…» (тогда отсутствующие и неподтверждённые
    /// ссылки не мешают); <paramref name="projectSelected"/> — выбран проект; <paramref name="includePending"/> — считать причиной
    /// свойства, которые ещё читаются. Пустой список — можно ставить на учёт.
    /// </summary>
    public List<ImportBlockingReason> BlockingReasons(bool allowMissing, bool projectSelected = true, bool includePending = true)
    {
        var result = new List<ImportBlockingReason>();
        var newDocs = NewDocuments.ToList();

        // Ошибки чтения: отдельно — «открыт одноимённый документ» (его снимает закрытие документа и «Прочитать заново»).
        var failed = newDocs.Where(d => d.ReadError != null).ToList();
        var sameName = failed.Where(d => d.SameNameOpenPath != null).ToList();
        if (sameName.Count > 0)
        {
            var cad = sameName[0].SameNameOpenCad ?? "CAD";
            var paths = sameName.Select(d => d.SameNameOpenPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var them = sameName.Count == 1 ? "него" : "них";
            result.Add(new ImportBlockingReason
            {
                Code = ImportBlockingCodes.SameNameOpen,
                Count = sameName.Count,
                Text = Plural(sameName.Count, "файл не прочитан", "файла не прочитаны", "файлов не прочитано") + (paths.Count == 1
                    ? $" (в {cad} открыт одноимённый документ из другой папки: {paths[0]})"
                    : $" (в {cad} открыты одноимённые документы из другой папки: {paths.Count}, например {paths[0]})"),
                Action = paths.Count == 1
                    ? $"закройте его в {cad} и нажмите «Прочитать заново» или снимите у {them} «Загружать»"
                    : $"закройте их в {cad} (или сборку, в которую они входят) и нажмите «Прочитать заново» или снимите у {them} «Загружать»",
                CliAction = $"закройте {(paths.Count == 1 ? "его" : "их")} в {cad} и повторите или оставьте ссылкой: --by-link <файл>",
                Tab = ImportReportTab.Documents,
                CanReread = true,
                Documents = sameName,
            });
        }
        var otherFailed = failed.Except(sameName).ToList();
        if (otherFailed.Count > 0)
        {
            var them = otherFailed.Count == 1 ? "него" : "них";
            result.Add(new ImportBlockingReason
            {
                Code = LocalProblemCodes.ReadError,
                Count = otherFailed.Count,
                Text = Plural(otherFailed.Count, "файл не прочитан", "файла не прочитаны", "файлов не прочитано") + (otherFailed.Count == 1 ? $" («{otherFailed[0].FileName}»)" : string.Empty),
                Action = $"исправьте причину (столбец «Ошибка чтения») и нажмите «Прочитать заново» или снимите у {them} «Загружать»",
                CliAction = "исправьте причину и повторите или оставьте ссылкой: --by-link <файл>",
                Tab = ImportReportTab.Documents,
                CanReread = true,
                Documents = otherFailed,
            });
        }

        if (Problems.Any(p => p.Code == LocalProblemCodes.AlreadyInStorage))
        {
            result.Add(new ImportBlockingReason
            {
                Code = LocalProblemCodes.AlreadyInStorage,
                Count = 1,
                Text = $"головной документ «{Head.FileName}» уже учтён в PDM",
                Action = "откройте его из PDM вместо повторной постановки",
                Tab = ImportReportTab.Documents,
                Documents = new List<ImportDocument> { Head },
            });
        }

        var refs = newDocs.SelectMany(d => d.References).ToList();
        var ambiguous = refs.Where(r => r.Category == ImportReferenceCategory.Ambiguous && r.EffectiveTarget == null).ToList();
        if (ambiguous.Count > 0)
        {
            result.Add(new ImportBlockingReason
            {
                Code = LocalProblemCodes.AmbiguousReference,
                Count = ambiguous.Count,
                Text = Plural(ambiguous.Count, "неоднозначная ссылка", "неоднозначные ссылки", "неоднозначных ссылок"),
                Action = "выберите вариант на вкладке «Неоднозначно»",
                CliAction = "выберите вариант: --choose <ссылка>=<путь>",
                Tab = ImportReportTab.Ambiguous,
                References = ambiguous,
            });
        }

        if (!allowMissing)
        {
            void Unconfirmed(ImportReferenceCategory category, string one, string few, string many, string tab, ImportReportTab where, string flag)
            {
                var list = refs.Where(r => r.Category == category && !r.Accept).ToList();
                if (list.Count == 0) return;
                result.Add(new ImportBlockingReason
                {
                    Code = LocalProblemCodes.Unconfirmed,
                    Count = list.Count,
                    Text = Plural(list.Count, one, few, many),
                    Action = $"подтвердите их на вкладке «{tab}» или отметьте «Разрешить…»",
                    CliAction = $"подтвердите: {flag} или --allow-missing",
                    Tab = where,
                    AllowMissingClears = true,
                    References = list,
                });
            }
            Unconfirmed(ImportReferenceCategory.OutsideFolders, "ссылка за пределами папок не подтверждена", "ссылки за пределами папок не подтверждены",
                "ссылок за пределами папок не подтверждено", "За пределами папок", ImportReportTab.OutsideFolders, "--accept-outside (или добавьте папку --extra)");
            Unconfirmed(ImportReferenceCategory.NameOnly, "ссылка, совпавшая только по имени, не подтверждена", "ссылки, совпавшие только по имени, не подтверждены",
                "ссылок, совпавших только по имени, не подтверждено", "Совпали только по имени", ImportReportTab.NameOnly, "--accept-name-matches");
            Unconfirmed(ImportReferenceCategory.Relocated, "ссылка после переноса папки не подтверждена", "ссылки после переноса папки не подтверждены",
                "ссылок после переноса папки не подтверждено", "Папка перенесена", ImportReportTab.Relocated, "--accept-relocated");

            var missing = refs.Where(r => r.Category == ImportReferenceCategory.Missing).ToList();
            if (missing.Count > 0)
            {
                result.Add(new ImportBlockingReason
                {
                    Code = LocalProblemCodes.MissingReference,
                    Count = missing.Count,
                    Text = Plural(missing.Count, "отсутствующая ссылка", "отсутствующие ссылки", "отсутствующих ссылок"),
                    Action = "найдите файлы (добавьте их папку в поиск и прочитайте ссылки заново) или отметьте «Разрешить…»",
                    CliAction = "добавьте папку с файлами: --extra <папка> или разрешите: --allow-missing",
                    Tab = ImportReportTab.Missing,
                    AllowMissingClears = true,
                    References = missing,
                });
            }
        }

        var noRoot = Problems.FirstOrDefault(p => p.Code == LocalProblemCodes.NoCommonRoot);
        if (noRoot != null)
        {
            result.Add(new ImportBlockingReason
            {
                Code = LocalProblemCodes.NoCommonRoot,
                Count = 1,
                Text = "включённые файлы лежат на разных дисках — нет общей папки",
                Action = "не подтверждайте ссылки за пределами папок (или снимите «Загружать» у файлов с другого диска) либо скопируйте их к проекту",
                CliAction = "не подтверждайте ссылки за пределами папок (--by-link <файл>) либо скопируйте их к проекту",
                Tab = ImportReportTab.OutsideFolders,
            });
        }

        var invalid = Problems.Where(p => p.Code == LocalProblemCodes.InvalidPath).ToList();
        if (invalid.Count > 0)
        {
            result.Add(new ImportBlockingReason
            {
                Code = LocalProblemCodes.InvalidPath,
                Count = invalid.Count,
                Text = Plural(invalid.Count, "недопустимый путь в хранилище", "недопустимых пути в хранилище", "недопустимых путей в хранилище") + ": " + invalid[0].Message,
                Action = "переименуйте файл или папку и прочитайте ссылки заново",
                Tab = ImportReportTab.Documents,
                Documents = newDocs.Where(d => d.RelPath.Length == 0).ToList(),
            });
        }

        // Прочие блокирующие проблемы (на будущее) — их собственным текстом.
        var known = new[]
        {
            LocalProblemCodes.ReadError, LocalProblemCodes.AlreadyInStorage, LocalProblemCodes.AmbiguousReference, LocalProblemCodes.Unconfirmed,
            LocalProblemCodes.MissingReference, LocalProblemCodes.NoCommonRoot, LocalProblemCodes.InvalidPath,
        };
        foreach (var g in Problems.Where(p => p.IsBlocking && !known.Contains(p.Code)).GroupBy(p => p.Code))
        {
            result.Add(new ImportBlockingReason { Code = g.Key, Count = g.Count(), Text = g.First().Message, Tab = ImportReportTab.Documents });
        }

        if (includePending)
        {
            var pending = PendingProperties.ToList();
            if (pending.Count > 0)
            {
                var total = newDocs.Count(d => d.HasStructure && d.ReadError == null);
                result.Add(new ImportBlockingReason
                {
                    Code = ImportBlockingCodes.PropertiesPending,
                    Count = pending.Count,
                    Text = $"свойства прочитаны у {total - pending.Count} из {total} загружаемых документов",
                    Action = "дождитесь окончания чтения",
                    Tab = ImportReportTab.Documents,
                    Documents = pending,
                });
            }
        }

        if (!projectSelected)
        {
            result.Add(new ImportBlockingReason
            {
                Code = ImportBlockingCodes.NoProject,
                Count = 1,
                Text = "не выбран проект",
                Action = "выберите проект",
                CliAction = "укажите проект: --project <имя или ID>",
                Tab = ImportReportTab.Project,
            });
        }
        return result;
    }

    /// <summary>«Нельзя поставить на учёт: 1 файл не прочитан (…) — закройте…; 12 отсутствующих ссылок — найдите…».</summary>
    public static string BlockingSummary(IEnumerable<ImportBlockingReason> reasons, bool cli = false)
    {
        var parts = reasons.Select(r =>
        {
            var action = cli && r.CliAction.Length > 0 ? r.CliAction : r.Action;
            return action.Length > 0 ? $"{r.Text} — {action}" : r.Text;
        }).ToList();
        return parts.Count == 0 ? string.Empty : "Нельзя поставить на учёт: " + string.Join("; ", parts) + ".";
    }

    /// <summary>«1 файл», «2 файла», «5 файлов» — число и форма слова.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = Math.Abs(n) % 100;
        var mod10 = mod100 % 10;
        var word = mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }
}

public sealed partial class ImportDocument
{
    internal void SetProperties(Dictionary<string, Dictionary<string, string>> properties)
    {
        Properties = properties ?? new Dictionary<string, Dictionary<string, string>>();
        Designation = DocumentProperties.Designation(Properties);
        Name = DocumentProperties.Name(Properties);
        HasProperties = true;
    }

    /// <summary>Сбросить ошибку чтения свойств — свойства прочитаются заново.</summary>
    internal void ClearPropertiesError()
    {
        ReadError = null;
        SameNameOpenPath = null;
        SameNameOpenCad = null;
        HasProperties = false;
    }
}
