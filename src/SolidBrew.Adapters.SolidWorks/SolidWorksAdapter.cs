using System.Diagnostics;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>Как открывать сборку, чтобы прочитать её свойства.</summary>
public enum SwAssemblyReadMode
{
    /// <summary>Выборочное открытие без компонентов (<c>OpenDoc7</c>, <c>Selective</c> с пустым списком).</summary>
    Selective,
    /// <summary>Облегчённое открытие (<c>LoadLightweight</c>).</summary>
    Lightweight,
    /// <summary>Обычное открытие с загрузкой всех компонентов.</summary>
    Full,
}

public sealed class SolidWorksOptions
{
    /// <summary>Если SOLIDWORKS не запущен — запустить новый экземпляр (иначе ошибка).</summary>
    public bool StartIfNotRunning { get; set; }

    /// <summary>Как открывать сборки для чтения свойств.</summary>
    public SwAssemblyReadMode AssemblyReadMode { get; set; } = SwAssemblyReadMode.Selective;

    /// <summary>
    /// Параметр <c>Searchflag</c> метода <c>GetDocumentDependencies2</c>: искать ссылки по правилам поиска SOLIDWORKS.
    /// По умолчанию выключен: тогда возвращаются пути, записанные в файле. С поиском ответ зависит от того, что уже
    /// загружено в SOLIDWORKS (проверено 25.11.2026 на 2026: устаревший путь чертежа то находился через модель,
    /// оставшуюся в памяти от предыдущего чтения, то нет). Ссылки разрешает клиент — по папке документа и по имени.
    /// </summary>
    public bool SearchDependencies { get; set; }

    /// <summary>
    /// Если документ нельзя открыть из-за одноимённого документа из другой папки — не бросать ошибку,
    /// а вернуть сведения без свойств (<see cref="SwReadResult.PropertiesSkipped"/>). Для сканирования.
    /// </summary>
    public bool SkipPropertiesIfBlocked { get; set; }
}

/// <summary>Подробный результат чтения (для диагностики и прототипа).</summary>
public sealed class SwReadResult
{
    public CadDocumentInfo Info { get; set; } = new CadDocumentInfo();
    /// <summary>Что вернул <c>VersionHistory</c>.</summary>
    public List<string> VersionHistory { get; set; } = new List<string>();
    /// <summary>Сырые пары имя/путь из <c>GetDocumentDependencies2</c>.</summary>
    public List<string> RawDependencies { get; set; } = new List<string>();
    /// <summary>Отброшенные виртуальные компоненты.</summary>
    public List<string> VirtualSkipped { get; set; } = new List<string>();
    /// <summary>«already-open» — документ уже был открыт; «opened» — открыт и закрыт адаптером.</summary>
    public string OpenMode { get; set; } = string.Empty;
    /// <summary>Почему свойства не прочитаны (только при <see cref="SolidWorksOptions.SkipPropertiesIfBlocked"/>).</summary>
    public string? PropertiesSkipped { get; set; }
    public int OpenErrors { get; set; }
    public int OpenWarnings { get; set; }
    public double DependenciesMs { get; set; }
    public double OpenMs { get; set; }
    public double PropertiesMs { get; set; }
    public double TotalMs { get; set; }
}

/// <summary>Подробный результат открытия с проверкой.</summary>
public sealed class SwOpenDiagnostics
{
    public OpenReport Report { get; set; } = new OpenReport();
    public bool WasAlreadyOpen { get; set; }
    public bool Opened { get; set; }
    public int OpenErrors { get; set; }
    public int OpenWarnings { get; set; }
    public List<string> OpenWarningNames { get; set; } = new List<string>();
    public double OpenMs { get; set; }
    /// <summary>Подавленные компоненты по конфигурациям: «конфигурация: имя (путь)».</summary>
    public List<string> Suppressed { get; set; } = new List<string>();
    /// <summary>Документы комплекта, которые после открытия находятся в памяти SOLIDWORKS, с реальными путями.</summary>
    public List<string> LoadedDocuments { get; set; } = new List<string>();
    /// <summary>Имена файлов дерева ссылок (по сохранённым путям).</summary>
    public List<string> TreeFileNames { get; set; } = new List<string>();
}

/// <summary>
/// Адаптер SOLIDWORKS через COM-автоматизацию запущенной программы (позднее связывание, без interop-сборок).
/// Все вызовы идут на одном выделенном потоке STA; методы можно вызывать из любых потоков.
/// </summary>
public sealed class SolidWorksAdapter : ICadAdapter, IDisposable
{
    public const string ProgId = "SldWorks.Application";

    private static readonly string[] SwExtensions = { ".sldprt", ".sldasm", ".slddrw" };

    private readonly StaWorker _sta = new StaWorker("SolidWorks STA");
    private readonly SolidWorksOptions _options;
    private SwSession? _session;

