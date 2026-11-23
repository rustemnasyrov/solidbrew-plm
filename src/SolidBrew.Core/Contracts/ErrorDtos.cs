using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Contracts
{
    /// <summary>Тело всех ответов 4xx.</summary>
    public sealed class ErrorResponse
    {
        /// <summary>Понятное пользователю сообщение на русском.</summary>
        public string Message { get; set; } = string.Empty;
        public List<ProblemDto> Problems { get; set; } = new List<ProblemDto>();
        /// <summary>Мешающая блокировка (например, документ взят другим).</summary>
        public LockDto? Lock { get; set; }
    }

    /// <summary>Отдельная проблема, найденная сервером (обычно при сдаче).</summary>
    public sealed class ProblemDto
    {
        /// <summary>Код проблемы, см. <see cref="ProblemCodes"/>.</summary>
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public Guid? DocumentId { get; set; }
        public string? TempKey { get; set; }
        public string? RelPath { get; set; }
    }

    public static class ProblemCodes
    {
        public const string NotLocked = "notLocked";
        public const string LockedByOther = "lockedByOther";
        public const string StaleBaseVersion = "staleBaseVersion";
        public const string MissingDependency = "missingDependency";
        public const string UnresolvedReference = "unresolvedReference";
        public const string MissingObject = "missingObject";
        public const string HashMismatch = "hashMismatch";
        public const string DuplicatePath = "duplicatePath";
        public const string LibraryReadOnly = "libraryReadOnly";
        public const string NewerCadVersion = "newerCadVersion";
        public const string Forbidden = "forbidden";
        public const string NotFound = "notFound";
        public const string Validation = "validation";
        public const string ReferencedByPublished = "referencedByPublished";
    }
}
