using Microsoft.Data.Sqlite;
using static LocalResourceLibrary.Core.LibraryRepository;

namespace LocalResourceLibrary.Core;

/// <summary>Library metadata and resource operations. Adding/removing membership never copies or deletes a resource.</summary>
public sealed class LibraryService
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

    public AddResourcesResult AddPaths(IEnumerable<string> paths, string? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (gate)
        {
            using var connection = repository.Connect();
            if (projectId != null) RequireProject(connection, null, projectId);
            var added = 0;
            var existing = 0;
            var errors = new List<string>();
            foreach (var rawPath in paths)
            {
                try
                {
                    var target = ResourcePaths.Normalize(rawPath);
                    var key = ResourcePaths.Key(target);
                    using var transaction = connection.BeginTransaction();
                    var itemId = Scalar(connection, transaction, "SELECT id FROM Item WHERE path_key=$key;", ("$key", key)) as string;
                    var isNew = itemId == null;
                    if (isNew)
                    {
                        var type = platform.DirectoryExists(target) ? "folder" : platform.FileExists(target) ? "file" : null;
                        if (type == null) throw new FileNotFoundException("路径不存在或暂时无法访问。", target);
                        itemId = Guid.NewGuid().ToString("N");
                        var now = Time(DateTimeOffset.UtcNow);
                        var identity = type == "file" ? CaptureIdentity(target) : null;
                        Execute(connection, transaction,
                            "INSERT INTO Item(id,type,target,path_key,created_at,updated_at,volume_id,file_id) VALUES($id,$type,$target,$key,$now,$now,$volume,$file);",
                            ("$id", itemId), ("$type", type), ("$target", target), ("$key", key), ("$now", now),
                            ("$volume", identity?.VolumeId), ("$file", identity?.FileId));
                    }
                    else BackfillIdentity(connection, transaction, RequireItem(connection, transaction, itemId!));
                    if (projectId != null)
                    {
                        RequireProject(connection, transaction, projectId);
                        var changed = Execute(connection, transaction, "INSERT OR IGNORE INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                            ("$project", projectId), ("$item", itemId));
                        if (changed > 0 && !isNew) Touch(connection, transaction, itemId!);
                    }
                    transaction.Commit();
                    if (isNew) added++; else existing++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  ArgumentException or NotSupportedException or SqliteException or InvalidOperationException)
                {
                    errors.Add($"{rawPath}: {exception.Message}");
                }
            }
            return new AddResourcesResult(added, existing, errors);
        }
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

    public Project CreateProject(string name, string description = "")
    {
        name = ValidateProjectName(name);
        var project = new Project(Guid.NewGuid().ToString("N"), name, description ?? "");
        Write((connection, transaction) =>
        {
            EnsureProjectNameAvailable(connection, transaction, name);
            Execute(connection, transaction, "INSERT INTO Project(id,name,name_key,description) VALUES($id,$name,$key,$description);",
                ("$id", project.Id), ("$name", name), ("$key", name.ToUpperInvariant()), ("$description", project.Description));
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
            if (snapshot.Items.Any(candidate => !changes.ContainsKey(candidate.Id) && newKeys.Contains(ResourcePaths.Key(candidate.Target))))
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

    private void Write(Action<SqliteConnection, SqliteTransaction> action)
    {
        lock (gate)
        {
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction();
            action(connection, transaction);
            transaction.Commit();
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

    private ResourceItem ResolveOpenTarget(SqliteConnection connection, SqliteTransaction transaction, ResourceItem item)
    {
        if (Exists(item.Target, item.Type)) return BackfillIdentity(connection, transaction, item);
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
                return item with { Target = recoveredPath };
            }
        }
        EnsureExists(item);
        return item;
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
    {
        using var command = Command(connection, transaction, "SELECT id,type,target,volume_id,file_id FROM Item WHERE id=$id;", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("资源记录不存在。");
        return new ResourceItem(reader.GetString(0), reader.GetString(1), reader.GetString(2), "", "", "", default, default, null, 0, false, [],
            reader.IsDBNull(3) || reader.IsDBNull(4) ? null : new FileIdentity(reader.GetString(3), reader.GetString(4)));
    }

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
