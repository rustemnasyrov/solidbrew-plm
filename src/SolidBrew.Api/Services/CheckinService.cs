using System.Globalization;
using Dapper;
using Npgsql;
using SolidBrew.Api.Auth;
using SolidBrew.Api.Blobs;
using SolidBrew.Api.Db;
using SolidBrew.Api.Http;
using SolidBrew.Core.Cad;
using SolidBrew.Core.Contracts;
using SolidBrew.Core.Hashing;
using SolidBrew.Core.Paths;

namespace SolidBrew.Api.Services;

/// <summary>Точки внедрения для тестов (например, сбой перед COMMIT сдачи).</summary>
public sealed class CheckinHooks
{
    /// <summary>Вызывается внутри транзакции сдачи после всех вставок, перед COMMIT.</summary>
    public Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? BeforeCommit { get; set; }
}

/// <summary>
/// Сдача: проверяет всё сразу (все проблемы одним списком) и публикует новые версии одной транзакцией.
/// Проверки, зависящие от БД, выполняются внутри транзакции после блокировки строк документов и блокировок,
/// поэтому параллельные операции не могут проскочить между проверкой и публикацией.
/// </summary>
public sealed class CheckinService(NpgsqlDataSource db, IBlobStore blobs, CheckinHooks hooks, ILogger<CheckinService> logger)
{
    public async Task<IResult> CheckinAsync(CheckinRequest request, CurrentUser user, CancellationToken ct)
    {
        if (user.RequireWriter("сдача документов") is { } denied) return denied;
        if (request.Items.Count == 0)
            return ApiErrors.Validation("Сдача пуста: выберите хотя бы один документ.");

        var problems = new List<ProblemDto>();
        LockDto? blockingLock = null;
        var items = request.Items;

        // --- Проверки без БД ---
        var tempKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        var documentItems = new Dictionary<Guid, int>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var label = Label(item, i);
            if (!RelPath.IsValid(item.RelPath))
            {
                var reason = RelPath.TryNormalize(item.RelPath, out var normalized, out var error)
                    ? $"путь должен быть в нормальном виде («{normalized}»)"
                    : error;
                Add(ProblemCodes.Validation, $"Недопустимый путь документа {label}: {reason}", item);
            }
            if (!Sha256Hex.IsValid(item.Sha256))
                Add(ProblemCodes.Validation, $"У документа {label} неверный хеш содержимого. Обновите клиент PDM и повторите сдачу.", item);
            if (item.Size < 0)
                Add(ProblemCodes.Validation, $"У документа {label} отрицательный размер файла.", item);
            if (string.IsNullOrWhiteSpace(item.Cad))
                Add(ProblemCodes.Validation, $"У документа {label} не указана система CAD.", item);

            if (item.DocumentId is { } id)
            {
                if (item.BaseVersion is null)
                    Add(ProblemCodes.Validation, $"Для документа {label} не указана исходная версия, от которой шла правка.", item);
                if (!documentItems.TryAdd(id, i))
                    Add(ProblemCodes.Validation, $"Документ {label} встречается в сдаче дважды.", item);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(item.TempKey))
                    Add(ProblemCodes.Validation, $"Новый документ {label} без временного ключа. Обновите клиент PDM.", item);
                else if (!tempKeys.TryAdd(item.TempKey, i))
                    Add(ProblemCodes.Validation, $"Временный ключ «{item.TempKey}» повторяется в сдаче (документ {label}).", item);
                if (item.BaseVersion is not null)
                    Add(ProblemCodes.Validation, $"Для нового документа {label} указана исходная версия — так быть не должно. Обновите клиент PDM.", item);
            }

