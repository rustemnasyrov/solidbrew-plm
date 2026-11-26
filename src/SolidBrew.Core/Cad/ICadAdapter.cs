using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SolidBrew.Core.Contracts;

namespace SolidBrew.Core.Cad
{
    /// <summary>Адаптер CAD: чтение свойств и ссылок, переписывание ссылок, проверка открытия.</summary>
    public interface ICadAdapter
    {
        /// <summary>Идентификатор CAD: «solidworks», «testcad».</summary>
        string CadId { get; }

        /// <summary>Название для пользователя.</summary>
        string DisplayName { get; }

        /// <summary>Расширения файлов в нижнем регистре, с точкой («.sldprt»).</summary>
        IReadOnlyCollection<string> Extensions { get; }

        /// <summary>Файл является документом этого CAD.</summary>
        bool IsDocument(string path);

        /// <summary>Вид документа по пути (расширению).</summary>
        DocumentKind KindOf(string path);

        /// <summary>Прочитать свойства, конфигурации и ссылки документа. Ошибки — <see cref="CadException"/>.</summary>
        Task<CadDocumentInfo> ReadAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Прочитать только ссылки документа — без свойств и конфигураций. Для SOLIDWORKS это не открывает документ
        /// и в десятки раз быстрее <see cref="ReadAsync"/>. Ошибки — <see cref="CadException"/>.
        /// </summary>
        Task<List<CadReference>> ReadReferencesAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Переписать ссылки внутри файла: ключ — старый путь ссылки (как в <see cref="CadReference.SavedPath"/>),
        /// значение — новый полный путь.
        /// </summary>
        Task ReplaceReferencesAsync(string documentPath, IReadOnlyDictionary<string, string> oldToNew, CancellationToken ct = default);

        /// <summary>
        /// Записать свойства в файл и сохранить его (свойство создаётся, если его нет; значение — текст).
        /// Файл должен быть доступен для записи. Ошибки — <see cref="CadException"/>; при ошибке файл не меняется.
        /// </summary>
        Task WritePropertiesAsync(string documentPath, IReadOnlyList<CadPropertyChange> changes, CancellationToken ct = default);

        /// <summary>
        /// Убрать из документа ссылки (пути — как в <see cref="CadReference.SavedPath"/>) и сохранить его. Сборка — удаляются
        /// компоненты; деталь — внешние ссылки разрываются (все сразу, так умеет CAD), а имена файлов записываются в свойство
        /// <see cref="DocumentProperties.RemovedReferencesKey"/>, по которому PDM их больше не учитывает. Чертёж — не поддерживается.
        /// Файл должен быть доступен для записи. Ошибки — <see cref="CadException"/>.
        /// </summary>
        Task RemoveReferencesAsync(string documentPath, IReadOnlyList<string> savedPaths, CancellationToken ct = default);

        /// <summary>Открыть в CAD и проверить, откуда реально загружены компоненты в каждой конфигурации.</summary>
        Task<OpenReport> OpenAndVerifyAsync(string headPath, string workRoot, CancellationToken ct = default);
    }
}
