using System.Text;
using System.Text.Json;
using SolidBrew.Adapters.SolidWorks;
using SolidBrew.Core.Json;

namespace SwProbe;

/// <summary>
/// Прототип адаптера SOLIDWORKS (architecture.md, раздел 5) и исполнитель заданий для машины с SOLIDWORKS.
/// Вывод — JSON (UTF-8) в stdout или в файл <c>--out</c>.
/// </summary>
internal static class Program
{
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions(PdmJson.Options) { WriteIndented = true };

    private const string Usage = """
        SwProbe — прототип адаптера SOLIDWORKS.
          info                                   версия SOLIDWORKS, открытые документы
          read <файл>                            свойства, конфигурации, ссылки, версия файла
          deps <файл> [--traverse]               сырой ответ GetDocumentDependencies2
          scan <папка> [--recursive]             read по всем файлам SOLIDWORKS + сводка
          make-testkit <папка>                   создать тестовый комплект этапа 1
          open-verify <головной файл> <workRoot> открыть и проверить пути компонентов
          close <файл>                           закрыть документ (только открытый этим исполнителем)
          replace-ref <документ> <старый> <новый> заменить ссылку в закрытом документе
          import-bench <головной файл> [папки…]  замер подготовки постановки на учёт: холодный/тёплый прогон, сверка
          serve --jobs <папка>                   исполнитель заданий из <папка>/inbox
        Общие ключи: --out <файл>, --asm-mode selective|lightweight|full, --search (поиск ссылок SOLIDWORKS), --start, --allow-write <папка>
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        ProbeArgs options;
        try { options = ProbeArgs.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (options.Command == "serve")
            return await Serve.RunAsync(options).ConfigureAwait(false);

        using var adapter = new SolidWorksAdapter();
        var result = await Commands.ExecuteAsync(adapter, options, CancellationToken.None).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(result.Output ?? new { error = result.Error }, Json);
        if (options.Out != null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(options.Out));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(options.Out, json, new UTF8Encoding(false));
        }
        Console.WriteLine(json);
        if (result.Error != null) Console.Error.WriteLine(result.Error);
        return result.ExitCode;
    }
}

/// <summary>Разобранная командная строка.</summary>
internal sealed class ProbeArgs
{
    public string Command { get; set; } = string.Empty;
    public List<string> Positional { get; } = new List<string>();
    public string? Out { get; set; }
    public string? Jobs { get; set; }
    public bool Recursive { get; set; }
    public bool Traverse { get; set; }
    public bool Search { get; set; }
    public bool Start { get; set; }
    public SwAssemblyReadMode AsmMode { get; set; } = SwAssemblyReadMode.Selective;
    public List<string> AllowWrite { get; } = new List<string>();

    public static ProbeArgs Parse(IReadOnlyList<string> args)
    {
        var result = new ProbeArgs();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"После «{a}» нужно значение.");
            switch (a)
            {
                case "--out": result.Out = Next(); break;
                case "--jobs": result.Jobs = Next(); break;
                case "--recursive": result.Recursive = true; break;
                case "--traverse": result.Traverse = true; break;
                case "--search": result.Search = true; break;
                case "--no-search": result.Search = false; break;
                case "--start": result.Start = true; break;
                case "--allow-write": result.AllowWrite.Add(Next()); break;
                case "--asm-mode":
                    result.AsmMode = Next().ToLowerInvariant() switch
                    {
                        "selective" => SwAssemblyReadMode.Selective,
                        "lightweight" => SwAssemblyReadMode.Lightweight,
                        "full" => SwAssemblyReadMode.Full,
                        var other => throw new ArgumentException($"Неизвестный режим «{other}»."),
                    };
                    break;
                default:
                    if (result.Command.Length == 0) result.Command = a.ToLowerInvariant();
                    else result.Positional.Add(a);
                    break;
            }
        }
        return result;
    }
}
