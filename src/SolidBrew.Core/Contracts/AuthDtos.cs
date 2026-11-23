using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Contracts
{
    public sealed class LoginRequest
    {
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; set; }
        public UserDto User { get; set; } = new UserDto();
    }

    public sealed class UserDto
    {
        public Guid Id { get; set; }
        public string Login { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public UserRole Role { get; set; }
        public bool Active { get; set; }
    }

    public sealed class CreateUserRequest
    {
        public string Login { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public UserRole Role { get; set; } = UserRole.Designer;
    }

    /// <summary>Изменение пользователя: null — поле не меняется.</summary>
    public sealed class UpdateUserRequest
    {
        public string? DisplayName { get; set; }
        public string? Password { get; set; }
        public UserRole? Role { get; set; }
        public bool? Active { get; set; }
    }

    public sealed class NotificationDto
    {
        public long Id { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset At { get; set; }
        public bool Seen { get; set; }
    }

    public sealed class ProjectDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ProjectKind Kind { get; set; }
    }

    public sealed class CreateProjectRequest
    {
        public string Name { get; set; } = string.Empty;
        public ProjectKind Kind { get; set; } = ProjectKind.Project;
    }

    /// <summary>Настройки сервера (ключ — значение). Ключи см. <see cref="SettingKeys"/>.</summary>
    public sealed class SettingsDto
    {
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    }

    public static class SettingKeys
    {
        /// <summary>Рабочая версия CAD, например <c>cad.solidworks.workingVersion</c>.</summary>
        public static string WorkingVersion(string cadId) => $"cad.{cadId}.workingVersion";
    }
}
