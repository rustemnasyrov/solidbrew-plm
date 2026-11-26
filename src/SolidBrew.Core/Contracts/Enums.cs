namespace SolidBrew.Core.Contracts
{
    /// <summary>Роль пользователя.</summary>
    public enum UserRole
    {
        Admin,
        Designer,
        Reader,
    }

    /// <summary>Вид проекта: обычный проект или библиотека компонентов.</summary>
    public enum ProjectKind
    {
        Project,
        Library,
    }

    /// <summary>Вид документа CAD.</summary>
    public enum DocumentKind
    {
        Part,
        Assembly,
        Drawing,
        Other,
    }

    /// <summary>Статус документа: используется, помечен «не используется» или удалён администратором.</summary>
    public enum DocumentStatus
    {
        Active,
        Unused,
        /// <summary>Удалён администратором: нет в поиске и списках, история версий сохранена.</summary>
        Deleted,
    }
}
