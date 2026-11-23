using System.Text;
using SolidBrew.Core.Api;
using SolidBrew.Workspace;

namespace SolidBrew.Cli;

/// <summary>Точка входа <c>sbpdm</c>.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (Console.IsInputRedirected) Console.InputEncoding = Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return await new CliApp(new CliEnvironment()).RunAsync(args, cts.Token);
    }
}

/// <summary>Окружение команды: потоки ввода-вывода, текущая папка, профиль, создание клиента API. Подменяется в тестах.</summary>
public sealed class CliEnvironment
{
    public TextWriter Out { get; init; } = Console.Out;

    /// <summary>Ход выполнения, предупреждения и ошибки.</summary>
    public TextWriter Err { get; init; } = Console.Error;

    public TextReader In { get; init; } = Console.In;

    public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Файл профиля; null — <see cref="ClientProfileStore.DefaultPath"/> (переменная SOLIDBREW_PROFILE или %APPDATA%).</summary>
    public string? ProfilePath { get; init; }

    /// <summary>Клиент API по адресу сервера и токену.</summary>
    public Func<string, string?, IPdmApi> ApiFactory { get; init; } = (url, token) => PdmClientFactory.Create(url, token);

    /// <summary>Скрытый ввод секрета (пароля) с подсказкой; null — с консоли без эха.</summary>
    public Func<string, string?>? ReadSecret { get; init; }

    /// <summary>Подключать адаптер SOLIDWORKS (SOLIDWORKS запускается лениво, только когда нужен).</summary>
    public bool UseSolidWorks { get; init; } = true;
}
