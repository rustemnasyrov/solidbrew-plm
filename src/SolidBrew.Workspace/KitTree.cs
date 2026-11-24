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
    public Guid ProjectId { get; init; }
    /// <summary>Узел — чертёж (по виду документа).</summary>
    public bool IsDrawing { get; init; }
    /// <summary>
    /// Чертёж, добавленный к комплекту (<see cref="KitItemDto.IsAttachedDrawing"/>): показывается дочерним узлом документа,
    /// который он изображает (<see cref="DrawsDocumentId"/>), хотя в CAD ссылка идёт от чертежа к модели.
    /// </summary>
    public bool IsAttachedDrawing { get; init; }
    /// <summary>Для чертежа: документ комплекта, под которым он показан (первая из его моделей в порядке комплекта).</summary>
    public Guid? DrawsDocumentId { get; init; }
    /// <summary>Для чертежа: все документы комплекта, на которые он ссылается (первый — <see cref="DrawsDocumentId"/>).</summary>
    public List<Guid> DrawsDocumentIds { get; init; } = new();
    /// <summary>Чертёж сдан с другой версией модели, чем в комплекте.</summary>
    public bool DrawingModelVersionMismatch { get; init; }
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
            if (IsAttachedDrawing && IsDrawing) parts.Insert(0, "чертёж");
            if (DrawingModelVersionMismatch) parts.Add("сдан с другой версией модели");
            return string.Join(", ", parts.Where(p => p.Length > 0));
        }
    }
}

/// <summary>
/// Построение дерева комплекта из <see cref="KitDto"/> и состояния рабочей папки.
/// Чертежи, добавленные к комплекту (<see cref="KitItemDto.IsAttachedDrawing"/>), показываются дочерними узлами документа,
/// который они изображают (после обычных детей); их собственные дети — только добавленные вместе с ними зависимости
/// (модели комплекта под чертежом не повторяются).
/// </summary>
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

        // Чертежи под моделями: модель — первый документ основного комплекта (в порядке комплекта), на который ссылается чертёж.
        var baseOrder = new Dictionary<Guid, int>();
        foreach (var it in kit.Items.Where(i => !i.IsAttachedDrawing))
        {
            if (!baseOrder.ContainsKey(it.DocumentId)) baseOrder[it.DocumentId] = baseOrder.Count;
        }
        var drawingsOf = new Dictionary<Guid, List<(KitItemDto Drawing, List<Guid> Models)>>();
        foreach (var d in kit.Items.Where(i => i.IsAttachedDrawing && i.Kind == DocumentKind.Drawing))
        {
            var models = kit.Edges.Where(e => e.ParentDocumentId == d.DocumentId && e.ParentVersion == d.Version && baseOrder.ContainsKey(e.ChildDocumentId))
                .Select(e => e.ChildDocumentId).Distinct().OrderBy(m => baseOrder[m]).ToList();
            if (models.Count == 0) continue;
            if (!drawingsOf.TryGetValue(models[0], out var list)) drawingsOf[models[0]] = list = new();
            list.Add((d, models));
        }

        KitTreeNode Make(KitItemDto it, string refPath, HashSet<(Guid, int)> path, List<Guid>? draws = null)
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
                ProjectId = it.ProjectId,
                IsDrawing = it.Kind == DocumentKind.Drawing,
                IsAttachedDrawing = it.IsAttachedDrawing && it.Kind == DocumentKind.Drawing,
                DrawsDocumentId = draws?.FirstOrDefault(),
                DrawsDocumentIds = draws ?? new List<Guid>(),
                DrawingModelVersionMismatch = it.DrawingModelVersionMismatch,
            };
            if (cycle) return node;

            path.Add((it.DocumentId, it.Version));
            var attachedDrawing = it.IsAttachedDrawing && it.Kind == DocumentKind.Drawing;
            if (children.TryGetValue((it.DocumentId, it.Version), out var edges))
            {
                foreach (var e in edges)
                {
                    if (attachedDrawing && baseOrder.ContainsKey(e.ChildDocumentId)) continue; // модель, под которой показан чертёж
                    if (items.TryGetValue((e.ChildDocumentId, e.ChildVersion), out var child))
                        node.Children.Add(Make(child, e.RefPath, path));
                }
            }
            if (unresolved.TryGetValue((it.DocumentId, it.Version), out var missing))
            {
                foreach (var u in missing)
                    node.Children.Add(new KitTreeNode { RefPath = u.RefPath, IsUnresolved = true });
            }
            node.Children.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
            if (!it.IsAttachedDrawing && drawingsOf.TryGetValue(it.DocumentId, out var drawings))
            {
                foreach (var (d, models) in drawings.OrderBy(x => Texts.FileNameOf(x.Drawing.RelPath), StringComparer.OrdinalIgnoreCase))
                    node.Children.Add(Make(d, string.Empty, path, models));
            }
            path.Remove((it.DocumentId, it.Version));
            return node;
        }

        if (!items.TryGetValue((kit.HeadDocumentId, kit.HeadVersion), out var head))
            throw new WorkspaceException("В комплекте нет головного документа.");
        return Make(head, string.Empty, new HashSet<(Guid, int)>());
    }
}
