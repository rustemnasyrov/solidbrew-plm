using System.Globalization;

namespace SolidBrew.Cli;

/// <summary>Ошибка в командной строке или в ходе команды с понятным сообщением и кодом выхода.</summary>
public sealed class CliException : Exception
{
    public CliException(string message, int exitCode = CliApp.Failed) : base(message) => ExitCode = exitCode;

    public int ExitCode { get; }
}

/// <summary>
/// Разобранные аргументы команды: позиционные, флаги (<c>--yes</c>) и параметры со значением
/// (<c>--dir X</c> или <c>--dir=X</c>; повторяемые — <c>--include a --include b</c>). Всё после <c>--</c> — позиционное.
/// </summary>
public sealed class CliArgs
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = new();

    private static readonly Dictionary<string, string> ShortAliases = new(StringComparer.Ordinal)
    {
        ["-y"] = "yes",
        ["-h"] = "help",
        ["-?"] = "help",
        ["-v"] = "verbose",
    };

    /// <summary>Разобрать токены. Неизвестный параметр — <see cref="CliException"/>.</summary>
    public static CliArgs Parse(IEnumerable<string> tokens, ICollection<string> flags, ICollection<string> valueOptions)
    {
        var result = new CliArgs();
        var list = tokens.ToList();
        var rest = false;
        for (var i = 0; i < list.Count; i++)
        {
            var token = list[i];
            if (rest || token == "-" || !token.StartsWith('-'))
            {
                result.Positionals.Add(token);
                continue;
            }
            if (token == "--")
            {
                rest = true;
                continue;
            }

            string name;
            string? inline = null;
            if (ShortAliases.TryGetValue(token, out var alias))
            {
                name = alias;
            }
            else if (token.StartsWith("--", StringComparison.Ordinal))
            {
                name = token.Substring(2);
                var eq = name.IndexOf('=');
                if (eq >= 0)
                {
                    inline = name.Substring(eq + 1);
                    name = name.Substring(0, eq);
                }
            }
            else
            {
                throw new CliException($"Неизвестный параметр «{token}». Справка: sbpdm help.");
            }

            if (flags.Contains(name))
            {
                if (inline != null) throw new CliException($"Параметр --{name} не принимает значения.");
                result._flags.Add(name);
            }
            else if (valueOptions.Contains(name))
            {
                if (inline == null)
                {
                    if (i + 1 >= list.Count) throw new CliException($"Для параметра --{name} не указано значение.");
                    inline = list[++i];
                }
                if (!result._values.TryGetValue(name, out var values)) result._values[name] = values = new List<string>();
                values.Add(inline);
            }
            else
            {
                throw new CliException($"Неизвестный параметр «{token}» для этой команды. Справка: sbpdm help <команда>.");
            }
        }
        return result;
    }

    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>Последнее значение параметра или null.</summary>
    public string? Value(string name) => _values.TryGetValue(name, out var v) && v.Count > 0 ? v[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var v) ? v : (IReadOnlyList<string>)Array.Empty<string>();

    public int? Int(string name)
    {
        var text = Value(name);
        if (text == null) return null;
        if (text.StartsWith('v') || text.StartsWith('V')) text = text.Substring(1);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
            throw new CliException($"--{name}: ожидается положительное число, указано «{Value(name)}».");
        return n;
    }

    /// <summary>Позиционный аргумент по номеру или null.</summary>
    public string? At(int index) => index < Positionals.Count ? Positionals[index] : null;
}
