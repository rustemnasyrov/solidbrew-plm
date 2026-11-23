using System;
using System.Collections.Generic;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Core.Cad
{
    /// <summary>Сведения, прочитанные адаптером из файла CAD.</summary>
    public sealed class CadDocumentInfo
    {
        public string Path { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public List<string> Configurations { get; set; } = new List<string>();
        /// <summary>Свойства: ключ — имя конфигурации, «» — свойства уровня файла.</summary>
        public Dictionary<string, Dictionary<string, string>> Properties { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public List<CadReference> References { get; set; } = new List<CadReference>();
    }

    /// <summary>Ссылка из файла CAD на другой файл.</summary>
    public sealed class CadReference
    {
        /// <summary>Путь, записанный в файле. Бывает абсолютным путём на другом компьютере.</summary>
        public string SavedPath { get; set; } = string.Empty;
        /// <summary>Имя файла — последняя часть <see cref="SavedPath"/>.</summary>
        public string FileName { get; set; } = string.Empty;
        /// <summary>Конфигурации, в которых используется ссылка.</summary>
        public List<string> Configurations { get; set; } = new List<string>();

        /// <summary>Последняя часть пути с любым разделителем («\» или «/»), независимо от ОС.</summary>
        public static string FileNameOf(string savedPath)
        {
            if (savedPath == null) throw new ArgumentNullException(nameof(savedPath));
            var i = savedPath.LastIndexOfAny(new[] { '\\', '/' });
            return i < 0 ? savedPath : savedPath.Substring(i + 1);
        }
    }

    /// <summary>Результат открытия документа в CAD с проверкой источников компонентов.</summary>
    public sealed class OpenReport
    {
        public string HeadPath { get; set; } = string.Empty;
        public bool Ok { get; set; }
        public List<OpenConfigurationReport> Configurations { get; set; } = new List<OpenConfigurationReport>();
        /// <summary>Найденные проблемы понятным языком.</summary>
        public List<string> Problems { get; set; } = new List<string>();
    }

    public sealed class OpenConfigurationReport
    {
        public string Name { get; set; } = string.Empty;
        public List<LoadedComponent> Components { get; set; } = new List<LoadedComponent>();
    }

    public sealed class LoadedComponent
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>Путь, откуда CAD реально загрузил компонент.</summary>
        public string LoadedPath { get; set; } = string.Empty;
        /// <summary>Компонент загружен из рабочей папки.</summary>
        public bool InsideWorkRoot { get; set; }
    }

    /// <summary>Ошибка адаптера CAD с понятным пользователю сообщением (CAD не установлен, файл не читается и т. п.).</summary>
    public class CadException : Exception
    {
        public CadException(string message) : base(message) { }

        public CadException(string message, Exception innerException) : base(message, innerException) { }
    }
}
