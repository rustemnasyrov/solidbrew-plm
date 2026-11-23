using System.Globalization;
using System.Text;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Adapters.TestCad;

/// <summary>
/// Разбор текстового формата TestCad. Файл — UTF-8, по строке на запись:
/// <c>cadver: 2018</c>, <c>config: имя</c>, <c>prop: ключ=значение</c>, <c>prop: конфигурация|ключ=значение</c>,
/// <c>ref: путь</c>. Остальные строки — «геометрия», они не разбираются.
/// </summary>
public static class TestCadFormat
{
    public const string PartExtension = ".tcprt";
    public const string AssemblyExtension = ".tcasm";
    public const string DrawingExtension = ".tcdrw";

    internal const string CadVerPrefix = "cadver:";
    internal const string ConfigPrefix = "config:";
    internal const string PropPrefix = "prop:";
    internal const string RefPrefix = "ref:";

    /// <summary>Вид документа по расширению; Other — не документ TestCad.</summary>
    public static DocumentKind KindOf(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            PartExtension => DocumentKind.Part,
            AssemblyExtension => DocumentKind.Assembly,
            DrawingExtension => DocumentKind.Drawing,
            _ => DocumentKind.Other,
        };
    }

    /// <summary>Разобрать текст файла.</summary>
    public static CadDocumentInfo Parse(string path, string text)
    {
        var info = new CadDocumentInfo { Path = path, Kind = KindOf(path) };
        var refs = new List<string>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (TryValue(line, CadVerPrefix, out var ver))
            {
                info.CadVersion = "TestCad " + ver;
                if (int.TryParse(ver, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
                    info.CadVersionMajor = major;
            }
            else if (TryValue(line, ConfigPrefix, out var config))
            {
                if (config.Length > 0 && !info.Configurations.Contains(config))
                    info.Configurations.Add(config);
            }
            else if (TryValue(line, PropPrefix, out var prop))
            {
                var eq = prop.IndexOf('=');
                if (eq <= 0) continue;
                var key = prop.Substring(0, eq).Trim();
                var value = prop.Substring(eq + 1).Trim();
                var scope = string.Empty;
                var bar = key.IndexOf('|');
                if (bar >= 0)
                {
                    scope = key.Substring(0, bar).Trim();
                    key = key.Substring(bar + 1).Trim();
                }
                if (key.Length == 0) continue;
                if (!info.Properties.TryGetValue(scope, out var dict))
                    info.Properties[scope] = dict = new Dictionary<string, string>();
                dict[key] = value;
            }
            else if (TryValue(line, RefPrefix, out var reference))
            {
                if (reference.Length > 0 && !refs.Contains(reference, StringComparer.OrdinalIgnoreCase))
                    refs.Add(reference);
            }
        }

        foreach (var saved in refs)
        {
            info.References.Add(new CadReference
            {
                SavedPath = saved,
                FileName = CadReference.FileNameOf(saved),
                Configurations = new List<string>(info.Configurations),
            });
        }
        return info;
    }

    internal static bool TryValue(string trimmedLine, string prefix, out string value)
    {
        if (trimmedLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = trimmedLine.Substring(prefix.Length).Trim();
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}

/// <summary>Содержимое файла TestCad для создания в тестах и демонстрациях.</summary>
public sealed class TestCadDocument
{
    /// <summary>Год версии CAD («cadver»); null — строка не пишется.</summary>
    public int? CadVersion { get; set; } = 2018;
    public List<string> Configurations { get; } = new();
    /// <summary>Свойства: ключ — конфигурация («» — уровень файла).</summary>
    public Dictionary<string, Dictionary<string, string>> Properties { get; } = new();
    /// <summary>Ссылки как они будут записаны в файл (абсолютные или относительные пути).</summary>
    public List<string> References { get; } = new();
    /// <summary>Строки «геометрии» — произвольный текст.</summary>
    public List<string> Geometry { get; } = new();

    public TestCadDocument Config(params string[] names)
    {
        Configurations.AddRange(names);
        return this;
    }

    public TestCadDocument Prop(string key, string value, string configuration = "")
    {
        if (!Properties.TryGetValue(configuration, out var dict))
            Properties[configuration] = dict = new Dictionary<string, string>();
        dict[key] = value;
        return this;
    }

    public TestCadDocument Ref(params string[] savedPaths)
    {
        References.AddRange(savedPaths);
        return this;
    }

    public TestCadDocument Geom(params string[] lines)
    {
        Geometry.AddRange(lines);
        return this;
    }

    /// <summary>Текст файла.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        if (CadVersion != null) sb.Append("cadver: ").Append(CadVersion.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        foreach (var c in Configurations) sb.Append("config: ").Append(c).Append("\r\n");
        foreach (var scope in Properties)
        {
            foreach (var p in scope.Value)
            {
                sb.Append("prop: ");
                if (scope.Key.Length > 0) sb.Append(scope.Key).Append('|');
                sb.Append(p.Key).Append('=').Append(p.Value).Append("\r\n");
            }
        }
        foreach (var r in References) sb.Append("ref: ").Append(r).Append("\r\n");
        foreach (var g in Geometry) sb.Append(g).Append("\r\n");
        return sb.ToString();
    }
}

/// <summary>Помощник для тестов и демонстраций: создание файлов TestCad.</summary>
public static class TestCadWriter
{
    /// <summary>Записать документ в файл (папки создаются). Возвращает полный путь.</summary>
    public static string Write(string path, TestCadDocument document)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, document.ToText(), TestCadFormat.Utf8NoBom);
        return full;
    }

    /// <summary>
    /// Создать файл с заданными ссылками, свойствами уровня файла, конфигурациями и строкой геометрии.
    /// </summary>
    public static string Create(string path, IEnumerable<string>? references = null,
        IDictionary<string, string>? properties = null, IEnumerable<string>? configurations = null,
        int? cadVersion = 2018, string? geometry = null)
    {
        var doc = new TestCadDocument { CadVersion = cadVersion };
        if (configurations != null) doc.Configurations.AddRange(configurations);
        if (properties != null)
        {
            foreach (var p in properties) doc.Prop(p.Key, p.Value);
        }
        if (references != null) doc.References.AddRange(references);
        doc.Geometry.Add(geometry ?? "geometry: " + Path.GetFileNameWithoutExtension(path));
        return Write(path, doc);
    }

    /// <summary>Дописать строку «геометрии» — имитация правки модели (меняет байты, не меняет ссылки).</summary>
    public static void AppendGeometry(string path, string line)
    {
        File.AppendAllText(path, line + "\r\n", TestCadFormat.Utf8NoBom);
    }
}
