using System.Runtime.InteropServices;
using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Core.Cad;

namespace SwProbe;

/// <summary>
/// Замкнутый тестовый комплект для приёмки этапа 1 (ТЗ, раздел 11): общая деталь с двумя конфигурациями,
/// ещё две детали, подсборка, головная сборка и чертежи. Создаётся через API SOLIDWORKS шаблонами по умолчанию.
/// </summary>
internal static class TestKit
{
    private sealed record PartSpec(string Designation, string Name, double Width, double Height, double Depth, List<ConfigSpec>? Configs);

    private sealed record ConfigSpec(string Name, double Depth, string Designation);

    private sealed record ComponentSpec(string Path, string? Configuration, double X);

    private sealed class Ctx
    {
        public Ctx(SwSession s) { S = s; }
        public SwSession S { get; }
        public List<string> Log { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Документы, открытые или созданные комплектом (для закрытия в конце): путь, заголовок.</summary>
        public List<(string Path, string Title)> Opened { get; } = new List<(string, string)>();
        public List<object> Files { get; } = new List<object>();
    }

    public const string SharedPart = "ТК.000.001 Пластина общая.SLDPRT";
    public const string SubPart = "ТК.100.001 Втулка.SLDPRT";
    public const string HeadPart = "ТК.000.002 Планка.SLDPRT";
    public const string SubAssembly = "ТК.100.000 Узел.SLDASM";
    public const string HeadAssembly = "ТК.000.000 Изделие тестовое.SLDASM";

    public static object Make(SwSession s, string folder)
    {
        var root = Path.GetFullPath(folder);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new ArgumentException($"Папка «{root}» не пуста. Тестовый комплект создаётся только в новой папке.");
        var drawings = Path.Combine(root, "Чертежи");

        var ctx = new Ctx(s);
        var sharedPath = Path.Combine(root, SharedPart);
        var subPartPath = Path.Combine(root, SubPart);
        var headPartPath = Path.Combine(root, HeadPart);
        var subPath = Path.Combine(root, SubAssembly);
        var headPath = Path.Combine(root, HeadAssembly);
        var drwHead = Path.Combine(drawings, Path.ChangeExtension(HeadAssembly, ".SLDDRW"));
        var drwSub = Path.Combine(drawings, Path.ChangeExtension(SubAssembly, ".SLDDRW"));
        var drwShared = Path.Combine(drawings, Path.ChangeExtension(SharedPart, ".SLDDRW"));

        foreach (var p in new[] { sharedPath, subPartPath, headPartPath, subPath, headPath, drwHead, drwSub, drwShared })
        {
            var other = s.FindSameNameElsewhere(p);
            if (other.Count > 0)
                throw new CadException($"В SOLIDWORKS открыт одноимённый документ «{other[0].Path}». Закройте его и повторите.");
        }

        var templates = new
        {
            part = Template(s, SwConst.PrefDefaultTemplatePart, "*.prtdot"),
            assembly = Template(s, SwConst.PrefDefaultTemplateAssembly, "*.asmdot"),
            drawing = Template(s, SwConst.PrefDefaultTemplateDrawing, "*.drwdot"),
        };
        ctx.Log.Add($"Шаблоны: {templates.part}; {templates.assembly}; {templates.drawing}");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(drawings);
        try
        {
            MakePart(ctx, templates.part, sharedPath, new PartSpec("ТК.000.001", "Пластина общая", 0.080, 0.040, 0.010,
                new List<ConfigSpec> { new("01", 0.010, "ТК.000.001"), new("02", 0.020, "ТК.000.001-01") }));
            MakePart(ctx, templates.part, subPartPath, new PartSpec("ТК.100.001", "Втулка", 0.030, 0.030, 0.015, null));
            MakePart(ctx, templates.part, headPartPath, new PartSpec("ТК.000.002", "Планка", 0.060, 0.010, 0.005, null));

            MakeAssembly(ctx, templates.assembly, subPath, "ТК.100.000", "Узел", new List<ComponentSpec>
            {
                new(sharedPath, "01", 0.0),
                new(subPartPath, null, 0.12),
            });
            MakeAssembly(ctx, templates.assembly, headPath, "ТК.000.000", "Изделие тестовое", new List<ComponentSpec>
            {
                new(subPath, null, 0.0),
                new(sharedPath, "02", 0.25),
                new(headPartPath, null, 0.40),
            });

            MakeDrawing(ctx, templates.drawing, drwHead, headPath, "ТК.000.000 СБ", "Изделие тестовое. Сборочный чертёж");
            MakeDrawing(ctx, templates.drawing, drwSub, subPath, "ТК.100.000 СБ", "Узел. Сборочный чертёж");
            MakeDrawing(ctx, templates.drawing, drwShared, sharedPath, "ТК.000.001", "Пластина общая");
        }
        finally
        {
            for (var i = ctx.Opened.Count - 1; i >= 0; i--)
            {
                var (path, title) = ctx.Opened[i];
                try
                {
                    if (path.Length > 0) s.CloseDoc(path, title);
                    else s.App.Call("CloseDoc", title);
                }
                catch (CadException ex) { ctx.Warnings.Add($"Не закрыт {title}: {ex.Message}"); }
            }
        }

