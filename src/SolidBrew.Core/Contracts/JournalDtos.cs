using System;

namespace SolidBrew.Core.Contracts
{
    public sealed class JournalEntryDto
    {
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public string UserLogin { get; set; } = string.Empty;
        /// <summary>Действие, см. <see cref="JournalActions"/>.</summary>
        public string Action { get; set; } = string.Empty;
        public Guid? DocumentId { get; set; }
        public int? Version { get; set; }
        /// <summary>Подробности — JSON-строка или null.</summary>
        public string? Details { get; set; }
    }

    public static class JournalActions
    {
        public const string Login = "login";
        public const string Checkin = "checkin";
        public const string Lock = "lock";
        public const string Unlock = "unlock";
        public const string ForceUnlock = "forceUnlock";
        public const string MarkUnused = "markUnused";
        public const string Deleted = "deleted";
        public const string Export = "export";
        public const string UserCreated = "userCreated";
        public const string UserUpdated = "userUpdated";
        public const string SettingsChanged = "settingsChanged";
        public const string ProjectCreated = "projectCreated";
    }
}
