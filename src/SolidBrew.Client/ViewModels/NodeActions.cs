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
    /// <summary>Файл, оставленный ссылкой: поставить на учёт и привязать ссылку родителя к новому документу.</summary>
    RegisterLinked,
    /// <summary>Ссылка на файл, не учтённый в PDM: указать файл модели, поставить его на учёт и привязать к нему ссылку.</summary>
    RegisterLinkedPick,
    /// <summary>Выбрать сборку SOLIDWORKS и поставить её на учёт новым изделием.</summary>
    AddProject,
    /// <summary>Развернуть узел со всеми вложенными.</summary>
    ExpandBranch,
    /// <summary>Свернуть узел со всеми вложенными.</summary>
    CollapseBranch,
    /// <summary>Удалить документ из PDM (только администратор).</summary>
    Delete,
    /// <summary>Убрать из родителя ссылку на файл, не учтённый в PDM (только администратор).</summary>
    RemoveReference,
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

    /// <summary>Действия, которые выполняются сразу над всеми выделенными узлами.</summary>
    public static readonly IReadOnlyList<NodeActionKind> Batch = new[]
    {
        NodeActionKind.RegisterLinked, NodeActionKind.CheckOut, NodeActionKind.Checkin,
        NodeActionKind.UndoCheckOut, NodeActionKind.UpdateToLatest,
    };

    /// <summary>Применимо ли действие к узлу (есть в его кнопках или меню).</summary>
    public static bool Applies(ProductNodeViewModel node, NodeActionKind kind, bool canModify, bool isProductRoot, bool isAdmin = false)
    {
        var (primary, more) = For(node, canModify, isProductRoot, isAdmin);
        return primary.Contains(kind) || more.Contains(kind);
    }

    /// <param name="node">Узел.</param>
    /// <param name="canModify">Роль позволяет менять хранилище (не читатель).</param>
    /// <param name="isProductRoot">Узел — само изделие окна.</param>
    /// <param name="isAdmin">Пользователь — администратор (удаление документов).</param>
    public static (List<NodeActionKind> Primary, List<NodeActionKind> More) For(ProductNodeViewModel? node, bool canModify, bool isProductRoot, bool isAdmin = false)
    {
        var primary = new List<NodeActionKind>();
        var more = new List<NodeActionKind>();
        if (node == null || node.Type is NodeType.Group)
        {
            // Правый щелчок по пустому месту дерева или по «Новые файлы».
            if (canModify) more.Add(NodeActionKind.AddProject);
            return (primary, more);
        }

        if (node.IsProductStub)
        {
            // Неоткрытое изделие верхнего уровня.
            primary.Add(NodeActionKind.OpenAsProduct);
            more.Add(NodeActionKind.OpenAsProductInNewWindow);
            if (isAdmin) more.Add(NodeActionKind.Delete);
            if (canModify) more.Add(NodeActionKind.AddProject);
            return (primary, more);
        }

        if (node.Type == NodeType.Unresolved)
        {
            // Файл не в PDM: открыть по ссылке; поставить на учёт — дальше он обычный документ (взять в работу, сдать…).
            if (node.LinkPath != null) primary.Add(NodeActionKind.OpenInCad);
            if (canModify && node.ParentDocument != null)
            {
                // Файл по ссылке есть — ставится он; нет (или нужен другой) — файл указывает пользователь.
                if (node.LinkPath != null)
                {
                    primary.Add(NodeActionKind.RegisterLinked);
                    more.Add(NodeActionKind.RegisterLinkedPick);
                }
                else
                {
                    primary.Add(NodeActionKind.RegisterLinkedPick);
                }
            }
            if (node.LinkPath != null) more.Add(NodeActionKind.ShowInExplorer);
            // Убрать ссылку из файла родителя — только администратор; у чертежа ссылки — это виды (их убирают в CAD).
            if (isAdmin && node.ParentDocument is { Type: not NodeType.Drawing, IsLockedByOther: false }) more.Add(NodeActionKind.RemoveReference);
            return (primary, more);
        }

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
        // Удалить — только администратор; изделие окна тоже (после удаления окно его закрывает).
        if (isAdmin && !node.IsLockedByOther) more.Add(NodeActionKind.Delete);
        if (node.HasLocalFile) more.Add(NodeActionKind.ShowInExplorer);
        if (node.Type == NodeType.Assembly && !isProductRoot)
        {
            more.Add(NodeActionKind.OpenAsProduct);
            more.Add(NodeActionKind.OpenAsProductInNewWindow);
        }
        if (node.Children.Count > 0)
        {
            more.Add(NodeActionKind.ExpandBranch);
            more.Add(NodeActionKind.CollapseBranch);
        }
        if (canModify && isProductRoot) more.Add(NodeActionKind.AddProject);

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
        NodeActionKind.RegisterLinked => "Сдать в хранилище…",
        NodeActionKind.RegisterLinkedPick => "Выбрать файл и сдать…",
        NodeActionKind.AddProject => "Добавить проект…",
        NodeActionKind.ExpandBranch => "Развернуть ветку",
        NodeActionKind.CollapseBranch => "Свернуть ветку",
        NodeActionKind.Delete => "Удалить…",
        NodeActionKind.RemoveReference => "Удалить ссылку…",
        _ => kind.ToString(),
    };

    public static string Glyph(NodeActionKind kind) => kind is NodeActionKind.Delete or NodeActionKind.RemoveReference ? "" : kind switch
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
        NodeActionKind.RegisterLinkedPick => "Указать файл этой модели на диске, загрузить его в хранилище и привязать к нему ссылку сборки",
        NodeActionKind.AddProject => "Выбрать сборку SOLIDWORKS и поставить её на учёт новым изделием",
        NodeActionKind.ExpandBranch => "Развернуть этот узел со всеми вложенными сборками",
        NodeActionKind.CollapseBranch => "Свернуть этот узел со всеми вложенными сборками",
        NodeActionKind.RegisterLinked => "Сдать файл по ссылке в хранилище (поставить на учёт) и привязать к нему ссылку сборки; дальше его можно брать в работу и сдавать",
        NodeActionKind.Delete => "Только администратор: убрать документ из PDM (если на него не ссылаются опубликованные версии других документов). История версий сохраняется",
        NodeActionKind.RemoveReference => "Только администратор: убрать эту ссылку из файла родителя (у сборки — удалить компонент, у детали — разорвать внешние ссылки) и сдать новую версию родителя",
        _ => null,
    };
}
