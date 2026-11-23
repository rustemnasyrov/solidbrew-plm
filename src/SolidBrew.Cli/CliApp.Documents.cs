using System.IO.Compression;
using System.Text.Json;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Json;
using SolidBrew.Workspace;

namespace SolidBrew.Cli;

public sealed partial class CliApp
{
    private async Task<int> SearchAsync()
    {
        var text = _args.Positionals.Count > 0 ? string.Join(" ", _args.Positionals) : null;
        Guid? projectId = _args.Value("project") is { } p ? (await ResolveProjectAsync(p).ConfigureAwait(false)).Id : null;
        var found = await Api.SearchDocumentsAsync(text, projectId, _args.Value("author"), _args.Flag("all"), _args.Int("limit") ?? 200, _ct).ConfigureAwait(false);
        Emit(found, () =>
        {
            if (found.Count == 0)
            {
                Out.WriteLine("Ничего не найдено.");
                return;
            }
            Table(new[] { "Обозначение", "Наименование", "Файл", "Версия", "Автор", "Проект", "Состояние", "ID" }, found.Select(d => new[]
            {
                d.Designation, d.Name, d.RelPath, "v" + d.LatestVersion, d.LatestAuthorDisplayName, d.ProjectName,
                LockText(d.Lock) + (d.Status == DocumentStatus.Unused ? ", не используется" : ""), d.Id.ToString(),
            }));
        });
        return Ok;
    }

    private async Task<int> ShowAsync()
    {
        var doc = await ResolveDocumentAsync(Need(0, "документ")).ConfigureAwait(false);
        var detail = await Api.GetDocumentAsync(doc.Id, _ct).ConfigureAwait(false);
        var journal = await Api.GetDocumentJournalAsync(doc.Id, _ct).ConfigureAwait(false);
        var number = _args.Int("version") ?? detail.Document.LatestVersion;
        var version = detail.Versions.FirstOrDefault(v => v.Number == number)
                      ?? throw new CliException($"У документа {Label(doc.RelPath, doc.Designation)} нет версии v{number} (последняя — v{detail.Document.LatestVersion}).");

        Emit(new { document = detail.Document, versions = detail.Versions, journal }, () =>
        {
            var d = detail.Document;
            Out.WriteLine($"{d.Designation ?? d.FileName}{(d.Name != null ? " — " + d.Name : "")}");
            Out.WriteLine($"  ID:          {d.Id}");
            Out.WriteLine($"  Проект:      {d.ProjectName}{(d.ProjectKind == ProjectKind.Library ? " (библиотека)" : "")}");
            Out.WriteLine($"  Файл:        {d.RelPath} ({KindText(d.Kind)}, {d.Cad})");
            Out.WriteLine($"  Последняя:   v{d.LatestVersion}, {d.LatestAuthorDisplayName}, {Time(d.LatestAt)}");
            Out.WriteLine($"  Статус:      {(d.Status == DocumentStatus.Unused ? "не используется" : "используется")}");
            Out.WriteLine($"  Блокировка:  {LockText(d.Lock)}");
            Out.WriteLine();
            Out.WriteLine($"Версия v{version.Number}: {version.RelPath}, {version.CadVersion ?? "версия CAD неизвестна"}, SHA-256 {version.Sha256}, {version.Size} Б");
            if (version.Configurations.Count > 0) Out.WriteLine("  Конфигурации: " + string.Join(", ", version.Configurations));
            foreach (var scope in version.Properties.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                foreach (var prop in scope.Value)
                    Out.WriteLine($"  {(scope.Key.Length == 0 ? "" : "[" + scope.Key + "] ")}{prop.Key} = {prop.Value}");
            }
            if (version.References.Count > 0)
            {
                Out.WriteLine("  Ссылки:");
                foreach (var r in version.References)
                {
                    Out.WriteLine(r.ChildDocumentId == null
                        ? $"    {r.RefPath} — отсутствует (не учтён в PDM)"
                        : $"    {r.RefPath} → {r.ChildRelPath} v{r.ChildVersion}" + (r.ChildLatestVersion is { } l && l != r.ChildVersion ? $" (последняя v{l})" : ""));
                }
            }
            if (version.Derived.Count > 0)
            {
                Out.WriteLine("  Производные файлы:");
                foreach (var f in version.Derived) Out.WriteLine($"    {f.FileName} ({f.Size} Б, {Short(f.Sha256)})");
            }
            Out.WriteLine();
            Out.WriteLine("Версии:");
            Table(new[] { "Версия", "Дата", "Автор", "Файл", "Комментарий" },
                detail.Versions.Select(v => new[] { "v" + v.Number, Time(v.At), v.AuthorDisplayName, v.RelPath, v.Comment }));
            if (journal.Count > 0)
            {
                Out.WriteLine();
                Out.WriteLine("Журнал:");
                PrintJournal(journal);
            }
        });
        return Ok;
    }

