using SolidBrew.Core.Contracts;

namespace SolidBrew.Workspace;

/// <summary>Узел дерева комплекта для экрана «Комплект» (6.1 п. 3).</summary>
public sealed class KitTreeNode
{
    public Guid DocumentId { get; init; }
    public int Version { get; init; }
    public int LatestVersion { get; init; }
    public string RelPath { get; init; } = string.Empty;
    public string FileName => RelPath.Length == 0 ? RefPath : Texts.FileNameOf(RelPath);
    /// <summary>Путь ссылки родителя на этот узел (как записан в файле родителя).</summary>
    public string RefPath { get; init; } = string.Empty;
    public string? Designation { get; init; }
    public string? Name { get; init; }
    public DocumentKind Kind { get; init; }
    public string Cad { get; init; } = string.Empty;
    public string? CadVersion { get; init; }
    public DocumentStatus Status { get; init; }
    public LockState Lock { get; init; }
    public LockDto? LockInfo { get; init; }
    /// <summary>Локальное состояние; null — документа нет в рабочей папке (или папка не задана).</summary>
    public LocalFileState? Local { get; init; }
    /// <summary>Версия, лежащая в рабочей папке (может отличаться от версии в комплекте).</summary>
    public int? LocalVersion { get; init; }
    /// <summary>Ссылка на отсутствующий файл (не учтён в PDM).</summary>
    public bool IsUnresolved { get; init; }
    /// <summary>Документ входит в комплект в нескольких версиях.</summary>
    public bool HasVersionConflict { get; init; }
    /// <summary>Повтор узла, уже показанного выше по ветке (цикл ссылок) — дети не разворачиваются.</summary>
    public bool IsCycle { get; init; }
    public bool IsOutdated => !IsUnresolved && Version < LatestVersion;
    public List<KitTreeNode> Children { get; } = new();

    /// <summary>Состояние по-русски: свободен / взят мной / взят другим (кем), изменён локально, устарел, конфликт версий.</summary>
    public string StateText
    {
        get
        {
            if (IsUnresolved) return "отсутствует (не учтён в PDM)";
            var parts = new List<string>
            {
                Lock switch
                {
                    LockState.LockedByMe => "взят мной",
                    LockState.LockedByOther => $"взят: {LockInfo?.UserDisplayName}",
                    LockState.Free => "свободен",
                    _ => string.Empty,
                },
            };
            if (Local == LocalFileState.ModifiedLocally) parts.Add("изменён локально");
            if (Local == LocalFileState.MissingLocally) parts.Add("нет файла в рабочей папке");
            if (LocalVersion != null && LocalVersion != Version) parts.Add($"в папке v{LocalVersion}");
            if (IsOutdated) parts.Add($"есть v{LatestVersion}");
            if (HasVersionConflict) parts.Add("в комплекте в нескольких версиях");
            if (Status == DocumentStatus.Unused) parts.Add("не используется");
            return string.Join(", ", parts.Where(p => p.Length > 0));
        }
    }
}

/// <summary>Построение дерева комплекта из <see cref="KitDto"/> и состояния рабочей папки.</summary>
public static class KitTreeBuilder
{
    /// <param name="kit">Комплект с сервера.</param>
    /// <param name="status">Состояние рабочей папки (необязательно) — для «изменён локально» и версии в папке.</param>
    /// <param name="myUserId">Текущий пользователь — чтобы отличить «взят мной» от «взят другим».</param>
    public static KitTreeNode Build(KitDto kit, WorkspaceStatus? status, Guid? myUserId)
    {
        if (kit == null) throw new ArgumentNullException(nameof(kit));
        var items = kit.Items.ToDictionary(i => (i.DocumentId, i.Version));
        var conflicted = new HashSet<Guid>(kit.Items.GroupBy(i => i.DocumentId).Where(g => g.Select(x => x.Version).Distinct().Count() > 1).Select(g => g.Key));
        var children = kit.Edges.GroupBy(e => (e.ParentDocumentId, e.ParentVersion)).ToDictionary(g => g.Key, g => g.ToList());
        var unresolved = kit.Unresolved.GroupBy(u => (u.ParentDocumentId, u.ParentVersion)).ToDictionary(g => g.Key, g => g.ToList());

        KitTreeNode Make(KitItemDto it, string refPath, HashSet<(Guid, int)> path)
        {
            var fs = status?.FindByDocument(it.DocumentId);
            var lockState = it.Lock == null ? LockState.Free
                : myUserId != null && it.Lock.UserId == myUserId ? LockState.LockedByMe : LockState.LockedByOther;
            var cycle = path.Contains((it.DocumentId, it.Version));
            var node = new KitTreeNode
            {
                DocumentId = it.DocumentId,
                Version = it.Version,
                LatestVersion = it.LatestVersion,
                RelPath = it.RelPath,
                RefPath = refPath,
                Designation = it.Designation,
                Name = it.Name,
                Kind = it.Kind,
                Cad = it.Cad,
                CadVersion = it.CadVersion,
                Status = it.Status,
                Lock = lockState,
                LockInfo = it.Lock,
                Local = fs?.Local,
                LocalVersion = fs?.LocalVersion,
                HasVersionConflict = conflicted.Contains(it.DocumentId),
                IsCycle = cycle,
            };
            if (cycle) return node;

            path.Add((it.DocumentId, it.Version));
            if (children.TryGetValue((it.DocumentId, it.Version), out var edges))
            {
                foreach (var e in edges)
                {
                    if (items.TryGetValue((e.ChildDocumentId, e.ChildVersion), out var child))
                        node.Children.Add(Make(child, e.RefPath, path));
                }
            }
            if (unresolved.TryGetValue((it.DocumentId, it.Version), out var missing))
            {
                foreach (var u in missing)
                    node.Children.Add(new KitTreeNode { RefPath = u.RefPath, IsUnresolved = true });
            }
            path.Remove((it.DocumentId, it.Version));
            node.Children.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
            return node;
        }

        if (!items.TryGetValue((kit.HeadDocumentId, kit.HeadVersion), out var head))
            throw new WorkspaceException("В комплекте нет головного документа.");
        return Make(head, string.Empty, new HashSet<(Guid, int)>());
    }
}
