using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Cad
{
    /// <summary>Извлечение обозначения и наименования из свойств версии.</summary>
    public static class DocumentProperties
    {
        /// <summary>Имена свойства «обозначение» в порядке приоритета.</summary>
        public static readonly IReadOnlyList<string> DesignationKeys = new[] { "Обозначение", "Designation", "PartNo", "Номер" };

        /// <summary>Имена свойства «наименование» в порядке приоритета.</summary>
        public static readonly IReadOnlyList<string> NameKeys = new[] { "Наименование", "Name", "Description" };

        public static string? Designation(Dictionary<string, Dictionary<string, string>>? properties) => Find(properties, DesignationKeys);

        public static string? Name(Dictionary<string, Dictionary<string, string>>? properties) => Find(properties, NameKeys);

        /// <summary>
        /// Ищет первое непустое значение среди <paramref name="keys"/> (без учёта регистра):
        /// сначала на уровне файла («»), затем в первой конфигурации, где оно есть.
        /// </summary>
        public static string? Find(Dictionary<string, Dictionary<string, string>>? properties, IReadOnlyList<string> keys)
        {
            if (properties == null || properties.Count == 0) return null;

            if (properties.TryGetValue(string.Empty, out var fileLevel))
            {
                var value = FindIn(fileLevel, keys);
                if (value != null) return value;
            }

            foreach (var pair in properties)
            {
                if (pair.Key.Length == 0) continue;
                var value = FindIn(pair.Value, keys);
                if (value != null) return value;
            }
            return null;
        }

        private static string? FindIn(Dictionary<string, string>? props, IReadOnlyList<string> keys)
        {
            if (props == null || props.Count == 0) return null;
            foreach (var key in keys)
            {
                foreach (var pair in props)
                {
                    if (string.Equals(pair.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                        return pair.Value.Trim();
                }
            }
            return null;
        }
    }
}
