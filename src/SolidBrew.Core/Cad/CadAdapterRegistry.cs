using System;
using System.Collections.Generic;

namespace SolidBrew.Core.Cad
{
    /// <summary>Набор адаптеров CAD: поиск по расширению файла и по идентификатору CAD.</summary>
    public sealed class CadAdapterRegistry
    {
        private readonly List<ICadAdapter> _all = new List<ICadAdapter>();
        private readonly Dictionary<string, ICadAdapter> _byId = new Dictionary<string, ICadAdapter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ICadAdapter> _byExtension = new Dictionary<string, ICadAdapter>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ICadAdapter> All => _all;

        /// <summary>Зарегистрировать адаптер. Повтор идентификатора или расширения — ошибка.</summary>
        public void Register(ICadAdapter adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            if (string.IsNullOrWhiteSpace(adapter.CadId))
                throw new ArgumentException("У адаптера CAD не задан идентификатор.", nameof(adapter));
            if (_byId.ContainsKey(adapter.CadId))
                throw new ArgumentException($"Адаптер CAD «{adapter.CadId}» уже зарегистрирован.", nameof(adapter));

            var extensions = new List<string>();
            foreach (var ext in adapter.Extensions)
            {
                var normalized = NormalizeExtension(ext);
                if (_byExtension.TryGetValue(normalized, out var other))
                    throw new ArgumentException($"Расширение «{normalized}» уже обслуживает адаптер «{other.CadId}».", nameof(adapter));
                extensions.Add(normalized);
            }

            _all.Add(adapter);
            _byId.Add(adapter.CadId, adapter);
            foreach (var ext in extensions)
                _byExtension[ext] = adapter;
        }

        /// <summary>Адаптер для файла по его расширению (без учёта регистра) или null.</summary>
        public ICadAdapter? FindFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var name = CadReference.FileNameOf(path);
            var dot = name.LastIndexOf('.');
            if (dot < 0) return null;
            return _byExtension.TryGetValue(name.Substring(dot), out var adapter) ? adapter : null;
        }

        /// <summary>Адаптер по идентификатору CAD (без учёта регистра) или null.</summary>
        public ICadAdapter? Get(string cadId)
        {
            if (string.IsNullOrEmpty(cadId)) return null;
            return _byId.TryGetValue(cadId, out var adapter) ? adapter : null;
        }

        private static string NormalizeExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
                throw new ArgumentException("Пустое расширение файла в адаптере CAD.");
            ext = ext.Trim();
            return ext[0] == '.' ? ext : "." + ext;
        }
    }
}
