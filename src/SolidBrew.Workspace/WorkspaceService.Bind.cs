using SolidBrew.Core.Api;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Paths;

namespace SolidBrew.Workspace;

/// <summary>Привязка одной ссылки родителя (путь, как сохранён в родителе) к документу PDM.</summary>
public sealed record ReferenceBinding(string RefPath, Guid ChildId, int ChildVersion);

/// <summary>Итог привязки ссылки «по ссылке» к документу PDM.</summary>
public sealed class BindReferenceResult : OperationResult
{
    public Guid ParentDocumentId { get; set; }
    public int ParentVersion { get; set; }
    /// <summary>
    /// Файл родителя в рабочей папке изменён локально: он не заменяется новой версией (байты новой версии те же, что у
    /// взятой за основу, правки сохранены), в папку кладётся только привязанный документ.
    /// </summary>
    public bool ParentModifiedLocally { get; set; }
}

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Привязать ссылку родителя на файл, не учтённый в PDM (оставленный «по ссылке»), к документу PDM <paramref name="childId"/>
    /// v<paramref name="childVersion"/>: новая версия родителя с теми же байтами и этой привязкой (7.6: новая привязка — новая
    /// версия, даже если файл не изменился). Родитель берётся в работу, если ещё не взят мной; после сдачи блокировка снимается,
    /// если её брала эта операция. Рабочая папка не меняется, кроме состояния родителя с локальными правками
    /// (<see cref="BindReferenceResult.ParentModifiedLocally"/>) — там версия обновляется сразу, чтобы дальнейшая сдача шла от неё.
    /// Положить файлы в папку и переписать ссылки — «Обновить до последней» (родителя или, при локальных правках, привязанного документа).
    /// </summary>
    public Task<BindReferenceResult> BindReferenceAsync(string workRoot, Guid parentId, string refPath, Guid childId, int childVersion,
        CancellationToken ct = default) =>
        BindReferencesAsync(workRoot, parentId, new[] { new ReferenceBinding(refPath, childId, childVersion) }, ct);

    /// <summary>
    /// То же для нескольких ссылок одного родителя сразу — одна новая версия родителя со всеми привязками
    /// (по одной привязке за раз вторая упёрлась бы в «в рабочей папке не последняя версия»).
    /// </summary>
    public async Task<BindReferenceResult> BindReferencesAsync(string workRoot, Guid parentId, IReadOnlyList<ReferenceBinding> bindings,
        CancellationToken ct = default)
    {
        if (bindings.Count == 0) throw new ArgumentException("Нет ссылок для привязки.", nameof(bindings));
        var refPath = bindings[0].RefPath;
        var root = Root(workRoot);
        var state = WorkspaceState.Load(root);
        var result = new BindReferenceResult { ParentDocumentId = parentId };
        var detail = await Api.GetDocumentAsync(parentId, ct).ConfigureAwait(false);
        var label = Texts.Doc(detail.Document.RelPath, detail.Document.Designation);
        var entry = state.FindByDocument(parentId);
        var baseVersion = entry?.Version ?? detail.Document.LatestVersion;
        if (baseVersion != detail.Document.LatestVersion)
        {
            result.Message = $"В рабочей папке {label} v{baseVersion}, а в PDM уже v{detail.Document.LatestVersion}. Обновите его до последней версии и повторите.";
            return result;
        }
        var version = detail.Versions.FirstOrDefault(v => v.Number == baseVersion)
                      ?? throw new WorkspaceException($"Сервер не вернул версию v{baseVersion} документа {label}.");
        // Ссылка версии → привязка, которая к ней относится.
        var bound = new Dictionary<VersionRefDto, ReferenceBinding>();
        foreach (var b in bindings)
        {
            var found = version.References.Where(r => r.ChildDocumentId == null && SameSavedPath(r.RefPath, b.RefPath)).ToList();
            if (found.Count == 0)
            {
                result.Message = $"В {label} v{baseVersion} нет ссылки на файл, не учтённый в PDM: «{b.RefPath}». Обновите дерево изделия.";
                return result;
            }
            foreach (var r in found) bound[r] = b;
        }

        var me = await GetMeAsync(ct).ConfigureAwait(false);
        var lockedByMe = detail.Document.Lock?.UserId == me.Id;
        if (detail.Document.Lock != null && !lockedByMe)
        {
            var l = detail.Document.Lock;
            result.Message = $"{label} взят в работу: {l.UserDisplayName}, с {Texts.Time(l.At)}. Привязать ссылку можно, когда его сдадут или отменят взятие.";
            return result;
        }

        // Правки в файле родителя — не публикуются этой операцией: сдаются те же байты, что у v{baseVersion}.
        var full = entry != null ? RelPath.Combine(root, entry.RelPath) : null;
        if (entry != null && File.Exists(full))
        {
            var sha = await FileOps.HashAsync(full!, ct).ConfigureAwait(false);
            result.ParentModifiedLocally = sha != entry.LocalSha;
        }

        if (!lockedByMe) await Api.LockAsync(parentId, ct).ConfigureAwait(false);
        var item = new CheckinItem
        {
            DocumentId = parentId,
            BaseVersion = baseVersion,
            RelPath = version.RelPath,
            Sha256 = version.Sha256,
            Size = version.Size,
            Cad = version.Cad,
            Kind = version.Kind,
            CadVersion = version.CadVersion,
            CadVersionMajor = version.CadVersionMajor,
            Configurations = version.Configurations.ToList(),
            Properties = version.Properties.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
            Derived = version.Derived.ToList(),
        };
        foreach (var r in version.References)
        {
            if (bound.TryGetValue(r, out var b))
                item.References.Add(new CheckinReference { RefPath = r.RefPath, DocumentId = b.ChildId, Version = b.ChildVersion });
            else if (r.ChildDocumentId != null)
                item.References.Add(new CheckinReference { RefPath = r.RefPath, DocumentId = r.ChildDocumentId, Version = r.ChildVersion });
            else
                item.References.Add(new CheckinReference { RefPath = r.RefPath });
        }
        var request = new CheckinRequest
        {
            ProjectId = detail.Document.ProjectId,
            Comment = bindings.Count == 1 ? $"Поставлен на учёт и привязан «{Texts.FileNameOf(refPath)}»" : $"Поставлены на учёт и привязаны: {string.Join(", ", bindings.Select(b => "«" + Texts.FileNameOf(b.RefPath) + "»"))}",
            KeepLocks = lockedByMe,
            AllowUnresolvedReferences = item.References.Any(r => r.DocumentId == null && r.TempKey == null),
            AcceptNewerCadVersion = true, // байты те же, что у опубликованной версии
            Items = { item },
        };

        CheckinResponse response;
        try
        {
            response = await Api.CheckinAsync(request, ct).ConfigureAwait(false);
        }
        catch (PdmApiException ex)
        {
            if (!lockedByMe)
            {
                try { await Api.UnlockAsync(parentId, ct).ConfigureAwait(false); }
                catch (PdmApiException) { }
            }
            result.Problems.AddRange(PlanProblem.FromServer(ex));
            result.Message = $"Ссылка не привязана, ничего не опубликовано: {ex.Message}";
            return result;
        }

        result.ParentVersion = response.Items.Single(i => i.DocumentId == parentId).Version;
        if (entry != null && result.ParentModifiedLocally)
        {
            // Байты v{новая} совпадают с v{baseVersion}: локальные правки остаются правками относительно новой версии.
            entry.Version = result.ParentVersion;
            foreach (var (r, b) in bound) entry.Deps.Add(new WorkspaceDependency { DocumentId = b.ChildId, Version = b.ChildVersion, RefPath = r.RefPath });
            state.Save(root);
        }
        result.Success = true;
        result.Message = bindings.Count == 1
            ? $"Ссылка на «{Texts.FileNameOf(refPath)}» привязана к документу PDM: {label} v{result.ParentVersion}."
            : $"Ссылки ({bindings.Count}) привязаны к документам PDM: {label} v{result.ParentVersion}.";
        return result;
    }
}
