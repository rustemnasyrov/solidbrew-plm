using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Contracts
{
    /// <summary>Сдача: атомарная публикация новых версий нескольких документов.</summary>
    public sealed class CheckinRequest
    {
        public Guid ProjectId { get; set; }
        public string? Comment { get; set; }
        /// <summary>«Сдать и оставить в работе»: блокировки не снимаются.</summary>
        public bool KeepLocks { get; set; }
        /// <summary>Разрешить отсутствующие ссылки.</summary>
        public bool AllowUnresolvedReferences { get; set; }
        /// <summary>Разрешить файлы, сохранённые в версии CAD новее рабочей.</summary>
        public bool AcceptNewerCadVersion { get; set; }
        public List<CheckinItem> Items { get; set; } = new List<CheckinItem>();
    }

    /// <summary>
    /// Документ в сдаче. Новый документ: задан <see cref="TempKey"/>, нет <see cref="DocumentId"/> и <see cref="BaseVersion"/>.
    /// Существующий: заданы <see cref="DocumentId"/> и <see cref="BaseVersion"/> (последняя версия, от которой шла правка).
    /// </summary>
    public sealed class CheckinItem
    {
        public Guid? DocumentId { get; set; }
        public string? TempKey { get; set; }
        public int? BaseVersion { get; set; }
        public string RelPath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Cad { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        public string? CadVersion { get; set; }
        public int? CadVersionMajor { get; set; }
        public List<string> Configurations { get; set; } = new List<string>();
        public Dictionary<string, Dictionary<string, string>> Properties { get; set; } = new Dictionary<string, Dictionary<string, string>>();
        public List<CheckinReference> References { get; set; } = new List<CheckinReference>();
        public List<DerivedFileDto> Derived { get; set; } = new List<DerivedFileDto>();
    }

    /// <summary>
    /// Ссылка документа в сдаче:
    /// DocumentId + Version — существующая опубликованная версия;
    /// DocumentId без Version — новая версия этого документа из этой же сдачи;
    /// TempKey — новый документ из этой сдачи;
    /// ничего не задано — отсутствующая ссылка.
    /// </summary>
    public sealed class CheckinReference
    {
        public string RefPath { get; set; } = string.Empty;
        public Guid? DocumentId { get; set; }
        public int? Version { get; set; }
        public string? TempKey { get; set; }
    }

    public sealed class CheckinResponse
    {
        public long CheckinId { get; set; }
        public List<CheckinResultItem> Items { get; set; } = new List<CheckinResultItem>();
    }

    public sealed class CheckinResultItem
    {
        public string? TempKey { get; set; }
        public Guid DocumentId { get; set; }
        public int Version { get; set; }
        public string RelPath { get; set; } = string.Empty;
    }
}
