using LocalResourceLibrary.Core;
using Microsoft.Data.Sqlite;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string Name, Func<Task> Check)[] checks =
        [
            ("Concurrent initialization serializes schema migration", ConcurrentInitialization),
            ("Concurrent version-three migration preserves existing resources", ConcurrentMigration),
            ("Three resource types deduplicate across independent services", ConcurrentResourceDeduplication),
            ("Concurrent metadata patches retain fields from all clients", ConcurrentMetadataPatches),
            ("Membership deltas preserve unrelated projects and roll back invalid changes", MembershipDeltas),
            ("Reviewed batch detects every stale resource before writing", BatchConflict),
            ("Database write failure rolls back the entire metadata batch", BatchWriteRollback),
            ("Query recovers file identity without opening or changing its context", QueryIdentityRecovery),
            ("UI edit saves only edited fields and rejects same-field conflicts", UiEditConflicts),
            ("Project patches preserve metadata, memberships and ordering", ProjectPatch)
        ];
        var failed = 0;
        foreach (var check in checks)
        {
            try
            {
                await check.Check();
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {check.Name}: {exception}");
            }
        }
        Console.WriteLine($"{checks.Length - failed}/{checks.Length} checks passed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task ConcurrentInitialization()
    {
        using var fixture = new Fixture();
        var services = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(fixture.Service)));
        Equal(4L, Convert.ToInt64(fixture.Scalar("PRAGMA user_version;")), "schema version");
        Equal("wal", fixture.Scalar("PRAGMA journal_mode;") as string, "concurrent-reader journal");
        foreach (var service in services) Equal(0, service.GetSnapshot(checkPaths: false).Items.Count, "empty new library");
    }

    private static async Task ConcurrentMigration()
    {
        using var fixture = new Fixture();
        fixture.Sql("""
            CREATE TABLE Item(id TEXT PRIMARY KEY,type TEXT NOT NULL,target TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,
                alias TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',note TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,updated_at TEXT NOT NULL,last_opened_at TEXT,open_count INTEGER NOT NULL DEFAULT 0,
                volume_id TEXT,file_id TEXT,favicon BLOB);
            CREATE TABLE Project(id TEXT PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,description TEXT NOT NULL DEFAULT '');
            CREATE TABLE ProjectItem(project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                item_id TEXT NOT NULL REFERENCES Item(id) ON DELETE CASCADE,PRIMARY KEY(project_id,item_id));
            INSERT INTO Item(id,type,target,path_key,alias,description,note,created_at,updated_at)
                VALUES('old-item','url','https://example.test/old','URL:https://example.test/old','Existing alias','Existing description','Existing note',
                    '2026-01-01T00:00:00.0000000+00:00','2026-01-02T00:00:00.0000000+00:00');
            INSERT INTO Project VALUES('old-project','Existing project','EXISTING PROJECT','Existing project description');
            INSERT INTO ProjectItem VALUES('old-project','old-item');
            PRAGMA user_version=3;
            """);
        var services = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(fixture.Service)));
        Equal(4L, Convert.ToInt64(fixture.Scalar("PRAGMA user_version;")), "migrated version");
        foreach (var service in services)
        {
            var item = service.GetResource("old-item");
            Equal("Existing alias", item.Alias, "alias survives migration");
            Equal("Existing description", item.Description, "description survives migration");
            Equal("Existing note", item.Note, "note survives migration");
            Equal("old-project", item.Projects.Single().Id, "membership survives migration");
        }
    }

    private static async Task ConcurrentResourceDeduplication()
    {
        using var fixture = new Fixture();
        var first = fixture.Service();
        var second = fixture.Service();
        var project = first.CreateProject("Concurrent collection");
        var file = fixture.File("source.txt", "original content");
        var folder = fixture.Folder("registered-folder");
        (string Type, string Target)[] targets = [("file", file), ("folder", folder), ("url", "https://example.test/resource")];
        var results = await Task.WhenAll(targets.SelectMany(target => Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).AddResource(target.Type, target.Target, $"Alias {index}", "Description", "Note", [project.Id, project.Id])))));
        Equal(3, results.Count(result => result.Added), "each type inserted exactly once");
        Equal(3, first.GetSnapshot().Items.Count, "unique shared resources");
        foreach (var target in targets)
        {
            var item = results.First(result => result.Item.Target == target.Target && result.Added).Item;
            var existing = second.AddResource(target.Type, target.Target, "Overwrite", "Overwrite", "Overwrite", [project.Id]);
            Require(!existing.Added, "repeated target must be reused");
            Equal(item.Id, existing.Item.Id, "permanent item identity");
            Equal(item.Alias, existing.Item.Alias, "dedup must retain alias");
            Equal("Description", existing.Item.Description, "dedup must retain description");
            Equal(1, existing.Item.Projects.Count, "unique membership");
        }
        Equal("original content", System.IO.File.ReadAllText(file), "add must leave content in place");
        Throws(() => second.AddResource("folder", file), "mismatched type rejected");
        Throws(() => second.AddResource("file", fixture.PathFor("missing.txt")), "missing paths rejected");
        Equal(3, first.GetSnapshot().Items.Count, "invalid additions cannot partially write");
    }

    private static async Task ConcurrentMetadataPatches()
    {
        using var fixture = new Fixture();
        var clients = Enumerable.Range(0, 3).Select(_ => fixture.Service()).ToArray();
        var item = clients[0].AddResource("url", "https://example.test/patch", "Initial", "Initial", "Initial").Item;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(client => Task.Run(() =>
        {
            for (var index = 0; index < 20; index++)
                clients[client].PatchResourceMetadata(client switch
                {
                    0 => new ResourceMetadataPatch(item.Id, Alias: $"Alias {index}"),
                    1 => new ResourceMetadataPatch(item.Id, Description: $"Description {index}"),
                    _ => new ResourceMetadataPatch(item.Id, Note: $"Note {index}")
                });
        })));
        var current = clients[0].GetResource(item.Id);
        Equal("Alias 19", current.Alias, "alias writer retained");
        Equal("Description 19", current.Description, "description writer retained");
        Equal("Note 19", current.Note, "note writer retained");
        clients[1].PatchResourceMetadata(new ResourceMetadataPatch(item.Id, Description: ""));
        current = clients[0].GetResource(item.Id);
        Equal("", current.Description, "empty string clears explicitly");
        Equal("Alias 19", current.Alias, "omitted alias untouched");
        Equal("Note 19", current.Note, "omitted note untouched");
    }

    private static async Task MembershipDeltas()
    {
        using var fixture = new Fixture();
        var first = fixture.Service();
        var second = fixture.Service();
        var projects = new[] { first.CreateProject("Alpha"), first.CreateProject("Beta"), first.CreateProject("Gamma") };
        var item = first.AddResource("url", "https://example.test/projects", projectIds: [projects[0].Id, projects[1].Id]).Item;
        await Task.WhenAll(
            Task.Run(() => first.ChangeResourceProjects(item.Id, addProjectIds: [projects[2].Id, projects[2].Id])),
            Task.Run(() => second.ChangeResourceProjects(item.Id, removeProjectIds: [projects[0].Id])));
        SetEqual([projects[1].Id, projects[2].Id], first.GetResource(item.Id).Projects.Select(project => project.Id), "unrelated membership retained");
        Throws(() => first.ChangeResourceProjects(item.Id, [projects[0].Id], ["missing-project"]), "invalid delta must roll back");
        SetEqual([projects[1].Id, projects[2].Id], first.GetResource(item.Id).Projects.Select(project => project.Id), "failed delta adds nothing");
        Throws(() => first.ChangeResourceProjects(item.Id, [projects[1].Id], [projects[1].Id]), "overlapping deltas rejected");
        Equal(1, first.GetSnapshot().Items.Count, "multi-project association uses one record");
    }

    private static Task BatchConflict()
    {
        using var fixture = new Fixture();
        var first = fixture.Service();
        var second = fixture.Service();
        var items = new[]
        {
            first.AddResource("file", fixture.File("batch.txt", "content"), description: "Before").Item,
            first.AddResource("folder", fixture.Folder("batch-folder"), description: "Before").Item,
            first.AddResource("url", "https://example.test/batch", description: "Before").Item
        };
        var patches = items.Select(item => new ResourceMetadataPatch(item.Id, Description: "Reviewed description")).ToArray();
        second.PatchResourceMetadata(new ResourceMetadataPatch(items[2].Id, Note: "Changed after preview"));
        var conflict = first.ApplyResourceMetadataBatch(patches, items);
        Require(!conflict.Committed, "stale batch must not commit");
        Equal(items[2].Id, conflict.Failures.Single().ItemId, "reports the changed resource");
        foreach (var item in items) Equal("Before", first.GetResource(item.Id).Description, "stale batch modifies no metadata");
        var approved = first.ApplyResourceMetadataBatch(patches, items.Select(item => first.GetResource(item.Id)));
        Require(approved.Committed && approved.Items.Count == 3, "fresh reviewed batch commits all resources");
        Equal("Changed after preview", first.GetResource(items[2].Id).Note, "batch keeps omitted field");
        var duplicate = first.ApplyResourceMetadataBatch([patches[0], patches[0]], approved.Items);
        Require(!duplicate.Committed, "duplicate patch IDs must fail atomically");
        var missing = first.ApplyResourceMetadataBatch([new ResourceMetadataPatch("missing", Note: "value")], []);
        Require(!missing.Committed && missing.Failures.Single().ItemId == "missing", "missing expected item reported");
        return Task.CompletedTask;
    }

    private static Task BatchWriteRollback()
    {
        using var fixture = new Fixture();
        var library = fixture.Service();
        var first = library.AddResource("url", "https://example.test/first", description: "Before first").Item;
        var second = library.AddResource("url", "https://example.test/second", description: "Before second").Item;
        fixture.Sql($"""
            CREATE TRIGGER reject_second BEFORE UPDATE OF description ON Item WHEN NEW.id='{second.Id}'
            BEGIN SELECT RAISE(ABORT,'simulated batch write failure'); END;
            """);
        var result = library.ApplyResourceMetadataBatch(
            [new ResourceMetadataPatch(first.Id, Description: "After first"), new ResourceMetadataPatch(second.Id, Description: "After second")], [first, second]);
        Require(!result.Committed && result.Failures.Count > 0, "failed SQL write reported");
        Equal("Before first", library.GetResource(first.Id).Description, "earlier write rolls back");
        Equal("Before second", library.GetResource(second.Id).Description, "failed write retains original");
        return Task.CompletedTask;
    }

    private static Task QueryIdentityRecovery()
    {
        using var fixture = new Fixture();
        var identities = new IdentityProvider();
        var library = fixture.Service(identities);
        var oldPath = fixture.File("old-name.txt", "unchanged");
        var identity = new FileIdentity("test-volume", "permanent-file-id");
        identities.ByPath[oldPath] = identity;
        var project = library.CreateProject("Recovery project");
        var before = library.AddResource("file", oldPath, "Saved alias", "Saved description", "Saved note", [project.Id]).Item;
        var newPath = fixture.PathFor("new-name.txt");
        System.IO.File.Move(oldPath, newPath);
        identities.ByPath[newPath] = identity;
        identities.Resolved[identity] = newPath;
        var stored = library.GetResource(before.Id, resolvePath: false);
        Require(stored.IsMissing && stored.Target == oldPath, "non-recovery query preserves saved target");
        var after = library.GetResource(before.Id);
        Equal(before.Id, after.Id, "recovery preserves item ID");
        Equal(newPath, after.Target, "query returns real recovered path");
        Require(!after.IsMissing, "recovered file available");
        Equal(before.Alias, after.Alias, "recovery preserves alias");
        Equal(before.Description, after.Description, "recovery preserves description");
        Equal(before.Note, after.Note, "recovery preserves note");
        Equal(before.UpdatedAt, after.UpdatedAt, "lookup does not mark resource as edited");
        Equal(0L, after.OpenCount, "query does not count as open");
        Equal(project.Id, after.Projects.Single().Id, "recovery preserves projects");
        Equal(0, fixture.Platform.OpenCalls, "query never invokes shell");
        System.IO.File.Delete(newPath);
        Require(library.GetResource(before.Id).IsMissing, "missing resource returns unavailable state");
        return Task.CompletedTask;
    }

    private static Task UiEditConflicts()
    {
        using var fixture = new Fixture();
        var ui = fixture.Service();
        var agent = fixture.Service();
        var firstProject = ui.CreateProject("Original project");
        var addedByAgent = ui.CreateProject("Agent project");
        var addedByUi = ui.CreateProject("UI project");
        var baseline = ui.AddResource("url", "https://example.test/ui", "Before", "Before description", "Before note", [firstProject.Id]).Item;
        agent.PatchResourceMetadata(new ResourceMetadataPatch(baseline.Id, Description: "Agent description"));
        agent.ChangeResourceProjects(baseline.Id, [addedByAgent.Id]);
        var saved = ui.ApplyResourceEdit(baseline, new ResourceMetadataPatch(baseline.Id, Alias: "UI alias"), [addedByUi.Id], []);
        Equal("Agent description", saved.Description, "UI omitted field preserves external edit");
        Equal("UI alias", saved.Alias, "UI edited field saved");
        SetEqual([firstProject.Id, addedByAgent.Id, addedByUi.Id], saved.Projects.Select(project => project.Id), "UI delta preserves external memberships");
        agent.PatchResourceMetadata(new ResourceMetadataPatch(saved.Id, Alias: "Agent alias"));
        Throws(() => ui.ApplyResourceEdit(saved, new ResourceMetadataPatch(saved.Id, Alias: "Conflicting UI alias"), [], [firstProject.Id]), "same-field conflict rejected");
        Equal("Agent alias", ui.GetResource(saved.Id).Alias, "conflict preserves external alias");
        SetEqual([firstProject.Id, addedByAgent.Id, addedByUi.Id], ui.GetResource(saved.Id).Projects.Select(project => project.Id), "conflict leaves memberships intact");
        var icon = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1 };
        var iconBaseline = ui.AddUrl("https://example.test/icon", "Icon", "", "", [], icon).Item;
        var changedUrl = ui.ApplyResourceEdit(iconBaseline, new ResourceMetadataPatch(iconBaseline.Id), [], [], "https://example.test/new-icon");
        Require(changedUrl.Favicon == null, "URL change drops previous site icon");
        return Task.CompletedTask;
    }

    private static Task ProjectPatch()
    {
        using var fixture = new Fixture();
        var ui = fixture.Service();
        var agent = fixture.Service();
        var project = ui.CreateProject("Original name", "Original description");
        ui.SetProjectPinned(project.Id, true);
        project = ui.GetSnapshot().Projects.Single();
        var item = ui.AddResource("url", "https://example.test/project-edit", projectIds: [project.Id]).Item;
        agent.PatchProject(project.Id, description: "Agent project description");
        var renamed = ui.PatchProject(project.Id, name: "UI renamed", expected: project);
        Equal("Agent project description", renamed.Description, "name-only patch preserves updated description");
        Equal(project.IsPinned, renamed.IsPinned, "patch keeps pinned state");
        Equal(project.SortOrder, renamed.SortOrder, "patch keeps order");
        Equal(project.Id, ui.GetResource(item.Id).Projects.Single().Id, "patch preserves membership");
        Throws(() => ui.PatchProject(project.Id, name: "Stale name", expected: project), "same project field conflict rejected");
        Equal("UI renamed", ui.GetSnapshot().Projects.Single().Name, "conflict preserves current project name");
        agent.PatchProject(project.Id, description: "");
        Equal("", ui.GetSnapshot().Projects.Single().Description, "empty description clears explicitly");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) => Require(EqualityComparer<T>.Default.Equals(expected, actual),
        $"{message}: expected '{expected}', got '{actual}'");

    private static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual, string message) =>
        Require(expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual), message);

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.CoreConcurrencyChecks", Guid.NewGuid().ToString("N"));
        public string DatabasePath { get; }
        public NoShellPlatform Platform { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "library.db");
        }

        public LibraryService Service() => new(DatabasePath, Platform);
        public LibraryService Service(IFileIdentityProvider identities) => new(DatabasePath, Platform, identities);
        public string PathFor(string name) => Path.Combine(root, name);
        public string File(string name, string contents)
        {
            var path = PathFor(name);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }
        public string Folder(string name)
        {
            var path = PathFor(name);
            Directory.CreateDirectory(path);
            return path;
        }
        public object? Scalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.CoreConcurrencyChecks"));
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean up outside this fixture's temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class NoShellPlatform : IResourcePlatform
    {
        public int OpenCalls { get; private set; }
        public bool FileExists(string path) => System.IO.File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void Open(string path, string type) { OpenCalls++; throw new InvalidOperationException("Unexpected shell launch."); }
        public void OpenLocation(string path, string type) => throw new InvalidOperationException("Unexpected shell location launch.");
        public void Move(string oldPath, string newPath, string type) => throw new InvalidOperationException("Unexpected physical mutation.");
    }

    private sealed class IdentityProvider : IFileIdentityProvider
    {
        public Dictionary<string, FileIdentity> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<FileIdentity, string> Resolved { get; } = [];
        public FileIdentity? GetIdentity(string path) => ByPath.GetValueOrDefault(path);
        public string? ResolvePath(FileIdentity identity) => Resolved.GetValueOrDefault(identity);
    }
}