    private async Task<int> WhereUsedAsync()
    {
        var doc = await ResolveDocumentAsync(Need(0, "документ")).ConfigureAwait(false);
        var version = _args.Int("version") ?? doc.LatestVersion;
        var view = await Service.GetWhereUsedAsync(doc.Id, version, _ct).ConfigureAwait(false);
        Emit(view, () =>
        {
            Out.WriteLine($"Где применяется {Label(doc.RelPath, doc.Designation)} v{version} (последняя v{doc.LatestVersion}):");
            if (view.ThisVersion.Count == 0) Out.WriteLine("  нигде");
            PrintUses(view.ThisVersion);
            if (view.OtherVersions.Count > 0)
            {
                Out.WriteLine("Применения других версий:");
                PrintUses(view.OtherVersions);
            }
        });
        return Ok;
    }

    private void PrintUses(IEnumerable<WhereUsedItemDto> items)
    {
        foreach (var i in items)
        {
            var notes = new List<string>();
            if (!i.UsesLatestChild) notes.Add($"использует не последнюю версию (v{i.ChildVersion})");
            if (!i.IsParentLatest) notes.Add($"не последняя версия родителя, последняя v{i.ParentLatestVersion}");
            Out.WriteLine($"  {Label(i.ParentRelPath, i.ParentDesignation)} v{i.ParentVersion} → v{i.ChildVersion}" + (notes.Count > 0 ? " — " + string.Join("; ", notes) : ""));
        }
    }

    private async Task<int> UnusedAsync()
    {
        var doc = await ResolveDocumentAsync(Need(0, "документ")).ConfigureAwait(false);
        var unused = !_args.Flag("undo");
        var updated = await Service.MarkUnusedAsync(doc.Id, unused, _ct).ConfigureAwait(false);
        Emit(updated, () => Out.WriteLine(unused
            ? $"{Label(updated.RelPath, updated.Designation)} помечен «не используется»: пропадает из поиска по умолчанию, история сохранена."
            : $"{Label(updated.RelPath, updated.Designation)} снова используется."));
        return Ok;
    }

    private async Task<int> ExportAsync()
    {
        var doc = await ResolveDocumentAsync(Need(0, "документ")).ConfigureAwait(false);
        var zip = FullPath(Need(1, "путь к ZIP-файлу"));
        var version = _args.Int("version") ?? doc.LatestVersion;
        await Service.ExportAsync(doc.Id, version, _args.Flag("derived"), zip, Progress, _ct).ConfigureAwait(false);

        ExportManifest? manifest = null;
        using (var archive = ZipFile.OpenRead(zip))
        {
            var entry = archive.GetEntry(ExportManifest.FileName);
            if (entry != null)
            {
                await using var s = entry.Open();
                manifest = await JsonSerializer.DeserializeAsync<ExportManifest>(s, PdmJson.Options, _ct).ConfigureAwait(false);
            }
        }
        Emit(new { file = zip, size = new FileInfo(zip).Length, manifest }, () =>
            Out.WriteLine($"Комплект {Label(doc.RelPath, doc.Designation)} v{version} выгружен: {zip} ({new FileInfo(zip).Length} Б, файлов: {manifest?.Files.Count ?? 0})."));
        return Ok;
    }

    private async Task<int> TreeAsync()
    {
        var workRoot = FindWorkspace(BaseDir);
        Guid id;
        int version;
        if (_args.At(0) is { } text)
        {
            var doc = await ResolveDocumentAsync(text).ConfigureAwait(false);
            id = doc.Id;
            version = _args.Int("version") ?? doc.LatestVersion;
        }
        else
        {
            if (workRoot == null) throw new CliException("Укажите документ или выполните команду в рабочей папке.");
            var state = WorkspaceState.Load(workRoot);
            if (state.HeadDocumentId == null) throw new CliException("В рабочей папке ещё не открыт ни один комплект. Укажите документ.");
            id = state.HeadDocumentId.Value;
            version = _args.Int("version") ?? state.HeadVersion ?? (await Api.GetDocumentAsync(id, _ct).ConfigureAwait(false)).Document.LatestVersion;
        }
        var tree = await Service.GetKitTreeAsync(id, version, workRoot, _ct).ConfigureAwait(false);
        Emit(tree, () => PrintTree(tree, ""));
        return Ok;
    }

    private void PrintTree(KitTreeNode node, string indent)
    {
        var name = node.IsUnresolved ? node.RefPath : node.RelPath;
        var head = node.IsUnresolved ? name : $"{name} v{node.Version}" + (node.Designation != null ? $" «{node.Designation}»" : "");
        Out.WriteLine($"{indent}{head} — {node.StateText}{(node.IsCycle ? " (цикл)" : "")}");
        foreach (var child in node.Children) PrintTree(child, indent + "  ");
    }
}