    public SolidWorksAdapter(SolidWorksOptions? options = null)
    {
        _options = options ?? new SolidWorksOptions();
    }

    public SolidWorksOptions Options => _options;

    public string CadId => "solidworks";

    public string DisplayName => "SOLIDWORKS";

    public IReadOnlyCollection<string> Extensions => SwExtensions;

    public bool IsDocument(string path) => SwConst.DocTypeOf(path) != SwConst.DocNone;

    public DocumentKind KindOf(string path) => SwConst.DocTypeOf(path) switch
    {
        SwConst.DocPart => DocumentKind.Part,
        SwConst.DocAssembly => DocumentKind.Assembly,
        SwConst.DocDrawing => DocumentKind.Drawing,
        _ => DocumentKind.Other,
    };

    /// <summary>Выполнить действие с SOLIDWORKS на потоке адаптера (подключается при первом вызове).</summary>
    public Task<T> RunAsync<T>(Func<SwSession, T> action, CancellationToken ct = default)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        return _sta.Run(() =>
        {
            var session = Connect();
            try
            {
                return action(session);
            }
            catch (SwDisconnectedException)
            {
                _session = null;
                throw;
            }
        }, ct);
    }

    private SwSession Connect()
    {
        if (_session != null) return _session;

        var raw = ComNative.TryGetActiveObject(ProgId, out var hr);
        if (raw == null)
        {
            if (!_options.StartIfNotRunning)
            {
                var hint = hr == ComNative.MK_E_UNAVAILABLE
                    ? " Если SOLIDWORKS открыт, он запущен в другом сеансе Windows или с другими правами (например, «от имени администратора»): запустите SolidBrew так же."
                    : $" (код 0x{hr:X8})";
                throw new CadException("SOLIDWORKS не запущен. Запустите SOLIDWORKS и повторите." + hint);
            }
            var type = Type.GetTypeFromProgID(ProgId)
                ?? throw new CadException("SOLIDWORKS не установлен на этом компьютере (не зарегистрирован «SldWorks.Application»).");
            try
            {
                raw = Activator.CreateInstance(type)
                    ?? throw new CadException("Не удалось запустить SOLIDWORKS.");
            }
            catch (Exception ex) when (ex is not CadException)
            {
                throw new CadException("Не удалось запустить SOLIDWORKS: " + ex.Message, ex);
            }
            var started = new ComObj(raw);
            started.Set("Visible", true);
        }
        _session = new SwSession(new ComObj(raw));
        return _session;
    }

    // ------------------------------------------------------------------ Чтение

    public async Task<CadDocumentInfo> ReadAsync(string path, CancellationToken ct = default) =>
        (await ReadDetailedAsync(path, ct).ConfigureAwait(false)).Info;

    /// <summary>Только ссылки (<c>GetDocumentDependencies2</c>): документ не открывается, ~13 мс на файл.</summary>
    public Task<List<CadReference>> ReadReferencesAsync(string path, CancellationToken ct = default)
    {
        var full = CheckFile(path);
        return RunAsync(s => ReadReferences(s, full, traverse: false, null, null), ct);
    }

    /// <summary>Чтение с подробностями: сырые ответы API и время этапов.</summary>
    public Task<SwReadResult> ReadDetailedAsync(string path, CancellationToken ct = default)
    {
        var full = CheckFile(path);
        return RunAsync(s => Read(s, full), ct);
    }

    private SwReadResult Read(SwSession s, string full)
    {
        var total = Stopwatch.StartNew();
        var result = new SwReadResult();
        var info = result.Info;
        info.Path = full;
        info.Kind = KindOf(full);

        var sw = Stopwatch.StartNew();
        info.References = ReadReferences(s, full, traverse: false, result.RawDependencies, result.VirtualSkipped);
        result.DependenciesMs = sw.Elapsed.TotalMilliseconds;

        info.Configurations = ComObj.AsStrings(s.App.Call("GetConfigurationNames", full));
        result.VersionHistory = ComObj.AsStrings(s.App.Call("VersionHistory", full));
        info.CadVersionMajor = SwVersion.LastSavedYear(result.VersionHistory);
        info.CadVersion = SwVersion.Display(info.CadVersionMajor);

        // Свойства читаются только из открытого документа.
        var open = s.FindOpenByPath(full);
        if (open != null)
        {
            result.OpenMode = "already-open";
            sw.Restart();
            info.Properties = ReadProperties(open.Doc, info.Configurations);
            result.PropertiesMs = sw.Elapsed.TotalMilliseconds;
        }
        else
        {
            // Одноимённый документ из другой папки не ищем заранее (перебор всех документов дорог):
            // SOLIDWORKS сам откажет с кодом swFileWithSameTitleAlreadyOpen, тогда выясняем, какой именно.
            result.OpenMode = "opened";
            sw.Restart();
            ComObj doc;
            int errors, warnings;
            var type = SwConst.DocTypeOf(full);
            try
            {
                using (s.HideNewDocuments(type))
                {
                    doc = OpenForRead(s, full, type, out errors, out warnings);
                }
            }
            catch (SwOpenException ex) when (ex.SameTitleOpen)
            {
                var blocked = SameNameError(s, full) ?? ex;
                if (!_options.SkipPropertiesIfBlocked) throw blocked;
                result.OpenMode = "skipped";
                result.PropertiesSkipped = blocked.Message;
                result.TotalMs = total.Elapsed.TotalMilliseconds;
                return result;
            }
            result.OpenMs = sw.Elapsed.TotalMilliseconds;
            result.OpenErrors = errors;
            result.OpenWarnings = warnings;
            var title = doc.CallString("GetTitle");
            try
            {
                sw.Restart();
                info.Properties = ReadProperties(doc, info.Configurations);
                result.PropertiesMs = sw.Elapsed.TotalMilliseconds;
            }
            finally
            {
                s.CloseDoc(full, title);
            }
        }

        result.TotalMs = total.Elapsed.TotalMilliseconds;
        return result;
    }

    private ComObj OpenForRead(SwSession s, string full, int type, out int errors, out int warnings)
    {
        const int baseOptions = SwConst.OpenSilent | SwConst.OpenReadOnly;
        if (type != SwConst.DocAssembly)
            return s.OpenDoc6(full, baseOptions, string.Empty, out errors, out warnings);

        switch (_options.AssemblyReadMode)
        {
            case SwAssemblyReadMode.Selective:
                return s.OpenDoc7(full, readOnly: true, selectiveEmpty: true, lightweight: false, out errors, out warnings);
            case SwAssemblyReadMode.Lightweight:
                return s.OpenDoc6(full, baseOptions | SwConst.OpenOverrideDefaultLoadLightweight | SwConst.OpenLoadLightweight,
                    string.Empty, out errors, out warnings);
            default:
                return s.OpenDoc6(full, baseOptions, string.Empty, out errors, out warnings);
        }
    }

    private static CadException? SameNameError(SwSession s, string full)
    {
        var others = s.FindSameNameElsewhere(full);
        if (others.Count == 0) return null;
        return new CadException(
            $"В SOLIDWORKS уже открыт документ «{others[0].FileName}» из папки «{Path.GetDirectoryName(others[0].Path)}». " +
            "SOLIDWORKS не может открыть второй файл с тем же именем. Закройте этот документ (и сборки, в которые он входит) и повторите.");
    }

    private List<CadReference> ReadReferences(SwSession s, string full, bool traverse, List<string>? raw, List<string>? virtualSkipped)
    {
        var values = ComObj.AsStrings(s.App.Call("GetDocumentDependencies2", full, traverse, _options.SearchDependencies, false));
        raw?.AddRange(values);
        var result = new List<CadReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            var name = values[i];
            var saved = values[i + 1];
            var fileName = saved.Length > 0 ? CadReference.FileNameOf(saved) : CadReference.FileNameOf(name);
            if (name.IndexOf('^') >= 0 || fileName.IndexOf('^') >= 0)
            {
                // Виртуальный компонент: хранится внутри сборки, отдельного файла нет.
                virtualSkipped?.Add(saved.Length > 0 ? saved : name);
                continue;
            }
            var key = saved.Length > 0 ? saved : name;
            if (SwSession.SamePath(key, full) || !seen.Add(key)) continue;
            result.Add(new CadReference { SavedPath = key, FileName = fileName });
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadProperties(ComObj doc, List<string> configurations)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        var ext = doc.CallObj("Extension") ?? throw new CadException("SOLIDWORKS не вернул ModelDocExtension.");
        result[string.Empty] = ReadPropertyManager(ext, string.Empty);
        foreach (var config in configurations)
        {
            var props = ReadPropertyManager(ext, config);
            if (props.Count > 0) result[config] = props;
        }
        return result;
    }

    private static Dictionary<string, string> ReadPropertyManager(ComObj ext, string configuration)
    {
        var result = new Dictionary<string, string>();
        var manager = ext.CallObj("CustomPropertyManager", configuration);
        if (manager == null) return result;
        foreach (var name in ComObj.AsStrings(manager.Call("GetNames")))
        {
            if (name.Length == 0) continue;
            var args = new object?[] { name, false, string.Empty, string.Empty };
            manager.CallRef("Get4", args, 2, 3);
            var value = ComObj.AsString(args[2]) ?? string.Empty;
            var resolved = ComObj.AsString(args[3]) ?? string.Empty;
            result[name] = resolved.Length > 0 ? resolved : value;
        }
        return result;
    }

    // ------------------------------------------------------------------ Замена ссылок

    /// <summary>
    /// Заменить ссылки в <b>закрытом</b> документе: <c>ReplaceReferencedDocument</c> на каждую пару, все пары — за один вызов
    /// (в одном сеансе SOLIDWORKS). Документ, открытый в SOLIDWORKS, не меняется — <see cref="CadException"/> с понятным текстом;
    /// так же — если SOLIDWORKS не запущен. Пары «старый = новый» пропускаются. Если часть ссылок заменить не удалось,
    /// остальные уже записаны в файл — это сказано в тексте ошибки.
    /// </summary>
    public Task ReplaceReferencesAsync(string documentPath, IReadOnlyDictionary<string, string> oldToNew, CancellationToken ct = default)
    {
        if (oldToNew == null) throw new ArgumentNullException(nameof(oldToNew));
        var full = CheckFile(documentPath);
        return RunAsync(s =>
        {
            if (s.FindOpenByPath(full) != null)
                throw new CadException($"Документ «{Path.GetFileName(full)}» открыт в SOLIDWORKS. Ссылки меняются только в закрытом файле: закройте его и повторите.");
            if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
                throw new CadException($"Файл «{Path.GetFileName(full)}» только для чтения — ссылки в нём менять нельзя.");

            var pairs = oldToNew.Where(p => !string.Equals(p.Key, p.Value, StringComparison.OrdinalIgnoreCase)).ToList();
            var failed = new List<string>();
            foreach (var pair in pairs)
            {
                ct.ThrowIfCancellationRequested();
                if (!s.App.CallBool("ReplaceReferencedDocument", full, pair.Key, pair.Value))
                    failed.Add($"«{pair.Key}» → «{pair.Value}»");
            }
            if (failed.Count > 0)
            {
                var done = pairs.Count - failed.Count;
                throw new CadException($"SOLIDWORKS не заменил ссылки в «{Path.GetFileName(full)}»: {string.Join("; ", failed)}." +
                                       (done > 0 ? $" Остальные ссылки ({done} из {pairs.Count}) заменены." : string.Empty));
            }
            return true;
        }, ct);
    }

    // ------------------------------------------------------------------ Запись свойств

    /// <summary>
    /// Записать свойства (<c>CustomPropertyManager.Add3</c>, тип «Текст», замена значения) и сохранить документ (<c>Save3</c>).
    /// Закрытый документ открывается невидимым и закрывается: сборка — облегчённой (выборочное открытие без компонентов
    /// сохранило бы их скрытыми), чертёж — обычным. Если в сборке не найден компонент или у чертежа модель, запись отменяется:
    /// SOLIDWORKS сохранил бы их подавленными. Уже открытый документ меняется в памяти и сохраняется, только если он открыт
    /// не «только для чтения» и в нём нет несохранённых правок. При ошибке до сохранения файл не меняется.
    /// </summary>
    public Task WritePropertiesAsync(string documentPath, IReadOnlyList<CadPropertyChange> changes, CancellationToken ct = default)
    {
        if (changes == null) throw new ArgumentNullException(nameof(changes));
        var full = CheckFile(documentPath);
        if (changes.Count == 0) return Task.CompletedTask;
        return RunAsync(s =>
        {
            WriteProperties(s, full, changes, ct);
            return true;
        }, ct);
    }

    private static void WriteProperties(SwSession s, string full, IReadOnlyList<CadPropertyChange> changes, CancellationToken ct)
    {
        var file = Path.GetFileName(full);
        if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new CadException($"Файл «{file}» только для чтения: возьмите документ в работу, чтобы изменить свойства.");

        ComObj doc;
        string? openedTitle = null;
        var open = s.FindOpenByPath(full);
        if (open != null)
        {
            doc = open.Doc;
            if (doc.CallBool("IsOpenedReadOnly"))
                throw new CadException($"«{file}» открыт в SOLIDWORKS только для чтения (открыт до взятия в работу). Закройте его в SOLIDWORKS и повторите.");
            if (doc.CallBool("GetSaveFlag"))
                throw new CadException($"В «{file}» в SOLIDWORKS есть несохранённые изменения. Сохраните или закройте документ и повторите — иначе они сохранились бы вместе со свойствами.");
        }
        else
        {
            if (s.FindSameNameElsewhere(full) is { Count: > 0 } others)
                throw new CadException($"В SOLIDWORKS уже открыт документ «{others[0].FileName}» из папки «{Path.GetDirectoryName(others[0].Path)}». " +
                                       "Закройте его (и сборки, в которые он входит) и повторите.");
            var type = SwConst.DocTypeOf(full);
            var options = SwConst.OpenSilent | (type == SwConst.DocAssembly ? SwConst.OpenOverrideDefaultLoadLightweight | SwConst.OpenLoadLightweight : 0);
            using (s.HideNewDocuments(type))
            {
                doc = s.OpenDoc6(full, options, string.Empty, out _, out _);
            }
            openedTitle = doc.CallString("GetTitle") ?? string.Empty;
            if (doc.CallBool("IsOpenedReadOnly"))
            {
                s.CloseDoc(full, openedTitle);
                throw new CadException($"SOLIDWORKS открыл «{file}» только для чтения (файл занят другой программой?). Свойства не записаны.");
            }
        }

        try
        {
            if (openedTitle != null) CheckNothingMissing(s, doc, file);
            ct.ThrowIfCancellationRequested();
            var ext = doc.CallObj("Extension") ?? throw new CadException("SOLIDWORKS не вернул ModelDocExtension.");
            foreach (var change in changes)
            {
                var manager = ext.CallObj("CustomPropertyManager", change.Configuration ?? string.Empty)
                              ?? throw new CadException($"В «{file}» нет конфигурации «{change.Configuration}».");
                var code = manager.CallInt("Add3", change.Name, SwConst.CustomInfoText, change.Value ?? string.Empty, SwConst.CustomPropertyReplaceValue);
                var args = new object?[] { change.Name, false, string.Empty, string.Empty };
                manager.CallRef("Get4", args, 2, 3);
                if ((code != 0 && code != 1) || !string.Equals(ComObj.AsString(args[2]) ?? string.Empty, change.Value ?? string.Empty, StringComparison.Ordinal))
                    throw new CadException($"SOLIDWORKS не записал свойство «{change.Name}»{Where(change)} в «{file}» (код {code}). Файл не сохранён.");
            }

            var save = new object?[] { SwConst.SaveAsOptionsSilent, 0, 0 };
            var ok = ComObj.AsBool(doc.CallRef("Save3", save, 1, 2));
            if (!ok || ComObj.AsInt(save[1]) != 0)
                throw new CadException($"SOLIDWORKS не сохранил «{file}» (ошибки {ComObj.AsInt(save[1])}, предупреждения {ComObj.AsInt(save[2])}).");
        }
        finally
        {
            if (openedTitle != null) s.CloseDoc(full, openedTitle);
        }

        static string Where(CadPropertyChange c) => string.IsNullOrEmpty(c.Configuration) ? string.Empty : $" (конфигурация «{c.Configuration}»)";
    }

    // ------------------------------------------------------------------ Удаление ссылок

    /// <summary>
    /// Убрать ссылки: у сборки — удалить компоненты верхнего уровня с этими файлами (<c>EditDelete</c>); у детали —
    /// разорвать внешние ссылки (<c>BreakAllExternalFileReferences2</c>: SOLIDWORKS разрывает все сразу и оставляет их в файле
    /// разорванными) и записать имена файлов в свойство <see cref="DocumentProperties.RemovedReferencesKey"/>. Затем сохранить.
    /// Документ должен быть закрыт в SOLIDWORKS. Чертёж не поддерживается.
    /// </summary>
    public Task RemoveReferencesAsync(string documentPath, IReadOnlyList<string> savedPaths, CancellationToken ct = default)
    {
        if (savedPaths == null) throw new ArgumentNullException(nameof(savedPaths));
        var full = CheckFile(documentPath);
        if (SwConst.DocTypeOf(full) == SwConst.DocDrawing)
            throw new CadException($"У чертежа «{Path.GetFileName(full)}» ссылки — это виды модели. Удалите виды в SOLIDWORKS.");
        if (savedPaths.Count == 0) return Task.CompletedTask;
        return RunAsync(s =>
        {
            RemoveReferences(s, full, savedPaths, ct);
            return true;
        }, ct);
    }

    private static void RemoveReferences(SwSession s, string full, IReadOnlyList<string> savedPaths, CancellationToken ct)
    {
        var file = Path.GetFileName(full);
        if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
            throw new CadException($"Файл «{file}» только для чтения: возьмите документ в работу.");
        if (s.FindOpenByPath(full) != null)
            throw new CadException($"«{file}» открыт в SOLIDWORKS. Закройте его и повторите.");
        if (s.FindSameNameElsewhere(full) is { Count: > 0 } others)
            throw new CadException($"В SOLIDWORKS уже открыт документ «{others[0].FileName}» из папки «{Path.GetDirectoryName(others[0].Path)}». Закройте его и повторите.");

        var type = SwConst.DocTypeOf(full);
        var options = SwConst.OpenSilent | (type == SwConst.DocAssembly ? SwConst.OpenOverrideDefaultLoadLightweight | SwConst.OpenLoadLightweight : 0);
        ComObj doc;
        using (s.HideNewDocuments(type))
        {
            doc = s.OpenDoc6(full, options, string.Empty, out _, out _);
        }
        var title = doc.CallString("GetTitle") ?? string.Empty;
        try
        {
            if (doc.CallBool("IsOpenedReadOnly"))
                throw new CadException($"SOLIDWORKS открыл «{file}» только для чтения. Ссылки не убраны.");
            ct.ThrowIfCancellationRequested();
            var names = savedPaths.Select(CadReference.FileNameOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ext = doc.CallObj("Extension") ?? throw new CadException("SOLIDWORKS не вернул ModelDocExtension.");

            if (type == SwConst.DocAssembly)
            {
                // Компоненты верхнего уровня (ссылки сборки — на её прямые компоненты), в том числе ненайденные (подавленные).
                var components = ComObj.AsObjects(doc.Call("GetComponents", true))
                    .Where(c => !c.CallBool("IsVirtual"))
                    .Select(c => (Comp: c, Name: Path.GetFileName(c.CallString("GetPathName") ?? string.Empty)))
                    .Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var missing = names.Where(n => components.All(c => !string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                    throw new CadException($"В сборке «{file}» нет компонентов {string.Join(", ", missing.Select(n => "«" + n + "»"))} — это не ссылка на компонент. Уберите её в SOLIDWORKS.");
                doc.Call("ClearSelection2", true);
                var first = true;
                foreach (var (comp, _) in components)
                {
                    // SelectData — пустой объект: при позднем связывании null нужно передать как пустой IDispatch.
                    if (!comp.CallBool("Select4", !first, new System.Runtime.InteropServices.DispatchWrapper(null), false))
                        throw new CadException($"SOLIDWORKS не выделил компонент «{comp.CallString("Name2")}» в «{file}». Ссылки не убраны.");
                    first = false;
                }
                // swDeleteSelectionOptions_e: 0 — только выделенное (без поглощённых элементов и дочерних).
                if (!ext.CallBool("DeleteSelection2", 0)) doc.Call("EditDelete");
                var left = ComObj.AsObjects(doc.Call("GetComponents", true))
                    .Select(c => Path.GetFileName(c.CallString("GetPathName") ?? string.Empty))
                    .Where(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                if (left.Count > 0)
                    throw new CadException($"SOLIDWORKS не удалил компоненты «{string.Join(", ", left)}» из «{file}». Ссылки не убраны.");
            }
            else
            {
                ext.Call("BreakAllExternalFileReferences2", false);
                var manager = ext.CallObj("CustomPropertyManager", string.Empty) ?? throw new CadException($"В «{file}» нет свойств уровня файла.");
                var args = new object?[] { DocumentProperties.RemovedReferencesKey, false, string.Empty, string.Empty };
                manager.CallRef("Get4", args, 2, 3);
                var current = new Dictionary<string, Dictionary<string, string>>
                {
                    [string.Empty] = new() { [DocumentProperties.RemovedReferencesKey] = ComObj.AsString(args[2]) ?? string.Empty },
                };
                var value = DocumentProperties.WithRemovedReferences(current, names);
                var code = manager.CallInt("Add3", DocumentProperties.RemovedReferencesKey, SwConst.CustomInfoText, value, SwConst.CustomPropertyReplaceValue);
                if (code != 0 && code != 1)
                    throw new CadException($"SOLIDWORKS не записал свойство «{DocumentProperties.RemovedReferencesKey}» в «{file}» (код {code}).");
            }

            var save = new object?[] { SwConst.SaveAsOptionsSilent, 0, 0 };
            var ok = ComObj.AsBool(doc.CallRef("Save3", save, 1, 2));
            if (!ok || ComObj.AsInt(save[1]) != 0)
                throw new CadException($"SOLIDWORKS не сохранил «{file}» (ошибки {ComObj.AsInt(save[1])}, предупреждения {ComObj.AsInt(save[2])}).");
        }
        finally
        {
            s.CloseDoc(full, title);
        }
    }

    /// <summary>
    /// Перед сохранением открытой адаптером сборки или чертежа: все компоненты и модели видов найдены. Иначе SOLIDWORKS
    /// сохранил бы ненайденные компоненты подавленными, а виды — пустыми.
    /// </summary>
    private static void CheckNothingMissing(SwSession s, ComObj doc, string file)
    {
        var type = doc.CallInt("GetType");
        var missing = new List<string>();
        if (type == SwConst.DocAssembly)
        {
            foreach (var comp in ComObj.AsObjects(doc.Call("GetComponents", false)))
            {
                if (comp.CallBool("IsVirtual")) continue;
                var path = comp.CallString("GetPathName") ?? string.Empty;
                if (path.Length == 0 || !File.Exists(path)) missing.Add(comp.CallString("Name2") ?? path);
            }
        }
        else if (type == SwConst.DocDrawing && doc.Call("GetViews") is Array sheets)
        {
            foreach (var sheet in sheets)
                foreach (var view in ComObj.AsObjects(sheet))
                {
                    var path = view.CallString("GetReferencedModelName") ?? string.Empty;
                    if (path.Length > 0 && s.FindOpenByPath(path) == null) missing.Add(path);
                }
        }
        if (missing.Count > 0)
            throw new CadException($"В «{file}» не найдены {(type == SwConst.DocDrawing ? "модели видов" : "компоненты")}: " +
                                   string.Join(", ", missing.Distinct(StringComparer.OrdinalIgnoreCase).Take(5)) + (missing.Count > 5 ? " …" : string.Empty) +
                                   ". Свойства не записаны: при сохранении SOLIDWORKS испортил бы документ. Положите недостающие файлы в рабочую папку и повторите.");
    }

    // ------------------------------------------------------------------ Открытие с проверкой

    public async Task<OpenReport> OpenAndVerifyAsync(string headPath, string workRoot, CancellationToken ct = default) =>
        (await OpenAndVerifyDetailedAsync(headPath, workRoot, ct).ConfigureAwait(false)).Report;

    /// <summary>Открыть головной документ видимым и проверить реальные пути компонентов. Документ остаётся открытым.</summary>
    public Task<SwOpenDiagnostics> OpenAndVerifyDetailedAsync(string headPath, string workRoot, CancellationToken ct = default)
    {
        var head = CheckFile(headPath);
        if (string.IsNullOrWhiteSpace(workRoot)) throw new ArgumentException("Не задана рабочая папка.", nameof(workRoot));
        var root = SwSession.Normalize(workRoot);
        return RunAsync(s => OpenAndVerify(s, head, root, ct), ct);
    }

    private SwOpenDiagnostics OpenAndVerify(SwSession s, string head, string root, CancellationToken ct)
    {
        var diag = new SwOpenDiagnostics();
        var report = diag.Report;
        report.HeadPath = head;
        var problems = new SortedSet<string>(StringComparer.Ordinal);

        if (!SwSession.IsInside(head, root))
            problems.Add($"Головной документ «{head}» лежит вне рабочей папки «{root}».");

        // 1. Имена всех файлов дерева (по сохранённым ссылкам) — до открытия.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(head) };
        foreach (var r in ReadReferences(s, head, traverse: true, null, null))
            names.Add(r.FileName);
        diag.TreeFileNames = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        // 2. Одноимённые документы не из рабочей папки — главная причина тихой подмены.
        var conflicts = 0;
        foreach (var d in s.GetOpenDocuments())
        {
            if (d.Path.Length == 0 || !names.Contains(d.FileName) || SwSession.IsInside(d.Path, root)) continue;
            conflicts++;
            problems.Add($"В SOLIDWORKS уже открыт «{d.FileName}» из «{Path.GetDirectoryName(d.Path)}». " +
                         "SOLIDWORKS возьмёт его вместо файла из рабочей папки. Закройте этот документ (и сборки, в которые он входит) и повторите.");
        }
        if (conflicts > 0)
        {
            report.Problems = problems.ToList();
            report.Ok = false;
            return diag;
        }
        ct.ThrowIfCancellationRequested();

        // 3. Открыть видимым (или взять уже открытый).
        ComObj doc;
        var existing = s.FindOpenByPath(head);
        if (existing != null)
        {
            diag.WasAlreadyOpen = true;
            doc = existing.Doc;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            doc = s.OpenDoc6(head, SwConst.OpenSilent, string.Empty, out var errors, out var warnings);
            diag.OpenMs = sw.Elapsed.TotalMilliseconds;
            diag.Opened = true;
            diag.OpenErrors = errors;
            diag.OpenWarnings = warnings;
            diag.OpenWarningNames = SwConst.DescribeLoadWarnings(warnings);
        }

        var type = doc.CallInt("GetType");
        if (type == SwConst.DocAssembly)
            VerifyAssembly(doc, root, report, diag, problems, ct);
        else if (type == SwConst.DocDrawing)
            VerifyDrawing(s, doc, root, report, diag, problems);
        else
            foreach (var c in ComObj.AsStrings(doc.Call("GetConfigurationNames")))
                report.Configurations.Add(new OpenConfigurationReport { Name = c });

        // 4. Что из комплекта реально в памяти SOLIDWORKS.
        foreach (var d in s.GetOpenDocuments())
        {
            if (d.Path.Length == 0 || !names.Contains(d.FileName)) continue;
            diag.LoadedDocuments.Add(d.Path);
            if (!SwSession.IsInside(d.Path, root))
                problems.Add($"«{d.FileName}» загружен из «{Path.GetDirectoryName(d.Path)}» — вне рабочей папки.");
        }
        diag.LoadedDocuments.Sort(StringComparer.OrdinalIgnoreCase);

        report.Problems = problems.ToList();
        report.Ok = problems.Count == 0 && report.Configurations.All(c => c.Components.All(x => x.InsideWorkRoot));
        return diag;
    }

    private static void VerifyAssembly(ComObj doc, string root, OpenReport report, SwOpenDiagnostics diag, SortedSet<string> problems, CancellationToken ct)
    {
        var active = doc.CallObj("GetActiveConfiguration")?.CallString("Name");
        var configs = ComObj.AsStrings(doc.Call("GetConfigurationNames"));
        foreach (var config in configs)
        {
            ct.ThrowIfCancellationRequested();
            doc.Call("ShowConfiguration2", config);
            var cr = new OpenConfigurationReport { Name = config };
            foreach (var comp in ComObj.AsObjects(doc.Call("GetComponents", false)))
            {
                var name = comp.CallString("Name2") ?? string.Empty;
                var path = comp.CallString("GetPathName") ?? string.Empty;
                if (comp.CallBool("IsVirtual")) continue;
                if (comp.CallBool("IsSuppressed"))
                {
                    diag.Suppressed.Add($"{config}: {name} ({path})");
                    // При тихом открытии SOLIDWORKS подавляет ненайденные компоненты — отличаем по наличию файла.
                    if (path.Length == 0 || !File.Exists(path))
                        problems.Add($"Конфигурация «{config}»: компонент «{name}» не найден ({path}); SOLIDWORKS его подавил.");
                    continue;
                }
                var inside = SwSession.IsInside(path, root);
                // В имени — конфигурация компонента: контракт LoadedComponent отдельного поля не имеет.
                var refConfig = comp.CallString("ReferencedConfiguration");
                var shown = string.IsNullOrEmpty(refConfig) ? name : $"{name} [{refConfig}]";
                cr.Components.Add(new LoadedComponent { Name = shown, LoadedPath = path, InsideWorkRoot = inside });
                if (path.Length == 0 || !File.Exists(path))
                    problems.Add($"Конфигурация «{config}»: компонент «{name}» не найден ({path}).");
                else if (!inside)
                    problems.Add($"Конфигурация «{config}»: компонент «{name}» загружен из «{path}» — вне рабочей папки.");
            }
            report.Configurations.Add(cr);
        }
        if (!string.IsNullOrEmpty(active)) doc.Call("ShowConfiguration2", active);
    }

    private static void VerifyDrawing(SwSession s, ComObj doc, string root, OpenReport report, SwOpenDiagnostics diag, SortedSet<string> problems)
    {
        var views = new OpenConfigurationReport { Name = "Виды" };
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.Call("GetViews") is Array sheets)
        {
            // Элемент — массив листа: сначала вид самого листа, затем виды модели.
            foreach (var sheet in sheets)
                foreach (var view in ComObj.AsObjects(sheet))
                    AddView(view);
        }
        report.Configurations.Add(views);

        // Компоненты сборок, на которые ссылаются виды (в конфигурации, активной у модели).
        foreach (var pair in models)
        {
            var model = s.FindOpenByPath(pair.Key);
            if (model == null || model.Type != SwConst.DocAssembly) continue;
            var cr = new OpenConfigurationReport { Name = $"{Path.GetFileName(pair.Key)} [{pair.Value}]" };
            foreach (var comp in ComObj.AsObjects(model.Doc.Call("GetComponents", false)))
            {
                if (comp.CallBool("IsVirtual")) continue;
                var name = comp.CallString("Name2") ?? string.Empty;
                var path = comp.CallString("GetPathName") ?? string.Empty;
                if (comp.CallBool("IsSuppressed"))
                {
                    diag.Suppressed.Add($"{cr.Name}: {name} ({path})");
                    if (path.Length == 0 || !File.Exists(path))
                        problems.Add($"«{Path.GetFileName(pair.Key)}»: компонент «{name}» не найден ({path}); SOLIDWORKS его подавил.");
                    continue;
                }
                var inside = SwSession.IsInside(path, root);
                cr.Components.Add(new LoadedComponent { Name = name, LoadedPath = path, InsideWorkRoot = inside });
                if (!inside) problems.Add($"«{Path.GetFileName(pair.Key)}»: компонент «{name}» загружен из «{path}» — вне рабочей папки.");
            }
            report.Configurations.Add(cr);
        }

        void AddView(ComObj view)
        {
            var path = view.CallString("GetReferencedModelName") ?? string.Empty;
            if (path.Length == 0) return; // лист
            var config = view.CallString("ReferencedConfiguration") ?? string.Empty;
            var name = view.CallString("GetName2") ?? string.Empty;
            var inside = SwSession.IsInside(path, root);
            views.Components.Add(new LoadedComponent { Name = $"{name} [{config}]", LoadedPath = path, InsideWorkRoot = inside });
            if (s.FindOpenByPath(path) == null)
                problems.Add($"Вид «{name}»: модель «{path}» не загружена — файл не найден ни по сохранённому пути, ни рядом с чертежом.");
            else if (!inside)
                problems.Add($"Вид «{name}» показывает «{path}» — вне рабочей папки.");
            models[path] = config;
        }
    }

    // ------------------------------------------------------------------

    private string CheckFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Не задан путь файла.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!IsDocument(full)) throw new CadException($"«{Path.GetFileName(full)}» не является документом SOLIDWORKS.");
        if (!File.Exists(full)) throw new CadException($"Файл «{full}» не найден.");
        return full;
    }

    public void Dispose()
    {
        _sta.Dispose();
    }
}
