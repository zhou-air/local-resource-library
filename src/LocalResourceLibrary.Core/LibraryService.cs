using Microsoft.Data.Sqlite;
using static LocalResourceLibrary.Core.LibraryRepository;

namespace LocalResourceLibrary.Core;

/// <summary>Library metadata and resource operations. Adding/removing membership never copies or deletes a resource.</summary>
public sealed partial class LibraryService
{
    private readonly LibraryRepository repository;
    private readonly IResourcePlatform platform;
    private readonly IFileIdentityProvider fileIdentityProvider;
    private readonly object gate = new();
    public string DatabasePath => repository.DatabasePath;

    public LibraryService(string databasePath, IResourcePlatform? platform = null, IFileIdentityProvider? fileIdentityProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.platform = platform ?? new WindowsResourcePlatform();
        this.fileIdentityProvider = fileIdentityProvider ?? new WindowsFileIdentityProvider();
        repository = new LibraryRepository(databasePath);
    }

    public LibrarySnapshot GetSnapshot(bool checkPaths = true)
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction(deferred: !checkPaths);
            var snapshot = ReadSnapshot(connection, transaction);
            if (checkPaths)
            {
                snapshot = snapshot with
                {
                    Items = snapshot.Items.Select(item =>
                    {
                        var exists = Exists(item.Target, item.Type);
                        // Opportunistically upgrade legacy files, without locating missing targets.
                        if (exists) item = BackfillIdentity(connection, transaction, item);
                        return item with { IsMissing = !exists };
                    }).ToArray()
                };
            }
            transaction.Commit();
            return snapshot;
        }
    }

    /// <summary>Get the current context, optionally recovering a saved file identity without opening its contents.</summary>
    public ResourceItem GetResource(string id, bool resolvePath = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction(deferred: !resolvePath);
            var item = RequireItem(connection, transaction, id);
            item = resolvePath ? ResolveResourceTarget(connection, transaction, item, throwIfMissing: false)
                : item with { IsMissing = !Exists(item.Target, item.Type) };
            transaction.Commit();
            return item;
        }
    }

    /// <summary>Register one target atomically; existing records retain metadata and only gain requested memberships.</summary>
    public AddResourceResult AddResource(string type, string target, string? alias = null, string? description = null,
        string? note = null, IEnumerable<string>? projectIds = null)
    {
        if (type is not ("file" or "folder" or ResourceUrls.Type))
            throw new ArgumentException("资源类型必须为 file、folder 或 url。", nameof(type));
        return RegisterResource(type, target, alias, description, note, projectIds ?? []);
    }

    public AddResourcesResult AddPaths(IEnumerable<string> paths, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var added = 0;
        var existing = 0;
        var errors = new List<string>();
        Write((connection, transaction) =>
        {
            if (projectId != null) RequireProject(connection, transaction, projectId);
            foreach (var rawPath in paths)
            {
                // Preserve the legacy per-path error contract, but commit successful imports
                // together and record the complete call as one undo unit.
                transaction.Save("register_path");
                try
                {
                    var target = ResourcePaths.Normalize(rawPath);
                    var result = RegisterResourceInTransaction(connection, transaction, null, target, ResourcePaths.Key(target),
                        null, null, null, projectId == null ? [] : [projectId], null, out _);
                    transaction.Release("register_path");
                    if (result.Added) added++; else existing++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException or SqliteException or InvalidOperationException)
                {
                    transaction.Rollback("register_path");
                    transaction.Release("register_path");
                    errors.Add($"{rawPath}: {exception.Message}");
                }
            }
        });
        return new AddResourcesResult(added, existing, errors);
    }

    /// <summary>Collect an exact URL once, or add memberships without overwriting the existing context.</summary>
    public AddUrlResult AddUrl(string url, string alias, string description, string note,
        IEnumerable<string> projectIds, byte[]? favicon = null)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var result = RegisterResource(ResourceUrls.Type, url, alias, description, note, projectIds, favicon);
        return new AddUrlResult(result.Item, result.Added);
    }

    private AddResourceResult RegisterResource(string? requestedType, string rawTarget, string? alias, string? description,
        string? note, IEnumerable<string> projectIds, byte[]? favicon = null)
    {
        var target = requestedType == ResourceUrls.Type ? ResourceUrls.Normalize(rawTarget) : ResourcePaths.Normalize(rawTarget);
        var key = requestedType == ResourceUrls.Type ? ResourceUrls.Key(target) : ResourcePaths.Key(target);
        var ids = projectIds.Distinct(StringComparer.Ordinal).ToArray();
        var icon = ResourceUrls.CopyFavicon(favicon);
        AddResourceResult result = null!;
        Write((connection, transaction) => result = RegisterResourceInTransaction(connection, transaction,
            requestedType, target, key, alias, description, note, ids, icon, out _));
        return result;
    }

    /// <summary>Update the URL and its context atomically. A changed target drops an old icon unless one is supplied.</summary>
    public void UpdateUrl(string id, string url, string alias, string description, string note,
        IEnumerable<string> projectIds, byte[]? favicon = null)
    {
        var target = ResourceUrls.Normalize(url);
        var key = ResourceUrls.Key(target);
        ArgumentNullException.ThrowIfNull(projectIds);
        var ids = projectIds.Distinct(StringComparer.Ordinal).ToArray();
        var icon = ResourceUrls.CopyFavicon(favicon);
        Write((connection, transaction) =>
        {
            var item = RequireItem(connection, transaction, id);
            if (!item.IsUrl) throw new NotSupportedException("此操作仅适用于网址资源。");
            foreach (var projectId in ids) RequireProject(connection, transaction, projectId);
            var otherId = Scalar(connection, transaction, "SELECT id FROM Item WHERE path_key=$key AND id<>$id;", ("$key", key), ("$id", id)) as string;
            if (otherId != null) throw new InvalidOperationException("资源库中已存在完全相同的网址，请使用现有记录。");
            Execute(connection, transaction, """
                UPDATE Item SET target=$target,path_key=$key,alias=$alias,description=$description,note=$note,
                    favicon=CASE WHEN $icon IS NOT NULL THEN $icon WHEN target=$target THEN favicon ELSE NULL END,
                    updated_at=$now WHERE id=$id;
                """, ("$target", target), ("$key", key), ("$alias", alias ?? ""), ("$description", description ?? ""),
                ("$note", note ?? ""), ("$icon", icon), ("$now", Time(DateTimeOffset.UtcNow)), ("$id", id));
            Execute(connection, transaction, "DELETE FROM ProjectItem WHERE item_id=$id;", ("$id", id));
            foreach (var projectId in ids)
                Execute(connection, transaction, "INSERT INTO ProjectItem(project_id,item_id) VALUES($project,$item);", ("$project", projectId), ("$item", id));
        });
    }

    public void UpdateItem(string id, string alias, string description, string note, IEnumerable<string> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var ids = projectIds.Distinct(StringComparer.Ordinal).ToArray();
        Write((connection, transaction) =>
        {
            RequireItem(connection, transaction, id);
            foreach (var projectId in ids) RequireProject(connection, transaction, projectId);
            Execute(connection, transaction, "UPDATE Item SET alias=$alias,description=$description,note=$note,updated_at=$now WHERE id=$id;",
                ("$alias", alias ?? ""), ("$description", description ?? ""), ("$note", note ?? ""), ("$now", Time(DateTimeOffset.UtcNow)), ("$id", id));
            Execute(connection, transaction, "DELETE FROM ProjectItem WHERE item_id=$id;", ("$id", id));
            foreach (var projectId in ids)
                Execute(connection, transaction, "INSERT INTO ProjectItem(project_id,item_id) VALUES($project,$item);", ("$project", projectId), ("$item", id));
        });
    }

    public ResourceItem PatchResourceMetadata(ResourceMetadataPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrWhiteSpace(patch.Id);
        ResourceItem result = null!;
        Write((connection, transaction) =>
        {
            var item = RequireItem(connection, transaction, patch.Id);
            ApplyMetadataPatch(connection, transaction, item, patch);
            result = RequireItem(connection, transaction, patch.Id);
        });
        return result with { IsMissing = !Exists(result.Target, result.Type) };
    }

    /// <summary>Add/remove only the specified memberships; unrelated projects stay intact.</summary>
    public ResourceItem ChangeResourceProjects(string id, IEnumerable<string>? addProjectIds = null,
        IEnumerable<string>? removeProjectIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var (add, remove) = MembershipDelta(addProjectIds, removeProjectIds);
        ResourceItem result = null!;
        Write((connection, transaction) =>
        {
            RequireItem(connection, transaction, id);
            ApplyMembershipDelta(connection, transaction, id, add, remove);
            result = RequireItem(connection, transaction, id);
        });
        return result with { IsMissing = !Exists(result.Target, result.Type) };
    }

    /// <summary>Commit a previously reviewed batch only if every resource still matches its expected context.</summary>
    public ResourceMetadataBatchResult ApplyResourceMetadataBatch(IEnumerable<ResourceMetadataPatch> patches,
        IEnumerable<ResourceItem> expectedItems)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(expectedItems);
        var changes = patches.ToArray();
        var expected = expectedItems.ToArray();
        lock (gate)
        {
            try
            {
                using var connection = repository.Connect();
                using var transaction = connection.BeginTransaction();
                var failures = new List<ResourceMetadataFailure>();
                var originals = new Dictionary<string, ResourceItem>(StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var patch in changes)
                {
                    if (patch == null || string.IsNullOrWhiteSpace(patch.Id))
                    {
                        failures.Add(new ResourceMetadataFailure(patch?.Id ?? "", "资源 ID 不能为空。"));
                        continue;
                    }
                    if (!seen.Add(patch.Id))
                    {
                        failures.Add(new ResourceMetadataFailure(patch.Id, "同一批次不能重复修改同一资源。"));
                        continue;
                    }
                    var candidates = expected.Where(item => item.Id == patch.Id).ToArray();
                    if (candidates.Length != 1)
                    {
                        failures.Add(new ResourceMetadataFailure(patch.Id, "此资源必须提供唯一的预览快照。"));
                        continue;
                    }
                    var item = ReadItem(connection, transaction, patch.Id);
                    if (item == null)
                        failures.Add(new ResourceMetadataFailure(patch.Id, "资源记录不存在。"));
                    else if (!SameResourceContext(item, candidates[0]))
                        failures.Add(new ResourceMetadataFailure(patch.Id, "资源在预览后已发生变化，请重新预览。"));
                    else originals.Add(patch.Id, item);
                }
                if (failures.Count > 0) return new ResourceMetadataBatchResult(false, [], failures);
                var before = enableHistory ? ReadHistoryState(connection, transaction) : null;
                PrepareHistory(before);
                foreach (var patch in changes) ApplyMetadataPatch(connection, transaction, originals[patch.Id], patch);
                var items = changes.Select(patch =>
                {
                    var item = RequireItem(connection, transaction, patch.Id);
                    return item with { IsMissing = !Exists(item.Target, item.Type) };
                }).ToArray();
                var after = enableHistory ? ReadHistoryState(connection, transaction) : null;
                transaction.Commit();
                RecordHistory(before, after, "EditResource");
                return new ResourceMetadataBatchResult(true, items, []);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                // Transaction disposal rolls back every write, including an error during commit.
                return new ResourceMetadataBatchResult(false, [], changes.Select(patch =>
                    new ResourceMetadataFailure(patch.Id, $"批量写入失败，全部变更已回滚：{exception.Message}")).ToArray());
            }
        }
    }

    /// <summary>Save only edited UI fields; conflict checks and membership changes use the same transaction.</summary>
    public ResourceItem ApplyResourceEdit(ResourceItem expected, ResourceMetadataPatch patch,
        IEnumerable<string> addProjectIds, IEnumerable<string> removeProjectIds, string? url = null, byte[]? favicon = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Id != expected.Id) throw new ArgumentException("资源补丁与编辑快照的 ID 不一致。", nameof(patch));
        var (add, remove) = MembershipDelta(addProjectIds, removeProjectIds);
        var target = url == null ? null : ResourceUrls.Normalize(url);
        var icon = ResourceUrls.CopyFavicon(favicon);
        ResourceItem result = null!;
        Write((connection, transaction) =>
        {
            var current = RequireItem(connection, transaction, expected.Id);
            if ((patch.Alias != null && current.Alias != expected.Alias) ||
                (patch.Description != null && current.Description != expected.Description) ||
                (patch.Note != null && current.Note != expected.Note) ||
                (target != null && current.Target != expected.Target) ||
                (icon != null && !SameBytes(current.Favicon, expected.Favicon)))
                throw new InvalidOperationException("资源已被其他进程修改，请保留当前草稿，刷新后重新编辑。");
            if (target != null || icon != null)
            {
                if (!current.IsUrl) throw new NotSupportedException("网址和网站图标只能用于 URL 资源。");
                var newTarget = target ?? current.Target;
                var key = ResourceUrls.Key(newTarget);
                if (Scalar(connection, transaction, "SELECT id FROM Item WHERE path_key=$key AND id<>$id;",
                        ("$key", key), ("$id", current.Id)) is string)
                    throw new InvalidOperationException("资源库中已存在完全相同的网址，请使用现有记录。");
                if (newTarget != current.Target || icon != null)
                    Execute(connection, transaction, """
                        UPDATE Item SET target=$target,path_key=$key,
                            favicon=CASE WHEN $icon IS NOT NULL THEN $icon WHEN target=$target THEN favicon ELSE NULL END,
                            updated_at=$now WHERE id=$id;
                        """, ("$target", newTarget), ("$key", key), ("$icon", icon),
                        ("$now", Time(DateTimeOffset.UtcNow)), ("$id", current.Id));
            }
            ApplyMetadataPatch(connection, transaction, current, patch);
            ApplyMembershipDelta(connection, transaction, current.Id, add, remove);
            result = RequireItem(connection, transaction, current.Id);
        });
        return result with { IsMissing = !Exists(result.Target, result.Type) };
    }

    private static void ApplyMetadataPatch(SqliteConnection connection, SqliteTransaction transaction,
        ResourceItem current, ResourceMetadataPatch patch)
    {
        if ((patch.Alias == null || patch.Alias == current.Alias) &&
            (patch.Description == null || patch.Description == current.Description) &&
            (patch.Note == null || patch.Note == current.Note)) return;
        Execute(connection, transaction, """
            UPDATE Item SET alias=COALESCE($alias,alias),description=COALESCE($description,description),
                note=COALESCE($note,note),updated_at=$now WHERE id=$id;
            """, ("$alias", patch.Alias), ("$description", patch.Description), ("$note", patch.Note),
            ("$now", Time(DateTimeOffset.UtcNow)), ("$id", patch.Id));
    }

    private static (string[] Add, string[] Remove) MembershipDelta(IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        var added = (add ?? []).Distinct(StringComparer.Ordinal).ToArray();
        var removed = (remove ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (added.Concat(removed).Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("项目 ID 不能为空。");
        if (added.Intersect(removed, StringComparer.Ordinal).Any()) throw new ArgumentException("不能同时增加和移除同一个项目。");
        return (added, removed);
    }

    private static void ApplyMembershipDelta(SqliteConnection connection, SqliteTransaction transaction,
        string itemId, string[] add, string[] remove)
    {
        foreach (var projectId in add.Concat(remove)) RequireProject(connection, transaction, projectId);
        var changed = 0;
        foreach (var projectId in add)
            changed += Execute(connection, transaction, "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                ("$project", projectId), ("$item", itemId));
        foreach (var projectId in remove)
            changed += Execute(connection, transaction, "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
                ("$project", projectId), ("$item", itemId));
        if (changed > 0) Touch(connection, transaction, itemId);
    }

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    private static bool SameResourceContext(ResourceItem current, ResourceItem expected) =>
        current.Id == expected.Id && current.Type == expected.Type && current.Target == expected.Target &&
        current.Alias == expected.Alias && current.Description == expected.Description && current.Note == expected.Note &&
        current.CreatedAt == expected.CreatedAt && current.UpdatedAt == expected.UpdatedAt &&
        current.LastOpenedAt == expected.LastOpenedAt && current.OpenCount == expected.OpenCount &&
        current.FileIdentity == expected.FileIdentity && SameBytes(current.Favicon, expected.Favicon) &&
        current.Projects.OrderBy(project => project.Id, StringComparer.Ordinal)
            .SequenceEqual(expected.Projects.OrderBy(project => project.Id, StringComparer.Ordinal));

    public Project GetProject(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (gate)
        {
            using var connection = repository.Connect();
            return ReadProject(connection, null, id) ?? throw new KeyNotFoundException("项目不存在。");
        }
    }

    public Project CreateProject(string name, string description = "", string color = ProjectColors.Default, string? groupId = null)
    {
        name = ValidateProjectName(name);
        color = ProjectColors.Normalize(color);
        var project = new Project(Guid.NewGuid().ToString("N"), name, description ?? "", Color: color, GroupId: groupId);
        Write((connection, transaction) =>
        {
            EnsureProjectNameAvailable(connection, transaction, name);
            if (groupId != null) RequireProjectGroup(connection, transaction, groupId);
            project = project with { SortOrder = NextProjectOrder(connection, transaction, false, groupId) };
            Execute(connection, transaction, """
                INSERT INTO Project(id,name,name_key,description,sort_order,color,group_id)
                VALUES($id,$name,$key,$description,$order,$color,$group);
                """, ("$id", project.Id), ("$name", name), ("$key", name.ToUpperInvariant()),
                ("$description", project.Description), ("$order", project.SortOrder), ("$color", color), ("$group", groupId));
        });
        return project;
    }

    public void UpdateProject(string id, string name, string description = "")
    {
        name = ValidateProjectName(name);
        Write((connection, transaction) =>
        {
            RequireProject(connection, transaction, id);
            EnsureProjectNameAvailable(connection, transaction, name, id);
            Execute(connection, transaction, "UPDATE Project SET name=$name,name_key=$key,description=$description WHERE id=$id;",
                ("$id", id), ("$name", name), ("$key", name.ToUpperInvariant()), ("$description", description ?? ""));
        });
    }

    public Project PatchProject(string id, string? name = null, string? description = null, Project? expected = null,
        string? color = null, string? groupId = null, bool clearGroup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (name != null) name = ValidateProjectName(name);
        if (color != null) color = ProjectColors.Normalize(color);
        if (clearGroup && groupId != null) throw new ArgumentException("清除分组时不能同时指定分组 ID。", nameof(groupId));
        Project result = null!;
        Write((connection, transaction) =>
        {
            var current = ReadProject(connection, transaction, id) ?? throw new KeyNotFoundException("项目不存在。");
            if (expected != null && (expected.Id != id ||
                (name != null && current.Name != expected.Name) ||
                (description != null && current.Description != expected.Description) ||
                (color != null && current.Color != expected.Color) ||
                ((groupId != null || clearGroup) && current.GroupId != expected.GroupId)))
                throw new InvalidOperationException("项目已被其他进程修改，请刷新后重试。");
            var newName = name ?? current.Name;
            var newGroupId = clearGroup ? null : groupId ?? current.GroupId;
            if (groupId != null) RequireProjectGroup(connection, transaction, groupId);
            var newOrder = newGroupId != current.GroupId && !current.IsPinned
                ? NextProjectOrder(connection, transaction, false, newGroupId) : current.SortOrder;
            if (name != null) EnsureProjectNameAvailable(connection, transaction, newName, id);
            Execute(connection, transaction, """
                UPDATE Project SET name=$name,name_key=$key,description=COALESCE($description,description),
                    color=COALESCE($color,color),group_id=$group,sort_order=$order WHERE id=$id;
                """, ("$id", id), ("$name", newName), ("$key", newName.ToUpperInvariant()),
                ("$description", description), ("$color", color), ("$group", newGroupId), ("$order", newOrder));
            result = current with { Name = newName, Description = description ?? current.Description,
                Color = color ?? current.Color, GroupId = newGroupId, SortOrder = newOrder };
        });
        return result;
    }

    public void SetProjectPinned(string id, bool pinned) => Write((connection, transaction) =>
    {
        var current = ReadProject(connection, transaction, id) ?? throw new KeyNotFoundException("项目不存在。");
        if (current.IsPinned == pinned) return;
        Execute(connection, transaction, """
            UPDATE Project SET is_pinned=$pinned,sort_order=$order WHERE id=$id;
            """, ("$id", id), ("$pinned", pinned ? 1 : 0),
            ("$order", NextProjectOrder(connection, transaction, pinned, current.GroupId)));
    });

    /// <summary>Reorder pinned projects globally; ordinary projects adopt the target's group.</summary>
    public void MoveProject(string id, string targetId, bool after) => Write((connection, transaction) =>
    {
        var projects = ReadSnapshot(connection, transaction).Projects;
        var source = projects.FirstOrDefault(project => project.Id == id) ?? throw new KeyNotFoundException("项目不存在。");
        var target = projects.FirstOrDefault(project => project.Id == targetId) ?? throw new KeyNotFoundException("项目不存在。");
        if (source.IsPinned != target.IsPinned) throw new InvalidOperationException("只能在同一置顶分组内排序。");
        if (id == targetId) return;
        var group = projects.Where(project => project.IsPinned == source.IsPinned &&
            (source.IsPinned || project.GroupId == target.GroupId) && project.Id != id).ToList();
        if (!source.IsPinned && source.GroupId != target.GroupId)
            Execute(connection, transaction, "UPDATE Project SET group_id=$group WHERE id=$id;",
                ("$group", target.GroupId), ("$id", id));
        group.Insert(group.FindIndex(project => project.Id == targetId) + (after ? 1 : 0), source);
        for (var index = 0; index < group.Count; index++)
            Execute(connection, transaction, "UPDATE Project SET sort_order=$order WHERE id=$id;",
                ("$id", group[index].Id), ("$order", index));
    });

    /// <summary>Change group without changing pin status; unpinned projects append to the target group.</summary>
    public void MoveProjectToGroup(string id, string? groupId) => Write((connection, transaction) =>
    {
        var current = ReadProject(connection, transaction, id) ?? throw new KeyNotFoundException("项目不存在。");
        if (groupId != null) RequireProjectGroup(connection, transaction, groupId);
        if (current.GroupId == groupId) return;
        Execute(connection, transaction, "UPDATE Project SET group_id=$group,sort_order=$order WHERE id=$id;",
            ("$id", id), ("$group", groupId), ("$order", current.IsPinned ? current.SortOrder :
                NextProjectOrder(connection, transaction, false, groupId)));
    });

    public void DeleteProject(string id) => Write((connection, transaction) =>
    {
        RequireProject(connection, transaction, id);
        Execute(connection, transaction, "DELETE FROM Project WHERE id=$id;", ("$id", id));
    });

    public void AddToProject(string itemId, string projectId) => Write((connection, transaction) =>
    {
        RequireItem(connection, transaction, itemId);
        RequireProject(connection, transaction, projectId);
        Execute(connection, transaction, "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
            ("$project", projectId), ("$item", itemId));
        Touch(connection, transaction, itemId);
    });

    public void RemoveFromProject(string itemId, string projectId) => Write((connection, transaction) =>
    {
        RequireItem(connection, transaction, itemId);
        RequireProject(connection, transaction, projectId);
        Execute(connection, transaction, "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
            ("$project", projectId), ("$item", itemId));
        Touch(connection, transaction, itemId);
    });

    public void RemoveFromLibrary(string itemId) => RemoveItemsFromLibrary([itemId]);

    /// <summary>Remove all selected records in one transaction; never touch the physical resources.</summary>
    public void RemoveItemsFromLibrary(IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        Write((connection, transaction) =>
        {
            foreach (var id in ids) RequireItem(connection, transaction, id);
            foreach (var id in ids)
                Execute(connection, transaction, "DELETE FROM Item WHERE id=$id;", ("$id", id));
        });
    }

    public void RemoveItemsFromProject(IEnumerable<string> itemIds, string projectId)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var ids = itemIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;
        Write((connection, transaction) =>
        {
            RequireProject(connection, transaction, projectId);
            foreach (var id in ids) RequireItem(connection, transaction, id);
            foreach (var id in ids)
            {
                Execute(connection, transaction, "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
                    ("$project", projectId), ("$item", id));
                Touch(connection, transaction, id);
            }
        });
    }

    public void Open(string itemId)
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            ResourceItem item;
            using (var transaction = connection.BeginTransaction())
            {
                item = ResolveOpenTarget(connection, transaction, RequireItem(connection, transaction, itemId));
                // A verified path repair remains valid even if the external application cannot launch.
                transaction.Commit();
            }
            // Only successful Windows shell dispatch counts; application-level success cannot be observed here.
            platform.Open(item.Target, item.Type);
            using var historyTransaction = connection.BeginTransaction();
            Execute(connection, historyTransaction, "UPDATE Item SET last_opened_at=$now,open_count=open_count+1,updated_at=$now WHERE id=$id;",
                ("$now", Time(DateTimeOffset.UtcNow)), ("$id", itemId));
            historyTransaction.Commit();
        }
    }

    public void OpenLocation(string itemId)
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            var item = RequireItem(connection, null, itemId);
            if (item.IsUrl) throw new NotSupportedException("网址没有本地文件位置。");
            EnsureExists(item);
            platform.OpenLocation(item.Target, item.Type);
        }
    }

    public void RenamePhysical(string itemId, string newName)
    {
        ResourcePaths.ValidateName(newName);
        ChangeTarget(itemId, item =>
        {
            EnsureExists(item);
            var parent = Path.GetDirectoryName(item.Target);
            if (string.IsNullOrEmpty(parent)) throw new InvalidOperationException("不能重命名驱动器或共享根目录。");
            return ResourcePaths.Normalize(Path.Combine(parent, newName));
        }, movePhysical: true);
    }

    public void RepairPath(string itemId, string newPath)
    {
        var target = ResourcePaths.Normalize(newPath);
        ChangeTarget(itemId, item =>
        {
            if (!Exists(target, item.Type)) throw new IOException("新路径不存在、暂时无法访问，或资源类型与原记录不同。");
            return target;
        }, movePhysical: false);
    }

    private void ChangeTarget(string itemId, Func<ResourceItem, string> makeTarget, bool movePhysical)
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction();
            var snapshot = ReadSnapshot(connection, transaction);
            var item = snapshot.Items.FirstOrDefault(candidate => candidate.Id == itemId)
                       ?? throw new KeyNotFoundException("资源记录不存在。");
            if (item.Type is not ("file" or "folder")) throw new NotSupportedException("此类型暂不支持修改本地路径。");
            var target = makeTarget(item);
            if (string.Equals(item.Target, target, StringComparison.Ordinal))
            {
                if (!movePhysical && item.Type == "file")
                {
                    StoreIdentity(connection, transaction, item.Id, CaptureIdentity(target));
                    transaction.Commit();
                }
                return;
            }
            var samePath = string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase);
            if (movePhysical && !samePath && (platform.FileExists(target) || platform.DirectoryExists(target)))
                throw new IOException("目标名称已存在，不能覆盖其他文件或文件夹。");
            if (item.Type == "folder" && !samePath && ResourcePaths.IsDescendant(target, item.Target))
                throw new IOException("新路径不能位于原文件夹内部。");
            var changes = new Dictionary<string, string> { [item.Id] = target };
            if (item.Type == "folder")
                foreach (var child in snapshot.Items.Where(candidate => candidate.Type is "file" or "folder" &&
                             ResourcePaths.IsDescendant(candidate.Target, item.Target)))
                    changes[child.Id] = ResourcePaths.Rebase(child.Target, item.Target, target);
            var newKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var changedPath in changes.Values)
                if (!newKeys.Add(ResourcePaths.Key(changedPath))) throw new IOException("修改后存在重复的资源路径。");
            if (snapshot.Items.Any(candidate => candidate.Type is "file" or "folder" &&
                    !changes.ContainsKey(candidate.Id) && newKeys.Contains(ResourcePaths.Key(candidate.Target))))
                throw new IOException("新路径已由另一个资源记录使用，请先处理重复记录。");

            var moved = false;
            try
            {
                if (movePhysical)
                {
                    // A case-only folder rename needs an intermediate sibling on Windows.
                    if (samePath) MoveCaseOnly(item.Target, target, item.Type);
                    else platform.Move(item.Target, target, item.Type);
                    moved = true;
                }
                var now = Time(DateTimeOffset.UtcNow);
                // Release old uniqueness keys first: rebasing a folder onto an ancestor can
                // make one resource's new path equal another affected resource's old path.
                foreach (var id in changes.Keys)
                    Execute(connection, transaction, "UPDATE Item SET path_key=$key WHERE id=$id;",
                        ("$key", "pending:" + Guid.NewGuid().ToString("N")), ("$id", id));
                foreach (var (id, changedPath) in changes)
                {
                    Execute(connection, transaction, "UPDATE Item SET target=$target,path_key=$key,updated_at=$now WHERE id=$id;",
                        ("$target", changedPath), ("$key", ResourcePaths.Key(changedPath)), ("$now", now), ("$id", id));
                    var changedItem = snapshot.Items.First(candidate => candidate.Id == id);
                    if (changedItem.Type == "file")
                        StoreIdentity(connection, transaction, id,
                            CaptureIdentity(changedPath) ?? (movePhysical ? changedItem.FileIdentity : null));
                }
                transaction.Commit();
            }
            catch (Exception original)
            {
                if (moved)
                {
                    try
                    {
                        if (samePath) MoveCaseOnly(target, item.Target, item.Type);
                        else platform.Move(target, item.Target, item.Type);
                    }
                    catch (Exception recovery)
                    {
                        throw new IOException($"数据库更新失败，原名称也未能恢复。资源当前可能位于“{target}”，请用修复路径重新关联。", new AggregateException(original, recovery));
                    }
                }
                throw;
            }
        }
    }

    private void MoveCaseOnly(string oldPath, string newPath, string type)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(oldPath)!, ".resource-library-rename-" + Guid.NewGuid().ToString("N"));
        platform.Move(oldPath, temporaryPath, type);
        try { platform.Move(temporaryPath, newPath, type); }
        catch (Exception original)
        {
            try { platform.Move(temporaryPath, oldPath, type); }
            catch (Exception recovery)
            {
                throw new IOException($"重命名失败，恢复原名称失败。资源暂时位于“{temporaryPath}”，请用修复路径重新关联。", new AggregateException(original, recovery));
            }
            throw;
        }
    }

    private void Write(Action<SqliteConnection, SqliteTransaction> action, string? historyDescription = null,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction();
            var before = enableHistory ? ReadHistoryState(connection, transaction, HistoryIncludesItems(operation)) : null;
            PrepareHistory(before);
            action(connection, transaction);
            var after = enableHistory ? ReadHistoryState(connection, transaction, HistoryIncludesItems(operation)) : null;
            transaction.Commit();
            RecordHistory(before, after, historyDescription ?? HistoryDescription(operation));
        }
    }

    private bool Exists(string path, string type) => type switch
    {
        "file" => platform.FileExists(path),
        "folder" => platform.DirectoryExists(path),
        _ => true
    };

    private void EnsureExists(ResourceItem item)
    {
        if (!Exists(item.Target, item.Type)) throw new FileNotFoundException("资源已缺失或暂时无法访问，请先修复路径。", item.Target);
    }

    private ResourceItem ResolveOpenTarget(SqliteConnection connection, SqliteTransaction transaction, ResourceItem item) =>
        ResolveResourceTarget(connection, transaction, item, throwIfMissing: true);

    private ResourceItem ResolveResourceTarget(SqliteConnection connection, SqliteTransaction transaction,
        ResourceItem item, bool throwIfMissing)
    {
        if (item.IsUrl)
        {
            ResourceUrls.Normalize(item.Target);
            return item with { IsMissing = false };
        }
        if (Exists(item.Target, item.Type)) return BackfillIdentity(connection, transaction, item) with { IsMissing = false };
        if (item.Type == "file" && item.FileIdentity is { } identity)
        {
            var recoveredPath = ResolveIdentity(identity);
            if (recoveredPath != null && platform.FileExists(recoveredPath) && CaptureIdentity(recoveredPath) == identity)
            {
                var key = ResourcePaths.Key(recoveredPath);
                if (Scalar(connection, transaction, "SELECT id FROM Item WHERE path_key=$key AND id<>$id;",
                        ("$key", key), ("$id", item.Id)) is string)
                    throw new IOException("新路径已由另一个资源记录使用，请先处理重复记录。");
                // Physical identity locates the file; logical identity and context belong to the existing item.
                Execute(connection, transaction, "UPDATE Item SET target=$target,path_key=$key WHERE id=$id;",
                    ("$target", recoveredPath), ("$key", key), ("$id", item.Id));
                return item with { Target = recoveredPath, IsMissing = false };
            }
        }
        if (throwIfMissing) EnsureExists(item);
        return item with { IsMissing = true };
    }

    private ResourceItem BackfillIdentity(SqliteConnection connection, SqliteTransaction transaction, ResourceItem item)
    {
        if (item.Type != "file" || item.FileIdentity != null) return item;
        var identity = CaptureIdentity(item.Target);
        if (identity == null) return item;
        StoreIdentity(connection, transaction, item.Id, identity);
        return item with { FileIdentity = identity };
    }

    private static void StoreIdentity(SqliteConnection connection, SqliteTransaction transaction, string itemId, FileIdentity? identity) =>
        Execute(connection, transaction, "UPDATE Item SET volume_id=$volume,file_id=$file WHERE id=$id;",
            ("$volume", identity?.VolumeId), ("$file", identity?.FileId), ("$id", itemId));

    private FileIdentity? CaptureIdentity(string path)
    {
        try { return fileIdentityProvider.GetIdentity(path); }
        catch (Exception exception) when (IdentityUnavailable(exception)) { return null; }
    }

    private string? ResolveIdentity(FileIdentity identity)
    {
        try
        {
            var path = fileIdentityProvider.ResolvePath(identity);
            return path == null ? null : ResourcePaths.Normalize(path);
        }
        catch (Exception exception) when (IdentityUnavailable(exception)) { return null; }
    }

    private static bool IdentityUnavailable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.ComponentModel.Win32Exception;

    private static ResourceItem RequireItem(SqliteConnection connection, SqliteTransaction? transaction, string id)
        => ReadItem(connection, transaction, id) ?? throw new KeyNotFoundException("资源记录不存在。");

    private static void RequireProject(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        if (Scalar(connection, transaction, "SELECT id FROM Project WHERE id=$id;", ("$id", id)) is not string)
            throw new KeyNotFoundException("项目不存在。");
    }

    private static void Touch(SqliteConnection connection, SqliteTransaction transaction, string id) =>
        Execute(connection, transaction, "UPDATE Item SET updated_at=$now WHERE id=$id;", ("$id", id), ("$now", Time(DateTimeOffset.UtcNow)));

    private static string ValidateProjectName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("项目名称不能为空。", nameof(name));
        if (name.Length > 200) throw new ArgumentException("项目名称不能超过 200 个字符。", nameof(name));
        return name;
    }

    private static void EnsureProjectNameAvailable(SqliteConnection connection, SqliteTransaction transaction, string name, string? exceptId = null)
    {
        var id = Scalar(connection, transaction, "SELECT id FROM Project WHERE name_key=$key;", ("$key", name.ToUpperInvariant())) as string;
        if (id != null && id != exceptId) throw new InvalidOperationException("已存在同名项目。");
    }
}