            var derivedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var derived in item.Derived)
            {
                if (!RelPath.IsValid(derived.FileName) || derived.FileName.Contains(RelPath.Separator))
                    Add(ProblemCodes.Validation, $"Недопустимое имя производного файла «{derived.FileName}» у документа {label}.", item);
                else if (!derivedNames.Add(derived.FileName))
                    Add(ProblemCodes.Validation, $"Производный файл «{derived.FileName}» указан у документа {label} дважды.", item);
                if (!Sha256Hex.IsValid(derived.Sha256) || derived.Size < 0)
                    Add(ProblemCodes.Validation, $"У производного файла «{derived.FileName}» документа {label} неверный хеш или размер.", item);
            }
            foreach (var reference in item.References)
            {
                if (string.IsNullOrWhiteSpace(reference.RefPath))
                    Add(ProblemCodes.Validation, $"У документа {label} есть ссылка без пути.", item);
            }
        }

        // --- Наличие содержимого в архиве ---
        var hashes = items.Where(it => Sha256Hex.IsValid(it.Sha256)).Select(it => it.Sha256)
            .Concat(items.SelectMany(it => it.Derived).Where(d => Sha256Hex.IsValid(d.Sha256)).Select(d => d.Sha256));
        var missing = (await blobs.FindMissingAsync(hashes, ct)).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (missing.Contains(item.Sha256))
                Add(ProblemCodes.MissingObject, $"Содержимое документа {Label(item, i)} не загружено на сервер. Повторите сдачу — клиент загрузит файл заново.", item);
            foreach (var derived in item.Derived.Where(d => missing.Contains(d.Sha256)))
                Add(ProblemCodes.MissingObject, $"Производный файл «{derived.FileName}» документа {Label(item, i)} не загружен на сервер. Повторите сдачу.", item);
        }

        // --- Проверки по БД и публикация в одной транзакции ---
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var existingIds = documentItems.Keys.OrderBy(id => id).ToArray();
        var documents = (await connection.QueryAsync<DocRow>("""
            SELECT d.id, d.project_id, p.kind AS project_kind, p.name AS project_name, d.status, d.current_rel_path, d.rel_key
            FROM documents d JOIN projects p ON p.id = d.project_id
            WHERE d.id = ANY(@existingIds)
            ORDER BY d.id
            FOR UPDATE OF d
            """, new { existingIds }, tx)).ToDictionary(d => d.Id);
        var locks = (await DocumentQueries.GetLocksAsync(connection, existingIds, tx, forUpdate: true)).ToDictionary(l => l.DocumentId);
        var latest = (await connection.QueryAsync<(Guid DocumentId, int Latest)>("""
            SELECT document_id, max(number) FROM versions WHERE document_id = ANY(@existingIds) GROUP BY document_id
            """, new { existingIds }, tx)).ToDictionary(r => r.DocumentId, r => r.Latest);

        var hasNew = items.Any(it => it.DocumentId is null);
        ProjectRow? project = null;
        if (hasNew)
        {
            project = await connection.QuerySingleOrDefaultAsync<ProjectRow>(
                "SELECT id, name, kind FROM projects WHERE id = @ProjectId", new { request.ProjectId }, tx);
            if (project is null)
                Add(ProblemCodes.Validation, $"Проект {request.ProjectId} для новых документов не найден. Выберите проект и повторите сдачу.", null);
        }

        // Существующие документы: состояние, права, блокировки, исходные версии.
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.DocumentId is not { } id) continue;
            var label = Label(item, i);
            if (!documents.TryGetValue(id, out var doc))
            {
                Add(ProblemCodes.NotFound, $"Документ {label} (ID {id}) не найден на сервере. Возможно, рабочая папка относится к другому серверу PDM.", item);
                continue;
            }
            label = $"«{doc.CurrentRelPath}»";
            if (doc.Status == "unused")
                Add(ProblemCodes.Validation, $"Документ {label} помечен «не используется». Снимите пометку и повторите сдачу.", item);
            if (doc.Status == "deleted")
                Add(ProblemCodes.Validation, $"Документ {label} удалён из PDM администратором — сдать его новую версию нельзя. Поставьте файл на учёт заново, если он нужен.", item);
            if (doc.ProjectKind == "library" && !user.IsAdmin)
                Add(ProblemCodes.LibraryReadOnly, $"Документ {label} находится в библиотеке «{doc.ProjectName}» — изменять библиотеку может только администратор. Исключите документ из сдачи или обратитесь к администратору.", item);

            if (!locks.TryGetValue(id, out var lockDto))
                Add(ProblemCodes.NotLocked, $"Документ {label} не взят вами в работу. Возьмите его в работу и повторите сдачу.", item);
            else if (lockDto.UserId != user.Id)
            {
                blockingLock ??= lockDto;
                Add(ProblemCodes.LockedByOther, ApiErrors.LockedByOtherMessage(doc.CurrentRelPath, lockDto), item);
            }

            var last = latest.GetValueOrDefault(id);
            if (item.BaseVersion is { } baseVersion && baseVersion != last)
                Add(ProblemCodes.StaleBaseVersion,
                    $"Документ {label} изменён на сервере: вы правили версию v{baseVersion}, а последняя — v{last}. " +
                    "Получите последнюю версию, перенесите в неё правки и повторите сдачу.", item);
        }

        // Новые документы в библиотеке — только администратор.
        if (project is { Kind: "library" } && !user.IsAdmin)
        {
            foreach (var item in items.Where(it => it.DocumentId is null))
                Add(ProblemCodes.LibraryReadOnly, $"Новый документ «{item.RelPath}» нельзя добавить в библиотеку «{project.Name}»: это может только администратор.", item);
        }

        // Пути: уникальны внутри сдачи и не заняты другими используемыми документами проекта.
        var targets = new List<(int Index, Guid ProjectId, string Key)>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!RelPath.IsValid(item.RelPath)) continue;
            Guid? projectId = item.DocumentId is { } id ? documents.GetValueOrDefault(id)?.ProjectId : project?.Id;
            if (projectId is { } pid) targets.Add((i, pid, RelPath.Key(item.RelPath)));
        }
        foreach (var group in targets.GroupBy(t => (t.ProjectId, t.Key)).Where(g => g.Count() > 1))
        {
            foreach (var t in group.Skip(1))
                Add(ProblemCodes.DuplicatePath, $"В сдаче несколько документов с путём «{items[t.Index].RelPath}». Переименуйте один из файлов.", items[t.Index]);
        }
        if (targets.Count > 0)
        {
            var occupied = await connection.QueryAsync<(Guid Id, Guid ProjectId, string RelKey, string CurrentRelPath)>("""
                SELECT d.id, d.project_id, d.rel_key, d.current_rel_path
                FROM documents d
                JOIN unnest(@pids, @keys) AS t(pid, k) ON d.project_id = t.pid AND d.rel_key = t.k
                WHERE d.status = 'active' AND NOT (d.id = ANY(@existingIds))
                """, new { pids = targets.Select(t => t.ProjectId).ToArray(), keys = targets.Select(t => t.Key).ToArray(), existingIds }, tx);
            foreach (var other in occupied)
            {
                foreach (var t in targets.Where(t => t.ProjectId == other.ProjectId && t.Key == other.RelKey))
                    Add(ProblemCodes.DuplicatePath,
                        $"Путь «{items[t.Index].RelPath}» уже занят другим документом проекта («{other.CurrentRelPath}»). " +
                        "Переименуйте файл или возьмите в работу существующий документ.", items[t.Index]);
            }
        }

        // Ссылки.
        var pinned = items.SelectMany(it => it.References)
            .Where(r => r.DocumentId is not null && r.Version is not null)
            .Select(r => (r.DocumentId!.Value, r.Version!.Value)).Distinct().ToArray();
        var existingVersions = new HashSet<(Guid, int)>();
        if (pinned.Length > 0)
        {
            var found = await connection.QueryAsync<(Guid DocumentId, int Number)>("""
                SELECT v.document_id, v.number FROM versions v
                JOIN unnest(@ids, @numbers) AS t(id, n) ON v.document_id = t.id AND v.number = t.n
                """, new { ids = pinned.Select(p => p.Item1).ToArray(), numbers = pinned.Select(p => p.Item2).ToArray() }, tx);
            existingVersions = found.Select(f => (f.DocumentId, f.Number)).ToHashSet();
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var label = Label(item, i);
            foreach (var reference in item.References)
            {
                if (reference.DocumentId is { } childId)
                {
                    if (reference.Version is { } childVersion)
                    {
                        if (!existingVersions.Contains((childId, childVersion)))
                            Add(ProblemCodes.MissingDependency,
                                $"Документ {label} ссылается на «{reference.RefPath}» версии v{childVersion}, которой нет на сервере. " +
                                "Откройте документ с опубликованной версией зависимости и повторите сдачу.", item);
                    }
                    else if (!documentItems.ContainsKey(childId))
                    {
                        Add(ProblemCodes.MissingDependency,
                            $"Документ {label} ссылается на новую версию «{reference.RefPath}», но этот документ не входит в сдачу. " +
                            "Добавьте его в сдачу или сошлитесь на опубликованную версию.", item);
                    }
                }
                else if (!string.IsNullOrEmpty(reference.TempKey))
                {
                    if (!tempKeys.ContainsKey(reference.TempKey))
                        Add(ProblemCodes.MissingDependency,
                            $"Документ {label} ссылается на новый документ «{reference.RefPath}», которого нет в сдаче. Добавьте его в сдачу.", item);
                }
                else if (!request.AllowUnresolvedReferences)
                {
                    Add(ProblemCodes.UnresolvedReference,
                        $"Документ {label} ссылается на «{reference.RefPath}», но такой файл не найден. " +
                        "Найдите файл и добавьте его в сдачу или подтвердите сдачу с отсутствующими ссылками.", item);
                }
            }
        }

        // Версия CAD новее рабочей.
        if (!request.AcceptNewerCadVersion)
        {
            var working = await LoadWorkingVersionsAsync(connection, tx);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.CadVersionMajor is { } major && working.TryGetValue(item.Cad, out var limit) && major > limit)
                    Add(ProblemCodes.NewerCadVersion,
                        $"Документ {Label(item, i)} сохранён в {item.Cad} {item.CadVersion ?? major.ToString(CultureInfo.InvariantCulture)}, " +
                        $"а рабочая версия — {limit}. Коллеги с рабочей версией не смогут его открыть. " +
                        "Пересохраните файл в рабочей версии или подтвердите сдачу.", item);
            }
        }

        if (problems.Count > 0)
        {
            await tx.RollbackAsync(ct);
            var message = problems.Count == 1
                ? problems[0].Message + " Ничего не опубликовано, блокировки и локальные правки сохранены."
                : $"Сдача не выполнена: найдено проблем — {problems.Count}. Ничего не опубликовано, блокировки и локальные правки сохранены.";
            return ApiErrors.Error(StatusCodes.Status422UnprocessableEntity, message, problems, blockingLock);
        }

        // --- Публикация ---
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        var checkinId = await connection.ExecuteScalarAsync<long>(
            "INSERT INTO checkins (user_id, comment) VALUES (@userId, @comment) RETURNING id",
            new { userId = user.Id, comment }, tx);

        var docIds = new Guid[items.Count];
        var numbers = new int[items.Count];
        var newVersionOf = new Dictionary<Guid, int>();
        var tempDocs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.DocumentId is { } id)
            {
                docIds[i] = id;
                numbers[i] = latest[id] + 1;
            }
            else
            {
                docIds[i] = Guid.CreateVersion7();
                numbers[i] = 1;
                tempDocs[item.TempKey!] = docIds[i];
                await connection.ExecuteAsync("""
                    INSERT INTO documents (id, project_id, cad, kind, current_rel_path, rel_key, created_by)
                    VALUES (@id, @projectId, @cad, @kind, @relPath, @relKey, @userId)
                    """, new
                {
                    id = docIds[i], projectId = project!.Id, cad = item.Cad.Trim(), kind = DbText.Kind(item.Kind),
                    relPath = item.RelPath, relKey = RelPath.Key(item.RelPath), userId = user.Id,
                }, tx);
            }
            newVersionOf[docIds[i]] = numbers[i];
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var versionId = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO versions (document_id, number, checkin_id, author_id, sha256, size, rel_path, cad_version, cad_version_major,
                                      configurations, properties, designation, name)
                VALUES (@documentId, @number, @checkinId, @authorId, @sha256, @size, @relPath, @cadVersion, @cadVersionMajor,
                        CAST(@configurations AS jsonb), CAST(@properties AS jsonb), @designation, @name)
                RETURNING id
                """, new
            {
                documentId = docIds[i], number = numbers[i], checkinId, authorId = user.Id, sha256 = item.Sha256, size = item.Size,
                relPath = item.RelPath, cadVersion = item.CadVersion, cadVersionMajor = item.CadVersionMajor,
                configurations = DbText.ToJson(item.Configurations), properties = DbText.ToJson(item.Properties),
                designation = DocumentProperties.Designation(item.Properties), name = DocumentProperties.Name(item.Properties),
            }, tx);

            foreach (var reference in item.References)
            {
                Guid? childId = null;
                int? childVersion = null;
                if (reference.DocumentId is { } refId)
                {
                    childId = refId;
                    childVersion = reference.Version ?? newVersionOf[refId];
                }
                else if (!string.IsNullOrEmpty(reference.TempKey))
                {
                    childId = tempDocs[reference.TempKey];
                    childVersion = 1;
                }
                await connection.ExecuteAsync("""
                    INSERT INTO version_refs (version_id, ref_path, child_document_id, child_version)
                    VALUES (@versionId, @refPath, @childId, @childVersion)
                    """, new { versionId, refPath = reference.RefPath, childId, childVersion }, tx);
            }

            foreach (var derived in item.Derived)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO derived_files (version_id, file_name, sha256, size) VALUES (@versionId, @fileName, @sha256, @size)
                    """, new { versionId, fileName = derived.FileName, sha256 = derived.Sha256, size = derived.Size }, tx);
            }

            string? previousPath = null;
            if (item.DocumentId is { } existing && documents[existing].CurrentRelPath != item.RelPath)
            {
                previousPath = documents[existing].CurrentRelPath;
                await connection.ExecuteAsync(
                    "UPDATE documents SET current_rel_path = @relPath, rel_key = @relKey WHERE id = @id",
                    new { id = existing, relPath = item.RelPath, relKey = RelPath.Key(item.RelPath) }, tx);
            }

            await Journal.WriteAsync(connection, user.Id, JournalActions.Checkin, docIds[i], numbers[i],
                new { checkinId, relPath = item.RelPath, previousRelPath = previousPath, comment }, tx);
        }

        if (request.KeepLocks)
        {
            var created = docIds.Where((_, i) => items[i].DocumentId is null).ToArray();
            await connection.ExecuteAsync("""
                INSERT INTO locks (document_id, user_id) SELECT unnest(@created), @userId ON CONFLICT DO NOTHING
                """, new { created, userId = user.Id }, tx);
        }
        else
        {
            await connection.ExecuteAsync("DELETE FROM locks WHERE document_id = ANY(@existingIds) AND user_id = @userId",
                new { existingIds, userId = user.Id }, tx);
        }

        try
        {
            if (hooks.BeforeCommit is { } hook) await hook(connection, tx, ct);
            await tx.CommitAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.UniqueViolation)
        {
            logger.LogWarning(ex, "Сдача отклонена при фиксации: конфликт путей или номеров версий");
            return ApiErrors.Unprocessable(ProblemCodes.DuplicatePath,
                "Сдача не выполнена: одновременно с вами другой пользователь занял тот же путь или опубликовал версию. " +
                "Ничего не опубликовано, блокировки и локальные правки сохранены. Обновите список документов и повторите сдачу.");
        }

        logger.LogInformation("Сдача {CheckinId}: {Count} документов, пользователь {Login}", checkinId, items.Count, user.Login);
        return Results.Ok(new CheckinResponse
        {
            CheckinId = checkinId,
            Items = items.Select((item, i) => new CheckinResultItem
            {
                TempKey = item.TempKey,
                DocumentId = docIds[i],
                Version = numbers[i],
                RelPath = item.RelPath,
            }).ToList(),
        });

        void Add(string code, string message, CheckinItem? item) => problems.Add(new ProblemDto
        {
            Code = code,
            Message = message,
            DocumentId = item?.DocumentId,
            TempKey = item?.DocumentId is null ? item?.TempKey : null,
            RelPath = item?.RelPath,
        });
    }

    internal static async Task<Dictionary<string, int>> LoadWorkingVersionsAsync(NpgsqlConnection connection, NpgsqlTransaction? tx)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rows = await connection.QueryAsync<(string Key, string Value)>(
            "SELECT key, value FROM settings WHERE key LIKE 'cad.%.workingVersion'", transaction: tx);
        foreach (var (key, value) in rows)
        {
            var cad = key["cad.".Length..^".workingVersion".Length];
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                result[cad] = version;
        }
        return result;
    }

    private static string Label(CheckinItem item, int index) =>
        string.IsNullOrWhiteSpace(item.RelPath) ? $"№{index + 1}" : $"«{item.RelPath}»";

    private sealed class DocRow
    {
        public Guid Id { get; set; }
        public Guid ProjectId { get; set; }
        public string ProjectKind { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string Status { get; set; } = "";
        public string CurrentRelPath { get; set; } = "";
        public string RelKey { get; set; } = "";
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
    }
}
