using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidBrew.Adapters.SolidWorks;

/// <summary>
/// Версия SOLIDWORKS, в которой сохранён файл, по результату <c>ISldWorks.VersionHistory</c>.
/// Метод возвращает историю сохранений, по записи на версию формата: «9000[2016/286]», «11000[2018/1,2018/211]».
/// Берём последнюю запись истории.
/// </summary>
public static class SwVersion
{
    private static readonly Regex Number = new Regex(@"\d+", RegexOptions.CultureInvariant);
    private static readonly Regex BracketYear = new Regex(@"[\[,]\s*((?:19|20)\d\d)\s*/", RegexOptions.CultureInvariant);

    /// <summary>Год версии SOLIDWORKS последнего сохранения или null, если разобрать не удалось.</summary>
    public static int? LastSavedYear(IReadOnlyList<string> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var year = YearOf(history[i]);
            if (year != null) return year;
        }
        return null;
    }

    /// <summary>
    /// Год по одной записи истории. SOLIDWORKS 2018 возвращает записи вида «11000[2018/1,2018/211]»:
    /// номер формата файла, затем в скобках «год/сборка» программ, сохранявших файл. Берём наибольший год в скобках;
    /// если скобок нет — четырёхзначный год в тексте («[SldWorks 2018]»).
    /// </summary>
    public static int? YearOf(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        int? best = null;
        foreach (Match m in BracketYear.Matches(entry))
        {
            var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (best == null || y > best) best = y;
        }
        if (best != null) return best;
        foreach (Match m in Number.Matches(entry))
        {
            if (int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1995 && n <= 2100 && (best == null || n > best))
                best = n;
        }
        return best;
    }

    /// <summary>Подпись версии для пользователя: «SOLIDWORKS 2018».</summary>
    public static string? Display(int? year) => year == null ? null : "SOLIDWORKS " + year.Value.ToString(CultureInfo.InvariantCulture);
}
