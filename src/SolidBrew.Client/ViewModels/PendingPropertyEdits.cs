using SolidBrew.Core.Cad;
using SolidBrew.Workspace;

namespace SolidBrew.Client.ViewModels;

/// <summary>
/// Значения свойств, заполненные в карточках и ещё не записанные в файлы, — по документам окна изделия.
/// Не теряются при переходе к другому узлу; «Сдать» записывает их все одной операцией.
/// </summary>
public sealed class PendingPropertyEdits
{
    private readonly Dictionary<Guid, (string Label, List<CadPropertyChange> Changes)> _docs = new();

    public event EventHandler? Changed;

    /// <summary>Сколько документов с заполненными значениями.</summary>
    public int Count => _docs.Count;

    public IReadOnlyCollection<Guid> Documents => _docs.Keys;

    public bool Has(Guid documentId) => _docs.ContainsKey(documentId);

    /// <summary>Заполненное для документа (пусто — ничего).</summary>
    public IReadOnlyList<CadPropertyChange> For(Guid documentId) =>
        _docs.TryGetValue(documentId, out var d) ? d.Changes : Array.Empty<CadPropertyChange>();

    /// <summary>Заменить заполненное для документа (пустой список — убрать документ).</summary>
    public void Set(Guid documentId, string label, IReadOnlyList<CadPropertyChange> changes)
    {
        if (changes.Count == 0)
        {
            if (!_docs.Remove(documentId)) return;
        }
        else
        {
            _docs[documentId] = (label, changes.ToList());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(IEnumerable<Guid> documentIds)
    {
        var any = false;
        foreach (var id in documentIds) any |= _docs.Remove(id);
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_docs.Count == 0) return;
        _docs.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public List<PropertyWrite> ToWrites() => _docs.Select(d => new PropertyWrite(d.Key, d.Value.Changes.ToList())).ToList();

    /// <summary>Перечень для подтверждения: «документ: свойство = значение; …» (одно значение, записанное в несколько конфигураций, — один раз).</summary>
    public List<string> Describe() => _docs.Values
        .OrderBy(d => d.Label, StringComparer.CurrentCultureIgnoreCase)
        .Select(d => d.Label + ": " + string.Join("; ", d.Changes.GroupBy(c => (c.Name, c.Value)).Select(g => $"{g.Key.Name} = {g.Key.Value}")))
        .ToList();
}