        var stillOpen = s.GetOpenDocuments().Where(d => SwSession.IsInside(d.Path, root)).Select(d => d.Path).ToList();
        return new
        {
            folder = root,
            templates,
            files = ctx.Files,
            stillOpen,
            warnings = ctx.Warnings,
            log = ctx.Log,
        };
    }

    private static string Template(SwSession s, int pref, string mask)
    {
        var value = s.App.CallString("GetUserPreferenceStringValue", pref) ?? string.Empty;
        if (value.Length > 0 && File.Exists(value)) return value;
        // Запасной вариант: шаблоны из папки установки.
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dir = Path.Combine(programData, "SolidWorks");
        if (Directory.Exists(dir))
        {
            var found = Directory.EnumerateFiles(dir, mask, SearchOption.AllDirectories)
                .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (found != null) return found;
        }
        throw new CadException($"Не найден шаблон SOLIDWORKS ({mask}); в настройках: «{value}».");
    }

    private static ComObj NewDocument(Ctx ctx, string template, int paperSize = 0, double width = 0, double height = 0)
    {
        var doc = ctx.S.App.CallObj("NewDocument", template, paperSize, width, height)
            ?? throw new CadException($"SOLIDWORKS не создал документ по шаблону «{template}».");
        ctx.Opened.Add((string.Empty, doc.CallString("GetTitle") ?? string.Empty));
        return doc;
    }

    private static void MarkSaved(Ctx ctx, ComObj doc, string path)
    {
        // Заменить запись «новый документ» на сохранённый путь для закрытия в конце.
        var title = doc.CallString("GetTitle") ?? string.Empty;
        for (var i = ctx.Opened.Count - 1; i >= 0; i--)
        {
            if (ctx.Opened[i].Path.Length == 0)
            {
                ctx.Opened[i] = (path, title);
                return;
            }
        }
        ctx.Opened.Add((path, title));
    }

