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

        /// <summary>
        /// Служебное свойство уровня файла: имена файлов внешних ссылок, убранных через PDM («Удалить ссылку»), через «|».
        /// CAD не всегда умеет удалить ссылку из файла (SOLIDWORKS у детали её только разрывает) — PDM такие ссылки не учитывает.
        /// </summary>
        public const string RemovedReferencesKey = "PDM_Убранные_ссылки";

        /// <summary>Имена файлов убранных ссылок (из <see cref="RemovedReferencesKey"/>).</summary>
        public static List<string> RemovedReferences(Dictionary<string, Dictionary<string, string>>? properties)
        {
            var result = new List<string>();
            if (properties == null || !properties.TryGetValue(string.Empty, out var file) || file == null) return result;
            foreach (var pair in file)
            {
                if (!string.Equals(pair.Key?.Trim(), RemovedReferencesKey, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                foreach (var part in pair.Value.Split('|'))
                {
                    var name = part.Trim();
                    if (name.Length > 0 && !result.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) result.Add(name);
                }
            }
            return result;
        }

        /// <summary>Значение <see cref="RemovedReferencesKey"/> с добавленными именами файлов.</summary>
        public static string WithRemovedReferences(Dictionary<string, Dictionary<string, string>>? properties, IEnumerable<string> fileNames)
        {
            var names = RemovedReferences(properties);
            foreach (var n in fileNames)
            {
                var name = n.Trim();
                if (name.Length > 0 && !names.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) names.Add(name);
            }
            return string.Join("|", names);
        }

        /// <summary>Убрать из прочитанных ссылок документа те, что убраны через PDM (по имени файла).</summary>
        public static void ApplyRemovedReferences(CadDocumentInfo info)
        {
            if (info == null) return;
            var removed = RemovedReferences(info.Properties);
            if (removed.Count == 0) return;
            info.References = info.References.FindAll(r => !removed.Exists(n => string.Equals(n, CadReference.FileNameOf(r.SavedPath), StringComparison.OrdinalIgnoreCase)));
        }

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
