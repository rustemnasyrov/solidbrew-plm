using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Contracts
{
    /// <summary>Комплект: замкнутое дерево выбранной версии головного документа.</summary>
    public sealed class KitDto
    {
        public Guid HeadDocumentId { get; set; }
        public int HeadVersion { get; set; }
        public List<KitItemDto> Items { get; set; } = new List<KitItemDto>();
        public List<KitEdgeDto> Edges { get; set; } = new List<KitEdgeDto>();
        public List<KitUnresolvedRefDto> Unresolved { get; set; } = new List<KitUnresolvedRefDto>();
    }

    public sealed class KitItemDto
    {
        public Guid DocumentId { get; set; }
        public int Version { get; set; }
        public int LatestVersion { get; set; }
        public string RelPath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Cad { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public string? Designation { get; set; }
        public string? Name { get; set; }
        public DocumentStatus Status { get; set; }
        public LockDto? Lock { get; set; }
        public Guid ProjectId { get; set; }
    }

    public sealed class KitEdgeDto
    {
        public Guid ParentDocumentId { get; set; }
        public int ParentVersion { get; set; }
        public Guid ChildDocumentId { get; set; }
        public int ChildVersion { get; set; }
        public string RefPath { get; set; } = string.Empty;
    }

    public sealed class KitUnresolvedRefDto
    {
        public Guid ParentDocumentId { get; set; }
        public int ParentVersion { get; set; }
        public string RefPath { get; set; } = string.Empty;
    }

    /// <summary>«Где применяется»: версии родителей, ссылающиеся на версию документа.</summary>
    public sealed class WhereUsedDto
    {
        public List<WhereUsedItemDto> Items { get; set; } = new List<WhereUsedItemDto>();
    }

    /// <summary>
    /// Применение документа. Ответ /where-used содержит применения ВСЕХ версий документа;
    /// <see cref="ChildVersion"/> — какую версию использует родитель. Клиент выделяет запрошенную версию.
    /// </summary>
    public sealed class WhereUsedItemDto
    {
        /// <summary>Версия запрошенного документа, на которую ссылается родитель.</summary>
        public int ChildVersion { get; set; }
        public Guid ParentDocumentId { get; set; }
        public int ParentVersion { get; set; }
        public string ParentRelPath { get; set; } = string.Empty;
        public string? ParentDesignation { get; set; }
        public string? ParentName { get; set; }
        public int ParentLatestVersion { get; set; }
        /// <summary>Эта версия родителя — последняя.</summary>
        public bool IsParentLatest { get; set; }
        /// <summary>Родитель ссылается на последнюю версию запрошенного документа.</summary>
        public bool UsesLatestChild { get; set; }
    }

    /// <summary>Перечень файлов выгрузки; кладётся в ZIP как <c>manifest.json</c>.</summary>
    public sealed class ExportManifest
    {
        /// <summary>Имя файла перечня внутри ZIP.</summary>
        public const string FileName = "manifest.json";

        public Guid HeadDocumentId { get; set; }
        public int HeadVersion { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public List<ExportManifestFile> Files { get; set; } = new List<ExportManifestFile>();
    }

    public sealed class ExportManifestFile
    {
        public string RelPath { get; set; } = string.Empty;
        public Guid DocumentId { get; set; }
        public int Version { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
        /// <summary>Производный файл (PDF, STEP…), а не исходный документ.</summary>
        public bool Derived { get; set; }
    }
}
