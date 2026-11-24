using System.Windows.Input;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>Действие над узлом дерева изделия.</summary>
public enum NodeActionKind
{
    OpenInCad,
    CheckOut,
    Checkin,
    UndoCheckOut,
    UpdateToLatest,
    Rename,
    WhereUsed,
    MarkUnused,
    ReturnToWork,
    ShowInExplorer,
    OpenAsProduct,
    OpenAsProductInNewWindow,
}

/// <summary>Кнопка или пункт меню действия.</summary>
public sealed record NodeAction(NodeActionKind Kind, string Text, string Glyph, ICommand Command, string? ToolTip = null)
{
    public override string ToString() => Text;
}

/// <summary>Какие действия применимы к узлу при его состоянии и роли пользователя.</summary>
public static class NodeActionRules
{
    /// <summary>Кнопок в панели — не больше; остальное в «⋯».</summary>
    public const int MaxPrimary = 4;

    /// <param name="node">Узел.</param>
    /// <param name="canModify">Роль позволяет менять хранилище (не читатель).</param>
    /// <param name="isProductRoot">Узел — само изделие окна.</param>
    public static (List<NodeActionKind> Primary, List<NodeActionKind> More) For(ProductNodeViewModel? node, bool canModify, bool isProductRoot)
    {
        var primary = new List<NodeActionKind>();
        var more = new List<NodeActionKind>();
        if (node == null || node.Type is NodeType.Group or NodeType.Unresolved) return (primary, more);

        if (node.IsDraft)
        {
            if (node.HasLocalFile) primary.Add(NodeActionKind.OpenInCad);
            if (canModify) primary.Add(NodeActionKind.Checkin);
            if (node.HasLocalFile) more.Add(NodeActionKind.ShowInExplorer);
            return (primary, more);
        }

        var inFolder = node.File != null;
        if (node.HasLocalFile) primary.Add(NodeActionKind.OpenInCad);
        if (canModify && inFolder)
        {
            switch (node.Lock)
            {
                case LockState.LockedByMe:
                    // Взят мной в другой рабочей папке: здесь файл ещё только для чтения.
                    if (node.File?.IsReadOnly == true && !node.IsModified) primary.Add(NodeActionKind.CheckOut);
                    if (node.IsModified) primary.Add(NodeActionKind.Checkin);
                    primary.Add(NodeActionKind.UndoCheckOut);
                    break;
                case LockState.LockedByOther:
                    break;
                default:
                    primary.Add(NodeActionKind.CheckOut);
                    break;
            }
        }
        if (node.IsOutdated && inFolder && !(node.IsMine && node.IsModified)) primary.Add(NodeActionKind.UpdateToLatest);

        if (canModify && inFolder && !node.IsLockedByOther) more.Add(NodeActionKind.Rename);
        more.Add(NodeActionKind.WhereUsed);
        if (canModify) more.Add(node.IsUnused ? NodeActionKind.ReturnToWork : NodeActionKind.MarkUnused);
        if (node.HasLocalFile) more.Add(NodeActionKind.ShowInExplorer);
        if (node.Type == NodeType.Assembly && !isProductRoot)
        {
            more.Add(NodeActionKind.OpenAsProduct);
            more.Add(NodeActionKind.OpenAsProductInNewWindow);
        }

        // Не больше MaxPrimary кнопок: лишнее уходит в «⋯».
        while (primary.Count > MaxPrimary)
        {
            more.Insert(0, primary[^1]);
            primary.RemoveAt(primary.Count - 1);
        }
        return (primary, more);
    }

    public static string Text(NodeActionKind kind) => kind switch
    {
        NodeActionKind.OpenInCad => "Открыть в CAD",
        NodeActionKind.CheckOut => "Взять в работу",
        NodeActionKind.Checkin => "Сдать",
        NodeActionKind.UndoCheckOut => "Отменить взятие",
        NodeActionKind.UpdateToLatest => "Обновить до последней",
        NodeActionKind.Rename => "Переименовать…",
        NodeActionKind.WhereUsed => "Где применяется",
        NodeActionKind.MarkUnused => "Пометить «не используется»",
        NodeActionKind.ReturnToWork => "Вернуть в работу",
        NodeActionKind.ShowInExplorer => "Показать в Проводнике",
        NodeActionKind.OpenAsProduct => "Открыть как изделие",
        NodeActionKind.OpenAsProductInNewWindow => "Открыть как изделие в новом окне",
        _ => kind.ToString(),
    };

    public static string Glyph(NodeActionKind kind) => kind switch
    {
        NodeActionKind.OpenInCad => "",
        NodeActionKind.CheckOut => "",
        NodeActionKind.Checkin => "",
        NodeActionKind.UndoCheckOut => "",
        NodeActionKind.UpdateToLatest => "",
        NodeActionKind.Rename => "",
        NodeActionKind.WhereUsed => "",
        NodeActionKind.MarkUnused => "",
        NodeActionKind.ReturnToWork => "",
        NodeActionKind.ShowInExplorer => "",
        NodeActionKind.OpenAsProduct => "",
        NodeActionKind.OpenAsProductInNewWindow => "",
        _ => "",
    };

    public static string? ToolTip(NodeActionKind kind) => kind switch
    {
        NodeActionKind.OpenInCad => "Открыть файл рабочей папки в его программе",
        NodeActionKind.CheckOut => "Получить блокировку на изменение; файл станет доступен для записи",
        NodeActionKind.Checkin => "Опубликовать новые версии изменённых и новых документов рабочей папки",
        NodeActionKind.UndoCheckOut => "Снять свою блокировку без публикации",
        NodeActionKind.UpdateToLatest => "Положить в рабочую папку последнюю версию (с её комплектом)",
        NodeActionKind.OpenAsProduct => "Сделать эту сборку корнем окна (своя рабочая папка)",
        _ => null,
    };
}
