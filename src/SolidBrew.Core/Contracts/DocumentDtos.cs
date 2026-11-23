using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Contracts
{
    /// <summary>Текущая блокировка документа («взят в работу»).</summary>
    public sealed class LockDto
    {
        public Guid DocumentId { get; set; }
        public Guid UserId { get; set; }
        public string UserLogin { get; set; } = string.Empty;
        public string UserDisplayName { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }
    }

    /// <summary>Краткие сведения о документе (строка поиска, заголовок карточки).</summary>
    public sealed class DocumentSummaryDto
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public ProjectKind ProjectKind { get; set; }
        /// <summary>Идентификатор адаптера CAD: «solidworks», «testcad».</summary>
        public string Cad { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        public DocumentStatus Status { get; set; }
        /// <summary>Относительный путь последней версии (разделитель «/»).</summary>
        public string RelPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int LatestVersion { get; set; }
        public string? Designation { get; set; }
        public string? Name { get; set; }
        public string LatestAuthorLogin { get; set; } = string.Empty;
        public string LatestAuthorDisplayName { get; set; } = string.Empty;
        public DateTimeOffset LatestAt { get; set; }
        public LockDto? Lock { get; set; }
    }

    /// <summary>Опубликованная версия документа.</summary>
    public sealed class VersionDto
    {
        public Guid DocumentId { get; set; }
        public int Number { get; set; }
        public string AuthorLogin { get; set; } = string.Empty;
        public string AuthorDisplayName { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }
        public long CheckinId { get; set; }
        public string? Comment { get; set; }
        /// <summary>SHA-256 содержимого, hex в нижнем регистре.</summary>
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
        public string RelPath { get; set; } = string.Empty;
        public string Cad { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        /// <summary>Версия CAD, в которой сохранён файл, текстом.</summary>
        public string? CadVersion { get; set; }
        /// <summary>Основной номер версии CAD для сравнения (например 2022).</summary>
        public int? CadVersionMajor { get; set; }
        public List<string> Configurations { get; set; } = new List<string>();
        /// <summary>Свойства: ключ — имя конфигурации, «» — свойства уровня файла.</summary>
        public Dictionary<string, Dictionary<string, string>> Properties { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public List<VersionRefDto> References { get; set; } = new List<VersionRefDto>();
        public List<DerivedFileDto> Derived { get; set; } = new List<DerivedFileDto>();
    }

    /// <summary>Ссылка версии на другой документ. Child* = null — отсутствующая ссылка.</summary>
    public sealed class VersionRefDto
    {
        /// <summary>Путь ссылки, как он записан в файле.</summary>
        public string RefPath { get; set; } = string.Empty;
        public Guid? ChildDocumentId { get; set; }
        public int? ChildVersion { get; set; }
        public string? ChildRelPath { get; set; }
        public int? ChildLatestVersion { get; set; }
    }

    /// <summary>Производный файл версии (PDF, STEP, DXF…).</summary>
    public sealed class DerivedFileDto
    {
        public string FileName { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public sealed class DocumentDetailDto
    {
        public DocumentSummaryDto Document { get; set; } = new DocumentSummaryDto();
        /// <summary>Версии по убыванию номера.</summary>
        public List<VersionDto> Versions { get; set; } = new List<VersionDto>();
    }

    public sealed class MarkUnusedRequest
    {
        public bool Unused { get; set; }
    }

    public sealed class MissingObjectsRequest
    {
        public List<string> Hashes { get; set; } = new List<string>();
    }

    public sealed class MissingObjectsResponse
    {
        public List<string> Missing { get; set; } = new List<string>();
    }
}