    private static void MakePart(Ctx ctx, string template, string path, PartSpec spec)
    {
        var doc = NewDocument(ctx, template);
        TrySet(ctx, doc, "LengthUnit", SwConst.LengthMm);

        // Первая базовая плоскость (имена плоскостей зависят от языка шаблона).
        ComObj? plane = null;
        for (var f = doc.CallObj("FirstFeature"); f != null; f = f.CallObj("GetNextFeature"))
        {
            if (f.CallString("GetTypeName2") == "RefPlane") { plane = f; break; }
        }
        if (plane == null) throw new CadException("В шаблоне детали нет базовой плоскости.");
        doc.Call("ClearSelection2", true);
        plane.Call("Select2", false, 0);

        var sketch = doc.CallObj("SketchManager") ?? throw new CadException("Нет SketchManager.");
        sketch.Call("InsertSketch", true);
        sketch.Call("CreateCenterRectangle", 0.0, 0.0, 0.0, spec.Width / 2, spec.Height / 2, 0.0);

        var fm = doc.CallObj("FeatureManager") ?? throw new CadException("Нет FeatureManager.");
        var feature = fm.CallObj("FeatureExtrusion2",
            true, false, false, SwConst.EndCondBlind, SwConst.EndCondBlind, spec.Depth, 0.0,
            false, false, false, false, 0.0, 0.0,
            false, false, false, false, true, true, true, 0, 0.0, false)
            ?? throw new CadException($"Не создано вытягивание в «{Path.GetFileName(path)}».");
        var featureName = feature.CallString("Name") ?? string.Empty;
        ctx.Log.Add($"{Path.GetFileName(path)}: вытягивание «{featureName}»");

        SetProperty(doc, string.Empty, "Обозначение", spec.Designation);
        SetProperty(doc, string.Empty, "Наименование", spec.Name);

        var configNames = new List<string>();
        if (spec.Configs != null && spec.Configs.Count > 0)
        {
            var dimName = "D1@" + featureName;
            for (var i = 0; i < spec.Configs.Count; i++)
            {
                var c = spec.Configs[i];
                if (i == 0)
                {
                    var active = doc.CallObj("GetActiveConfiguration") ?? throw new CadException("Нет активной конфигурации.");
                    active.Set("Name", c.Name);
                }
                else if (doc.Call("AddConfiguration3", c.Name, string.Empty, string.Empty, 0) is null or false)
                {
                    throw new CadException($"Не добавлена конфигурация «{c.Name}».");
                }
                doc.Call("ShowConfiguration2", c.Name);
                SetDimension(ctx, doc, dimName, c.Depth, c.Name);
                SetProperty(doc, c.Name, "Обозначение", c.Designation);
                SetProperty(doc, c.Name, "Наименование", spec.Name);
                configNames.Add(c.Name);
            }
            doc.Call("ShowConfiguration2", spec.Configs[0].Name);
        }
        doc.Call("ForceRebuild3", false);
        Save(ctx, doc, path);
        ctx.Files.Add(new { path, kind = "part", spec.Designation, spec.Name, configurations = configNames });
    }

    private static void SetDimension(Ctx ctx, ComObj doc, string name, double value, string config)
    {
        var dim = doc.CallObj("Parameter", name) ?? throw new CadException($"Не найден размер «{name}».");
        try
        {
            var r = dim.CallInt("SetSystemValue3", value, 3, new[] { config }); // swSetValue_InSpecificConfigurations
            if (r != 0) ctx.Warnings.Add($"SetSystemValue3({name}, {config}) = {r}");
        }
        catch (CadException ex)
        {
            ctx.Log.Add($"SetSystemValue3 не сработал ({ex.Message}), пробую SetSystemValue2.");
            dim.Call("SetSystemValue2", value, SwConst.SetValueInThisConfiguration);
        }
        doc.Call("EditRebuild3");
        string? actual;
        try { actual = ComObj.AsString(dim.Call("GetSystemValue3", 3, new[] { config }) is Array a && a.Length > 0 ? a.GetValue(0) : null); }
        catch (CadException ex) { actual = "ошибка: " + ex.Message; }
        ctx.Log.Add($"Размер {name} в «{config}» = {value} м (прочитано: {actual})");
    }

    private static void MakeAssembly(Ctx ctx, string template, string path, string designation, string name, List<ComponentSpec> components)
    {
        var doc = NewDocument(ctx, template);
        TrySet(ctx, doc, "LengthUnit", SwConst.LengthMm);
        var added = new List<string>();
        foreach (var c in components)
        {
            var comp = ctx.S.App.Call("GetOpenDocumentByName", c.Path) == null
                ? throw new CadException($"Компонент «{c.Path}» не загружен в память перед вставкой.")
                : AddComponent(ctx, doc, c);
            if (c.Configuration != null)
            {
                var current = comp.CallString("ReferencedConfiguration");
                if (!string.Equals(current, c.Configuration, StringComparison.Ordinal))
                    comp.Set("ReferencedConfiguration", c.Configuration);
            }
            added.Add($"{comp.CallString("Name2")} → {comp.CallString("GetPathName")} [{comp.CallString("ReferencedConfiguration")}]");
        }
        doc.Call("ForceRebuild3", false);
        SetProperty(doc, string.Empty, "Обозначение", designation);
        SetProperty(doc, string.Empty, "Наименование", name);
        Save(ctx, doc, path);
        ctx.Log.Add($"{Path.GetFileName(path)}: {string.Join("; ", added)}");
        ctx.Files.Add(new { path, kind = "assembly", designation, name, components = added });
    }

