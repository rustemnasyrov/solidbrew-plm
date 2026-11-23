using System.Text.Json;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Api.Db;

/// <summary>Преобразования значений между БД (текст, jsonb, timestamptz) и контрактами.</summary>
internal static class DbText
{
    public static string Role(UserRole role) => role switch
    {
        UserRole.Admin => "admin",
        UserRole.Designer => "designer",
        UserRole.Reader => "reader",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static UserRole Role(string role) => role switch
    {
        "admin" => UserRole.Admin,
        "designer" => UserRole.Designer,
        "reader" => UserRole.Reader,
        _ => throw new InvalidDataException($"Неизвестная роль «{role}» в БД."),
    };

    public static string Kind(DocumentKind kind) => kind switch
    {
        DocumentKind.Part => "part",
        DocumentKind.Assembly => "assembly",
        DocumentKind.Drawing => "drawing",
        DocumentKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DocumentKind Kind(string kind) => kind switch
    {
        "part" => DocumentKind.Part,
        "assembly" => DocumentKind.Assembly,
        "drawing" => DocumentKind.Drawing,
        _ => DocumentKind.Other,
    };

    public static string ProjectKind(ProjectKind kind) => kind == Core.Contracts.ProjectKind.Library ? "library" : "project";

    public static ProjectKind ProjectKind(string kind) => kind == "library" ? Core.Contracts.ProjectKind.Library : Core.Contracts.ProjectKind.Project;

    public static DocumentStatus Status(string status) => status == "unused" ? DocumentStatus.Unused : DocumentStatus.Active;

    public static DateTimeOffset Time(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static readonly JsonSerializerOptions JsonOptions = new();

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T FromJson<T>(string? json) where T : new() =>
        string.IsNullOrEmpty(json) ? new T() : JsonSerializer.Deserialize<T>(json, JsonOptions) ?? new T();
}
