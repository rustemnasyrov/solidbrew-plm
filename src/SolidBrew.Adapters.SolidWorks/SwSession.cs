using SolidBrew.Core.Cad;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>
/// Документ, открытый в SOLIDWORKS (в том числе невидимый — компонент открытой сборки).
/// Каждый вызов COM через IDispatch стоит ~10 мс, поэтому заголовок, тип и видимость читаются лениво.
/// </summary>
public sealed class SwOpenDocument
{
    private string? _title;
    private int? _type;
    private bool? _visible;

    public SwOpenDocument(ComObj doc, string path)
    {
        Doc = doc;
        Path = path;
    }

    public ComObj Doc { get; }
    /// <summary>Полный путь; пустой у нового несохранённого документа.</summary>
    public string Path { get; }
    public string Title => _title ??= Doc.CallString("GetTitle") ?? string.Empty;
    public int Type => _type ??= Doc.CallInt("GetType");
    public bool Visible
    {
        get
        {
            if (_visible == null)
            {
                try { _visible = Doc.CallBool("Visible"); }
                catch (CadException) { _visible = false; }
            }
            return _visible.Value;
        }
    }
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Подключение к SOLIDWORKS и общие операции над ним. Живёт на потоке STA адаптера;
/// получать только через <see cref="SolidWorksAdapter.RunAsync{T}"/>.
/// </summary>
public sealed class SwSession
{
    internal SwSession(ComObj app)
    {
        App = app;
        Revision = app.CallString("RevisionNumber") ?? string.Empty;
    }

    /// <summary>Объект <c>SldWorks.Application</c>.</summary>
    public ComObj App { get; }

    /// <summary>Номер версии программы, например «26.1.0» (26 + 1992 = SOLIDWORKS 2018).</summary>
    public string Revision { get; }

    /// <summary>Год версии программы по <see cref="Revision"/> («26.1.0» → 2018).</summary>
    public int? RevisionYear
    {
        get
        {
            var dot = Revision.IndexOf('.');
            var head = dot < 0 ? Revision : Revision.Substring(0, dot);
            return int.TryParse(head, out var major) && major > 0 ? major + 1992 : null;
        }
    }

    /// <summary>Все документы в памяти SOLIDWORKS, включая невидимые компоненты сборок.</summary>
    public List<SwOpenDocument> GetOpenDocuments()
    {
        var result = new List<SwOpenDocument>();
        foreach (var doc in ComObj.AsObjects(App.Call("GetDocuments")))
            result.Add(Describe(doc));
        return result;
    }

    private static SwOpenDocument Describe(ComObj doc) => new SwOpenDocument(doc, doc.CallString("GetPathName") ?? string.Empty);

    /// <summary>Открытый документ по имени или пути (<c>GetOpenDocumentByName</c>) или null.</summary>
    private SwOpenDocument? ByName(string name)
    {
        var doc = App.CallObj("GetOpenDocumentByName", name);
        return doc == null ? null : Describe(doc);
    }

    /// <summary>Открытый документ с этим же полным путём или null.</summary>
    public SwOpenDocument? FindOpenByPath(string fullPath)
    {
        var d = ByName(fullPath);
        return d != null && d.Path.Length > 0 && SamePath(d.Path, fullPath) ? d : null;
    }

    /// <summary>
    /// Открытые документы с таким же именем файла, но из другой папки (SOLIDWORKS держит в памяти не больше одного
    /// документа с данным именем). Перебирает все документы в памяти: по одному вызову COM на документ.
    /// <c>GetOpenDocumentByName</c> по одному имени файла такие документы не находит (проверено на 2018).
    /// </summary>
    public List<SwOpenDocument> FindSameNameElsewhere(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        var result = new List<SwOpenDocument>();
        foreach (var d in GetOpenDocuments())
        {
            if (d.Path.Length == 0) continue;
            if (string.Equals(d.FileName, name, StringComparison.OrdinalIgnoreCase) && !SamePath(d.Path, fullPath))
                result.Add(d);
        }
        return result;
    }

    /// <summary>Открыть документ <c>OpenDoc6</c>. При ошибке — <see cref="CadException"/>.</summary>
    public ComObj OpenDoc6(string fullPath, int options, string configuration, out int errors, out int warnings)
    {
        var type = SwConst.DocTypeOf(fullPath);
        var args = new object?[] { fullPath, type, options, configuration, 0, 0 };
        var doc = ComObj.Wrap(App.CallRef("OpenDoc6", args, 4, 5));
        errors = ComObj.AsInt(args[4]);
        warnings = ComObj.AsInt(args[5]);
        if (doc == null) throw new SwOpenException(fullPath, errors);
        return doc;
    }

    /// <summary>
    /// Открыть через <c>OpenDoc7</c> и <c>DocumentSpecification</c> (только чтение, без сообщений).
    /// <paramref name="selectiveEmpty"/> — для сборки: выборочное открытие без компонентов.
    /// </summary>
    public ComObj OpenDoc7(string fullPath, bool readOnly, bool selectiveEmpty, bool lightweight, out int errors, out int warnings)
    {
        var spec = App.CallObj("GetOpenDocSpec", fullPath)
            ?? throw new CadException($"SOLIDWORKS не вернул параметры открытия для «{fullPath}».");
        spec.Set("ReadOnly", readOnly);
        spec.Set("Silent", true);
        if (selectiveEmpty)
        {
            spec.Set("Selective", true);
            spec.Set("ComponentList", Array.Empty<string>());
        }
        if (lightweight) spec.Set("LightWeight", true);
        var doc = App.CallObj("OpenDoc7", spec.Raw);
        errors = ComObj.AsInt(spec.Get("Error"));
        warnings = ComObj.AsInt(spec.Get("Warning"));
        if (doc == null) throw new SwOpenException(fullPath, errors);
        return doc;
    }

    /// <summary>Закрыть документ без сохранения. Закрывать только документы, открытые самим адаптером.</summary>
    public void CloseDoc(string fullPath, string? title = null)
    {
        App.Call("CloseDoc", fullPath);
        if (!string.IsNullOrEmpty(title) && FindOpenByPath(fullPath) != null)
            App.Call("CloseDoc", title);
    }

    /// <summary>
    /// Временно открывать документы невидимыми (<c>DocumentVisible</c>). При освобождении — вернуть видимость.
    /// </summary>
    public IDisposable HideNewDocuments(int docType)
    {
        App.Call("DocumentVisible", false, docType);
        return new Restore(() => App.Call("DocumentVisible", true, docType));
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return path; }
    }

    /// <summary>Путь лежит внутри папки (сравнение без учёта регистра).</summary>
    public static bool IsInside(string path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var r = Normalize(root) + "\\";
        return Normalize(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Restore : IDisposable
    {
        private Action? _action;
        public Restore(Action action) { _action = action; }
        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _action, null);
            a?.Invoke();
        }
    }
}

/// <summary>SOLIDWORKS не открыл документ; <see cref="ErrorCode"/> — swFileLoadError_e.</summary>
public sealed class SwOpenException : CadException
{
    public SwOpenException(string path, int errorCode)
        : base($"SOLIDWORKS не открыл «{path}»: {SwConst.DescribeLoadError(errorCode)}.")
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }

    /// <summary>Уже открыт документ с таким же именем (swFileWithSameTitleAlreadyOpen).</summary>
    public bool SameTitleOpen => (ErrorCode & 0x10000) != 0;
}