    private static ComObj AddComponent(Ctx ctx, ComObj asm, ComponentSpec c)
    {
        try
        {
            var comp = asm.CallObj("AddComponent5", c.Path, 0, string.Empty, false, c.Configuration ?? string.Empty, c.X, 0.0, 0.0);
            if (comp != null) return comp;
            ctx.Log.Add($"AddComponent5 вернул null для {Path.GetFileName(c.Path)}, пробую AddComponent4.");
        }
        catch (CadException ex)
        {
            ctx.Log.Add($"AddComponent5: {ex.Message}; пробую AddComponent4.");
        }
        return asm.CallObj("AddComponent4", c.Path, c.Configuration ?? string.Empty, c.X, 0.0, 0.0)
            ?? throw new CadException($"Не вставлен компонент «{c.Path}».");
    }

    private static void MakeDrawing(Ctx ctx, string template, string path, string modelPath, string designation, string name)
    {
        const int paperUserDefined = 12; // swDwgPapersUserDefined; при листе из шаблона размер не используется
        var doc = NewDocument(ctx, template, paperUserDefined, 0.420, 0.297);
        var ok = doc.CallBool("Create3rdAngleViews2", modelPath);
        var views = 0;
        if (doc.Call("GetViews") is Array sheets)
            foreach (var sheet in sheets) views += sheet is Array v ? Math.Max(v.Length - 1, 0) : 0;
        if (!ok || views == 0)
        {
            ctx.Warnings.Add($"{Path.GetFileName(path)}: Create3rdAngleViews2 = {ok}, видов {views}; пробую вид по модели.");
            doc.CallObj("CreateDrawViewFromModelView3", modelPath, "*Изометрия", 0.2, 0.15, 0.0);
            doc.CallObj("CreateDrawViewFromModelView3", modelPath, "*Isometric", 0.2, 0.15, 0.0);
        }
        SetProperty(doc, string.Empty, "Обозначение", designation);
        SetProperty(doc, string.Empty, "Наименование", name);
        Save(ctx, doc, path);
        ctx.Files.Add(new { path, kind = "drawing", model = modelPath, designation, name, views });
    }

    private static void SetProperty(ComObj doc, string config, string name, string value)
    {
        var ext = doc.CallObj("Extension") ?? throw new CadException("Нет ModelDocExtension.");
        var manager = ext.CallObj("CustomPropertyManager", config) ?? throw new CadException($"Нет CustomPropertyManager для «{config}».");
        var r = manager.CallInt("Add3", name, SwConst.CustomInfoText, value, SwConst.CustomPropertyReplaceValue);
        if (r != 0 && r != 1) // 0 — добавлено, 1 — уже было (заменено)
            throw new CadException($"Свойство «{name}» не записано (код {r}).");
    }

    private static void Save(Ctx ctx, ComObj doc, string path)
    {
        var ext = doc.CallObj("Extension") ?? throw new CadException("Нет ModelDocExtension.");
        var args = new object?[] { path, SwConst.SaveAsCurrentVersion, SwConst.SaveAsOptionsSilent, new DispatchWrapper(null), 0, 0 };
        bool ok;
        try
        {
            ok = ComObj.AsBool(ext.CallRef("SaveAs", args, 4, 5));
        }
        catch (CadException ex)
        {
            ctx.Log.Add($"Extension.SaveAs: {ex.Message}; пробую SaveAs3.");
            var code = doc.CallInt("SaveAs3", path, SwConst.SaveAsCurrentVersion, SwConst.SaveAsOptionsSilent);
            ok = code == 0;
            args[4] = code;
        }
        if (!ok || !File.Exists(path))
            throw new CadException($"Не сохранён «{path}» (ошибки {ComObj.AsInt(args[4])}, предупреждения {ComObj.AsInt(args[5])}).");
        MarkSaved(ctx, doc, path);
    }

    private static void TrySet(Ctx ctx, ComObj obj, string property, object value)
    {
        try { obj.Set(property, value); }
        catch (CadException ex) { ctx.Warnings.Add($"{property}: {ex.Message}"); }
    }
}
