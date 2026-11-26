using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SolidBrew.Core.Cad
{
    /// <summary>
    /// Необязательная часть адаптера CAD: чтение в два приёма. <see cref="ReadStructureAsync"/> — ссылки, конфигурации и версия CAD
    /// без открытия документа (в SOLIDWORKS — десятки миллисекунд на файл); <see cref="ReadPropertiesAsync"/> — свойства,
    /// для которых документ открывается (в SOLIDWORKS — от 0,1 до нескольких секунд). Адаптер без этого интерфейса читается
    /// целиком <see cref="ICadAdapter.ReadAsync"/> — см. <see cref="CadReading"/>.
    /// </summary>
    public interface ICadFastReader
    {
        /// <summary>
        /// Ссылки, конфигурации и версия CAD — без свойств (<see cref="CadDocumentInfo.Properties"/> пусто) и без открытия документа.
        /// Ошибки — <see cref="CadException"/>.
        /// </summary>
        Task<CadDocumentInfo> ReadStructureAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Свойства документа: ключ — конфигурация, «» — уровень файла. <paramref name="configurations"/> — конфигурации из
        /// <see cref="ReadStructureAsync"/>. Документ, открытый адаптером, закрывается; документы, открытые пользователем под
        /// другим путём, не трогаются. Ошибки — <see cref="CadException"/>, если мешает одноимённый открытый документ —
        /// <see cref="CadSameNameOpenException"/>.
        /// </summary>
        Task<Dictionary<string, Dictionary<string, string>>> ReadPropertiesAsync(string path, IReadOnlyList<string> configurations,
            CancellationToken ct = default);
    }

    /// <summary>Чтение документа CAD в два приёма с запасным путём для адаптеров без <see cref="ICadFastReader"/>.</summary>
    public static class CadReading
    {
        /// <summary>Адаптер умеет читать ссылки без свойств (и без открытия документа).</summary>
        public static bool HasFastRead(this ICadAdapter adapter) => adapter is ICadFastReader;

        /// <summary>
        /// Ссылки, конфигурации и версия CAD. Адаптер без быстрого чтения читает документ целиком — тогда свойства тоже заполнены.
        /// </summary>
        public static Task<CadDocumentInfo> ReadStructureAsync(this ICadAdapter adapter, string path, CancellationToken ct = default) =>
            adapter is ICadFastReader fast ? fast.ReadStructureAsync(path, ct) : adapter.ReadAsync(path, ct);

        /// <summary>Свойства документа (адаптер без быстрого чтения читает документ целиком).</summary>
        public static async Task<Dictionary<string, Dictionary<string, string>>> ReadPropertiesAsync(this ICadAdapter adapter, string path,
            IReadOnlyList<string> configurations, CancellationToken ct = default)
        {
            if (adapter is ICadFastReader fast) return await fast.ReadPropertiesAsync(path, configurations, ct).ConfigureAwait(false);
            return (await adapter.ReadAsync(path, ct).ConfigureAwait(false)).Properties;
        }
    }
}
