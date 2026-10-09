using Microsoft.Data.Sqlite;
using static LocalResourceLibrary.Core.LibraryRepository;

namespace LocalResourceLibrary.Core;

public sealed partial class LibraryService
{
    private const int UndoLimit = 100;
    private readonly List<HistoryFrame> undoHistory = [];
    private readonly List<HistoryFrame> redoHistory = [];
    private bool enableHistory;

    /// <summary>Opt in for a UI session. MCP/background service instances keep no independent history.</summary>
    public bool EnableHistory
    {
        get { lock (gate) return enableHistory; }
        set
        {
            lock (gate)
            {
                if (enableHistory == value) return;
                enableHistory = value;
                undoHistory.Clear();
                redoHistory.Clear();
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? HistoryChanged;

    public LibraryUndoState UndoState
    {
        get
        {
            lock (gate) return new LibraryUndoState(undoHistory.Count > 0, redoHistory.Count > 0,
                undoHistory.LastOrDefault()?.Description, redoHistory.LastOrDefault()?.Description);
        }
    }

    public LibraryHistoryResult Undo() => ApplyHistory(undo: true);
    public LibraryHistoryResult Redo() => ApplyHistory(undo: false);

    private LibraryHistoryResult ApplyHistory(bool undo)
    {
        lock (gate)
        {
            var source = undo ? undoHistory : redoHistory;
            var destination = undo ? redoHistory : undoHistory;
            if (!enableHistory || source.Count == 0) return new LibraryHistoryResult(false, null);
            var frame = source[^1];
            using var connection = repository.Connect();
            using var transaction = connection.BeginTransaction();
            var current = ReadHistoryState(connection, transaction,
                includeItems: frame.Changes.Any(change => change.Table.Name == "Item"));
            if (!HistoryMatches(frame, current, undo)) return HistoryConflict(frame.Description);
            Dictionary<string, long> revisions;
            try
            {
                RestoreHistory(connection, transaction, frame, undo);
                revisions = ReadReferenceRevisions(connection, transaction);
                transaction.Commit();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                // A new name/path or dependency may now occupy a restored row's unique key.
                // Never compensate by deleting or overwriting the external data.
                transaction.Rollback();
                return HistoryConflict(frame.Description);
            }
            AdvanceHistoryRevisions(frame.ReferenceRevisions.Keys, revisions);
            source.RemoveAt(source.Count - 1);
            destination.Add(frame);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return new LibraryHistoryResult(true, frame.Description);
        }
    }

    private LibraryHistoryResult HistoryConflict(string description)
    {
        // Removing stale history prevents repeated unsafe attempts and starts a fresh session boundary.
        undoHistory.Clear();
        redoHistory.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
        return new LibraryHistoryResult(false, description, Conflict: true);
    }

    private void RecordHistory(HistoryState? before, HistoryState? after, string description)
    {
        if (before == null || after == null) return;
        // The current service's writes legitimately advance shared relation tokens, including
        // legacy replacement APIs that delete/re-add an unchanged relationship.
        var revisedKeys = before.ReferenceRevisions.Keys.Union(after.ReferenceRevisions.Keys, StringComparer.Ordinal)
            .Where(key => before.ReferenceRevisions.GetValueOrDefault(key) != after.ReferenceRevisions.GetValueOrDefault(key));
        AdvanceHistoryRevisions(revisedKeys, after.ReferenceRevisions);
        var changes = new List<HistoryRowChange>();
        var dependencies = new List<HistoryDependency>();
        foreach (var table in HistoryTables)
        {
            var oldRows = before.Rows[table.Name];
            var newRows = after.Rows[table.Name];
            foreach (var key in oldRows.Keys.Union(newRows.Keys, StringComparer.Ordinal))
            {
                oldRows.TryGetValue(key, out var oldRow);
                newRows.TryGetValue(key, out var newRow);
                var columns = table.LogicalColumns.Where(index => oldRow == null || newRow == null ||
                    !HistoryValueEquals(oldRow[index], newRow[index])).ToArray();
                if (columns.Length == 0) continue;
                changes.Add(new HistoryRowChange(table, key, oldRow, newRow, columns));
                if (table.Name != "ProjectItem" && (oldRow == null || newRow == null))
                    dependencies.Add(new HistoryDependency(table.Name, key,
                        HistoryDependents(before, table.Name, key), HistoryDependents(after, table.Name, key)));
            }
        }
        if (changes.Count == 0) return;
        var revisions = changes.Where(change => change.Table.Name == "ProjectItem")
            .ToDictionary(change => change.Key, change => after.ReferenceRevisions.GetValueOrDefault(change.Key), StringComparer.Ordinal);
        undoHistory.Add(new HistoryFrame(description, changes, dependencies, revisions));
        if (undoHistory.Count > UndoLimit) undoHistory.RemoveAt(0);
        redoHistory.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HistoryMatches(HistoryFrame frame, HistoryState current, bool undo)
    {
        if (frame.ReferenceRevisions.Any(pair => current.ReferenceRevisions.GetValueOrDefault(pair.Key) != pair.Value))
            return false;
        foreach (var change in frame.Changes)
        {
            var expected = undo ? change.After : change.Before;
            current.Rows[change.Table.Name].TryGetValue(change.Key, out var actual);
            if ((expected == null) != (actual == null)) return false;
            if (expected != null && change.Columns.Any(index => !HistoryValueEquals(expected[index], actual![index])))
                return false;
        }
        foreach (var dependency in frame.Dependencies)
        {
            var expected = undo ? dependency.After : dependency.Before;
            if (!expected.SetEquals(HistoryDependents(current, dependency.Table, dependency.Key))) return false;
        }
        return true;
    }

    private void PrepareHistory(HistoryState? current)
    {
        if (current == null) return;
        if (undoHistory.Concat(redoHistory).Any(frame => frame.ReferenceRevisions.Any(pair =>
                current.ReferenceRevisions.GetValueOrDefault(pair.Key) != pair.Value)))
        {
            // A new user command starts from the external state. Do not refresh stale
            // frames' tokens and accidentally make an earlier command safe to replay.
            undoHistory.Clear();
            redoHistory.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AdvanceHistoryRevisions(IEnumerable<string> keys, IReadOnlyDictionary<string, long> current)
    {
        var changed = keys.ToHashSet(StringComparer.Ordinal);
        if (changed.Count == 0) return;
        foreach (var frame in undoHistory.Concat(redoHistory))
            foreach (var key in frame.ReferenceRevisions.Keys.Where(changed.Contains).ToArray())
                frame.ReferenceRevisions[key] = current.GetValueOrDefault(key);
    }

    private static HashSet<string> HistoryDependents(HistoryState state, string table, string id)
    {
        if (table == "ProjectGroup")
            return state.Rows["Project"].Where(pair => Equals(pair.Value[7], id))
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var index = table == "Project" ? 0 : 1;
        return state.Rows["ProjectItem"].Where(pair => Equals(pair.Value[index], id))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static void RestoreHistory(SqliteConnection connection, SqliteTransaction transaction,
        HistoryFrame frame, bool undo)
    {
        object?[]? Target(HistoryRowChange change) => undo ? change.Before : change.After;
        object?[]? Expected(HistoryRowChange change) => undo ? change.After : change.Before;
        // Release removed memberships before parent rows; restore parent rows before their children.
        foreach (var change in frame.Changes.Where(change => change.Table.Name == "ProjectItem" && Target(change) == null))
            DeleteHistoryRow(connection, transaction, change);
        foreach (var tableName in new[] { "ProjectGroup", "Project", "Item" })
        {
            foreach (var change in frame.Changes.Where(change => change.Table.Name == tableName && Target(change) != null))
            {
                var row = Target(change)!;
                var table = change.Table;
                if (Expected(change) == null)
                {
                    var names = string.Join(',', table.Columns);
                    var values = string.Join(',', table.Columns.Select((_, index) => "$v" + index));
                    Execute(connection, transaction, $"INSERT INTO {table.Name}({names}) VALUES({values});",
                        row.Select((value, index) => ("$v" + index, value)).ToArray());
                }
                else
                {
                    var updates = string.Join(',', change.Columns.Where(index => index > 0)
                        .Select(index => table.Columns[index] + "=$v" + index));
                    if (updates.Length == 0) continue;
                    Execute(connection, transaction, $"UPDATE {table.Name} SET {updates} WHERE id=$id;",
                        change.Columns.Select(index => ("$v" + index, row[index])).Append(("$id", row[0])).ToArray());
                }
            }
        }
        foreach (var tableName in new[] { "Item", "Project", "ProjectGroup" })
            foreach (var change in frame.Changes.Where(change => change.Table.Name == tableName && Target(change) == null))
                DeleteHistoryRow(connection, transaction, change);
        foreach (var change in frame.Changes.Where(change => change.Table.Name == "ProjectItem" && Target(change) != null))
        {
            var row = Target(change)!;
            Execute(connection, transaction, "INSERT INTO ProjectItem(project_id,item_id) VALUES($project,$item);",
                ("$project", row[0]), ("$item", row[1]));
        }
    }

    private static void DeleteHistoryRow(SqliteConnection connection, SqliteTransaction transaction, HistoryRowChange change)
    {
        var row = change.Before ?? change.After!;
        if (change.Table.Name == "ProjectItem")
            Execute(connection, transaction, "DELETE FROM ProjectItem WHERE project_id=$project AND item_id=$item;",
                ("$project", row[0]), ("$item", row[1]));
        else Execute(connection, transaction, $"DELETE FROM {change.Table.Name} WHERE id=$id;", ("$id", row[0]));
    }

    private static HistoryState ReadHistoryState(SqliteConnection connection, SqliteTransaction transaction,
        bool includeItems = true)
    {
        var rows = new Dictionary<string, Dictionary<string, object?[]>>(StringComparer.Ordinal);
        foreach (var table in HistoryTables)
        {
            var tableRows = new Dictionary<string, object?[]>(StringComparer.Ordinal);
            if (table.Name == "Item" && !includeItems)
            {
                rows.Add(table.Name, tableRows);
                continue;
            }
            using var command = Command(connection, transaction, $"SELECT {string.Join(',', table.Columns)} FROM {table.Name};");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, table.Columns.Length)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray();
                var key = table.Name == "ProjectItem" ? values[0] + "\u001f" + values[1] : (string)values[0]!;
                tableRows.Add(key, values);
            }
            rows.Add(table.Name, tableRows);
        }
        return new HistoryState(rows, ReadReferenceRevisions(connection, transaction));
    }

    private static Dictionary<string, long> ReadReferenceRevisions(SqliteConnection connection, SqliteTransaction transaction)
    {
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = Command(connection, transaction, "SELECT project_id,item_id,revision FROM ResourceReferenceRevision;");
        using var reader = command.ExecuteReader();
        while (reader.Read()) revisions.Add(reader.GetString(0) + "\u001f" + reader.GetString(1), reader.GetInt64(2));
        return revisions;
    }

    private static bool HistoryValueEquals(object? left, object? right) => left is byte[] bytes
        ? right is byte[] other && bytes.AsSpan().SequenceEqual(other) : Equals(left, right);

    private static bool HistoryIncludesItems(string operation) => operation is not (
        nameof(TransferResources) or nameof(ChangeResourceProjectsBatch) or nameof(ChangeResourceProjects) or
        nameof(AddToProject) or nameof(RemoveFromProject) or nameof(RemoveItemsFromProject) or
        nameof(CreateProject) or nameof(UpdateProject) or nameof(PatchProject) or nameof(DeleteProject) or
        nameof(SetProjectPinned) or nameof(MoveProject) or nameof(MoveProjectToGroup) or
        nameof(CreateProjectGroup) or nameof(PatchProjectGroup) or nameof(DeleteProjectGroup) or nameof(MoveProjectGroup));

    private static string HistoryDescription(string operation) => operation switch
    {
        nameof(CreateProject) => "CreateProject",
        nameof(DeleteProject) => "DeleteProject",
        nameof(UpdateProject) or nameof(PatchProject) => "EditProject",
        nameof(SetProjectPinned) or nameof(MoveProject) or nameof(MoveProjectToGroup) => "MoveProject",
        nameof(CreateProjectGroup) => "CreateProjectGroup",
        nameof(DeleteProjectGroup) => "DeleteProjectGroup",
        nameof(PatchProjectGroup) => "EditProjectGroup",
        nameof(MoveProjectGroup) => "MoveProjectGroup",
        nameof(RemoveItemsFromLibrary) => "DeleteResources",
        nameof(AddToProject) or nameof(RemoveFromProject) or nameof(RemoveItemsFromProject) or
            nameof(ChangeResourceProjects) => "ChangeResourceProjects",
        nameof(RegisterResource) or nameof(AddPaths) => "ImportResources",
        _ => "EditResource"
    };

    private static readonly HistoryTable[] HistoryTables =
    [
        new("ProjectGroup", ["id", "name", "name_key", "sort_order", "created_at"], [0, 1, 2, 3, 4]),
        new("Project", ["id", "name", "name_key", "description", "is_pinned", "sort_order", "color", "group_id"], [0, 1, 2, 3, 4, 5, 6, 7]),
        // Path-resolution identity and open statistics are passive state. They never become undo commands.
        new("Item", ["id", "type", "target", "path_key", "alias", "description", "note", "created_at", "updated_at",
            "last_opened_at", "open_count", "volume_id", "file_id", "favicon"], [0, 1, 2, 3, 4, 5, 6, 7, 13]),
        new("ProjectItem", ["project_id", "item_id"], [0, 1])
    ];

    private sealed record HistoryTable(string Name, string[] Columns, int[] LogicalColumns);
    private sealed record HistoryState(Dictionary<string, Dictionary<string, object?[]>> Rows,
        Dictionary<string, long> ReferenceRevisions);
    private sealed record HistoryRowChange(HistoryTable Table, string Key, object?[]? Before, object?[]? After, int[] Columns);
    private sealed record HistoryDependency(string Table, string Key, HashSet<string> Before, HashSet<string> After);
    private sealed record HistoryFrame(string Description, IReadOnlyList<HistoryRowChange> Changes,
        IReadOnlyList<HistoryDependency> Dependencies, Dictionary<string, long> ReferenceRevisions);
}
