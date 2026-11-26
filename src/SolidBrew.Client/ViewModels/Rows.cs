using System.Text.Json;
using SolidBrew.Client.Infrastructure;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Client.ViewModels;

public sealed class VersionRow
{
    public VersionRow(VersionDto v, int latest)
    {
        Version = v;
        IsLatest = v.Number == latest;
    }

    public VersionDto Version { get; }
    public int Number => Version.Number;
    public bool IsLatest { get; }
    public string Text => $"v{Version.Number}" + (IsLatest ? " (последняя)" : string.Empty);
    public string Author => string.IsNullOrWhiteSpace(Version.AuthorDisplayName) ? Version.AuthorLogin : Version.AuthorDisplayName;
    public string DateText => Format.Time(Version.At);
    public string Comment => Version.Comment ?? string.Empty;
    public string? CadVersion => Version.CadVersion;
    public string RelPath => Version.RelPath;
    public string SizeText => Format.Bytes(Version.Size);
    public string Configurations => string.Join(", ", Version.Configurations);
    public string Sha => Version.Sha256.Length > 12 ? Version.Sha256[..12] + "…" : Version.Sha256;
    public override string ToString() => Text;
}

public sealed record PropertyRow(string Name, string Value);

public sealed class ReferenceRow
{
    public ReferenceRow(VersionRefDto r)
    {
        RefPath = r.RefPath;
        ChildDocumentId = r.ChildDocumentId;
        ChildVersion = r.ChildVersion;
        ChildRelPath = r.ChildRelPath ?? "— не учтён в PDM (отсутствующая ссылка)";
        VersionText = r.ChildVersion is { } v ? (r.ChildLatestVersion is { } l && l != v ? $"v{v} (есть v{l})" : $"v{v}") : string.Empty;
        Mark = r.ChildDocumentId == null ? StateMark.Missing : r.ChildLatestVersion is { } l2 && l2 != r.ChildVersion ? StateMark.Outdated : StateMark.Free;
    }

    public string RefPath { get; }
    public Guid? ChildDocumentId { get; }
    public int? ChildVersion { get; }
    public string ChildRelPath { get; }
    public string VersionText { get; }
    public StateMark Mark { get; }
}

public sealed class WhereUsedRow
{
    public WhereUsedRow(WhereUsedItemDto i, bool thisVersion)
    {
        Item = i;
        IsThisVersion = thisVersion;
    }

    public WhereUsedItemDto Item { get; }
    public bool IsThisVersion { get; }
    public Guid ParentDocumentId => Item.ParentDocumentId;
    public int ParentVersion => Item.ParentVersion;
    public string ParentRelPath => Item.ParentRelPath;
    public string? ParentDesignation => Item.ParentDesignation;
    public string? ParentName => Item.ParentName;
    public string ParentVersionText => $"v{Item.ParentVersion}" + (Item.IsParentLatest ? " (последняя)" : $" (последняя v{Item.ParentLatestVersion})");
    public string ChildVersionText => $"v{Item.ChildVersion}";
    public bool UsesNotLatest => !Item.UsesLatestChild;
    public string ParentTitle => string.IsNullOrWhiteSpace(Item.ParentDesignation) ? Format.FileName(Item.ParentRelPath) : Item.ParentDesignation!;
    public string ParentFileName => Format.FileName(Item.ParentRelPath);
    public string NoteText => Item.UsesLatestChild ? string.Empty : "не последняя версия";
    public StateMark Mark => Item.UsesLatestChild ? StateMark.None : StateMark.Outdated;
}

public sealed class DerivedRow
{
    public DerivedRow(DerivedFileDto d)
    {
        FileName = d.FileName;
        SizeText = Format.Bytes(d.Size);
        Sha = d.Sha256;
    }

    public string FileName { get; }
    public string SizeText { get; }
    public string Sha { get; }
}

public sealed class JournalRow
{
    public JournalRow(JournalEntryDto e, IEnumerable<VersionRow>? versions = null)
    {
        Entry = e;
        DateText = Format.Time(e.At);
        User = e.UserLogin;
        ActionText = ActionName(e.Action);
        VersionText = e.Version is { } v ? $"v{v}" : string.Empty;
        DetailsText = DescribeDetails(e.Details);
    }

    public JournalEntryDto Entry { get; }
    public string DateText { get; }
    public string User { get; }
    public string ActionText { get; }
    public string VersionText { get; }
    public string DetailsText { get; }
    public string DocumentText => Entry.DocumentId?.ToString() ?? string.Empty;

    public static string ActionName(string action) => action switch
    {
        JournalActions.Login => "вход",
        JournalActions.Checkin => "сдача",
        JournalActions.Lock => "взять в работу",
        JournalActions.Unlock => "отменить взятие",
        JournalActions.ForceUnlock => "снятие блокировки администратором",
        JournalActions.MarkUnused => "не используется",
        JournalActions.Deleted => "удалён администратором",
        JournalActions.Export => "выгрузка комплекта",
        JournalActions.UserCreated => "создан пользователь",
        JournalActions.UserUpdated => "изменён пользователь",
        JournalActions.SettingsChanged => "изменены настройки",
        JournalActions.ProjectCreated => "создан проект",
        _ => action,
    };

    private static string DescribeDetails(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(details);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return details;
            var parts = new List<string>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Null => null,
                    _ => p.Value.GetRawText(),
                };
                if (!string.IsNullOrEmpty(value)) parts.Add($"{p.Name}: {value}");
            }
            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return details;
        }
    }
}
