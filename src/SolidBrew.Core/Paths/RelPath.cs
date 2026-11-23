using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SolidBrew.Core.Paths
{
    /// <summary>
    /// Относительные пути документов в хранилище: разделитель «/», без «..», без ведущего «/»,
    /// без пустых сегментов, Unicode NFC. Пример: <c>Сборки/АИРТ.467124.016 Имитатор центрального пульта.SLDASM</c>.
    /// </summary>
    public static class RelPath
    {
        public const char Separator = '/';

        private static readonly char[] InvalidChars = { '<', '>', ':', '"', '|', '?', '*' };

        /// <summary>
        /// Привести путь к нормальному виду: «\» → «/», убрать пустые сегменты и «.», NFC.
        /// Бросает <see cref="ArgumentException"/>, если путь пустой, абсолютный, содержит «..» или недопустимые символы.
        /// </summary>
        public static string Normalize(string path)
        {
            if (!TryNormalize(path, out var normalized, out var error))
                throw new ArgumentException(error, nameof(path));
            return normalized;
        }

        /// <summary>Как <see cref="Normalize"/>, но без исключения.</summary>
        public static bool TryNormalize(string? path, out string normalized, out string error)
        {
            normalized = string.Empty;
            error = string.Empty;
            if (path == null || path.Trim().Length == 0)
            {
                error = "Пустой относительный путь.";
                return false;
            }

            var text = path.Normalize(NormalizationForm.FormC).Replace('\\', Separator);
            if (text[0] == Separator)
            {
                error = $"Путь «{path}» должен быть относительным.";
                return false;
            }

            var segments = new List<string>();
            foreach (var segment in text.Split(Separator))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    error = $"Путь «{path}» не должен выходить за пределы папки («..»).";
                    return false;
                }
                if (segment.IndexOfAny(InvalidChars) >= 0 || HasControlChars(segment))
                {
                    error = $"Путь «{path}» содержит недопустимые символы.";
                    return false;
                }
                if (segment.Trim().Length == 0 || segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
                {
                    error = $"Имя «{segment}» в пути «{path}» не может оканчиваться пробелом или точкой.";
                    return false;
                }
                segments.Add(segment);
            }

            if (segments.Count == 0)
            {
                error = "Пустой относительный путь.";
                return false;
            }

            normalized = string.Join(Separator.ToString(), segments);
            return true;
        }

        /// <summary>Путь уже в нормальном виде (совпадает с результатом <see cref="Normalize"/>).</summary>
        public static bool IsValid(string? path) =>
            TryNormalize(path, out var normalized, out _) && string.Equals(normalized, path, StringComparison.Ordinal);

        /// <summary>Полный путь Windows: корень рабочей папки + относительный путь.</summary>
        public static string Combine(string root, string rel)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Не задана корневая папка.", nameof(root));
            var normalized = Normalize(rel);
            return Path.GetFullPath(Path.Combine(root, normalized.Replace(Separator, Path.DirectorySeparatorChar)));
        }

        /// <summary>
        /// Относительный путь файла внутри корня. Бросает <see cref="ArgumentException"/>, если файл вне корня.
        /// Сравнение без учёта регистра (Windows).
        /// </summary>
        public static string GetRelative(string root, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Не задана корневая папка.", nameof(root));
            if (string.IsNullOrWhiteSpace(fullPath)) throw new ArgumentException("Не задан путь файла.", nameof(fullPath));

            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || full.Length == rootFull.Length)
                throw new ArgumentException($"Файл «{fullPath}» находится вне рабочей папки «{root}».", nameof(fullPath));

            return Normalize(full.Substring(rootFull.Length));
        }

        /// <summary>Ключ сравнения путей без учёта регистра.</summary>
        public static string Key(string rel) => Normalize(rel).ToUpperInvariant();

        /// <summary>Имя файла — последний сегмент относительного пути.</summary>
        public static string FileName(string rel)
        {
            var normalized = Normalize(rel);
            var i = normalized.LastIndexOf(Separator);
            return i < 0 ? normalized : normalized.Substring(i + 1);
        }

        private static bool HasControlChars(string s)
        {
            foreach (var c in s)
            {
                if (c < ' ') return true;
            }
            return false;
        }
    }
}
