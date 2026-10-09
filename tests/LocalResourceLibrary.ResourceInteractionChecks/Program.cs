using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.Services;
using Microsoft.Data.Sqlite;

namespace LocalResourceLibrary.ResourceInteractionChecks;

internal static class Program
{
    private static int assertions;

    private static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("Batch copy and move preserve IDs, unrelated memberships and originals", CopyMove),
            ("Copied stable IDs remain valid after source membership or project changes", CopyAfterSourceChanges),
            ("Membership batches validate all IDs and roll back database failures", MembershipRollback),
            ("Mixed imports reuse existing resources and preserve metadata", ImportReferences),
            ("Mixed imports roll back every record and membership on invalid targets", ImportRollback),
            ("Batch reference changes undo and redo as one action", BatchHistory),
            ("Imported records undo together while preexisting records remain", ImportHistory),
            ("Logical deletion restores the same resources and memberships", DeleteHistory),
            ("Project and group lifecycle and properties share one history", ProjectHistory),
            ("Conflicting external membership edits cannot be overwritten by undo", ExternalConflicts),
            ("External remove/re-add cannot bypass undo guards or revive old history", ExternalMembershipRevisions),
            ("Repeated UI changes to the same membership support multi-step undo and redo", RepeatedMembershipHistory),
            ("Undo database failures roll back the whole batch", UndoRollback),
            ("Unrelated external writes survive reference undo", UnrelatedExternalWrites),
            ("History is session-only, opt-in and bounded to 100 actions", HistoryLifetime),
            ("Clipboard text and reference payload parsing enforce absolute targets and stable IDs", ClipboardParsing)
        ];
        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try { run(); Console.WriteLine("PASS: " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL: {name}\n{error}"); }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks; {assertions} assertions. Isolated temporary libraries only; no native mouse or visual acceptance.");
        return failures == 0 ? 0 : 1;
    }

    private static void CopyMove()
    {
        using var f = new Fixture();
        var first = f.Service.AddResource("file", f.File, "Manual alias", "Manual description", "Manual note", [f.A.Id, f.C.Id]).Item;
        var second = f.Service.AddResource("folder", f.Folder, projectIds: [f.A.Id, f.C.Id]).Item;
        var ids = new[] { first.Id, second.Id };
        var copied = f.Service.TransferResources(ids, f.B.Id);
        Assert(copied.AddedMemberships == 2 && copied.RemovedMemberships == 0, "copy adds both target memberships");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.B.Id, f.C.Id]), "copy retains all source and unrelated memberships");
        Assert(f.Service.TransferResources(ids.Concat(ids), f.B.Id).AddedMemberships == 0, "duplicate items and existing memberships are idempotent");
        var moved = f.Service.TransferResources(ids, f.B.Id, f.A.Id, move: true);
        Assert(moved.AddedMemberships == 0 && moved.RemovedMemberships == 2, "move removes source even when destination already contains resource");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.B.Id, f.C.Id]), "move removes only named source");
        Assert(f.Service.TransferResources(ids, f.B.Id, f.B.Id, move: true).RemovedMemberships == 0, "same-project move is a no-op");
        f.Service.TransferResources(ids, f.A.Id, move: true);
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.B.Id, f.C.Id]), "All Resources cut imports to target without removing any membership");
        var after = f.Service.GetResource(first.Id, false);
        Assert(after.FileIdentity == first.FileIdentity && after.Target == first.Target && after.Alias == first.Alias && after.Note == first.Note, "reference changes preserve permanent ID, file identity and metadata");
        Assert(f.Service.GetSnapshot(false).Items.Count == 2, "transfers never duplicate Items");
        f.AssertOriginals();
    }

    private static void CopyAfterSourceChanges()
    {
        using (var f = new Fixture())
        {
            var items = f.Service.ImportReferences([f.File, f.Folder], f.A.Id).Items;
            var ids = items.Select(item => item.Id).ToArray();
            f.Service.ChangeResourceProjectsBatch(ids, [f.C.Id]);
            f.Service.RemoveItemsFromProject(ids, f.A.Id);
            var result = f.Service.TransferResources(ids, f.A.Id, f.A.Id, move: false);
            Assert(result.AddedMemberships == 2 && result.RemovedMemberships == 0, "copied references pasted back into former source restore removed target associations");
            foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.C.Id]), "copy back to source retains every unrelated project membership");
            result = f.Service.TransferResources(ids, f.A.Id, f.A.Id, move: true);
            Assert(result.AddedMemberships == 0 && result.RemovedMemberships == 0, "same-project move remains a no-op");
            foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.C.Id]), "same-project move cannot remove existing associations");
            f.AssertOriginals();
        }
        using (var f = new Fixture())
        {
            var items = f.Service.ImportReferences([f.File, f.Folder], f.A.Id).Items;
            var ids = items.Select(item => item.Id).ToArray();
            f.Service.ChangeResourceProjectsBatch(ids, [f.C.Id]);
            f.Service.DeleteProject(f.A.Id);
            var result = f.Service.TransferResources(ids, f.B.Id, f.A.Id, move: false);
            Assert(result.AddedMemberships == 2 && result.RemovedMemberships == 0, "copy ignores deleted contextual source and resolves resources through stable Item IDs");
            foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.B.Id, f.C.Id]), "copy after source project deletion keeps Items and unrelated memberships");
            Throws<KeyNotFoundException>(() => f.Service.TransferResources(ids, f.C.Id, f.A.Id, move: true), "move still rejects deleted source project atomically");
            foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.B.Id, f.C.Id]), "rejected stale-source move leaves every existing association intact");
            Assert(f.Service.GetSnapshot(false).Items.Count == 2 && f.Service.GetResource(items[0].Id, false).FileIdentity == items[0].FileIdentity, "copy after source deletion preserves permanent Item and File identities");
            f.AssertOriginals();
        }
    }

    private static void MembershipRollback()
    {
        using var f = new Fixture();
        var ids = f.Service.ImportReferences([f.File, f.Folder], f.A.Id).Items.Select(item => item.Id).ToArray();
        Throws<KeyNotFoundException>(() => f.Service.TransferResources([ids[0], "stale-item"], f.B.Id), "unknown Item ID rejects complete transfer");
        Throws<KeyNotFoundException>(() => f.Service.ChangeResourceProjectsBatch(ids, [f.B.Id, "stale-project"], [f.A.Id]), "unknown Project ID rejects all additions and removals");
        Throws<ArgumentException>(() => f.Service.ChangeResourceProjectsBatch(ids, [f.A.Id], [f.A.Id]), "overlapping added/removed projects rejected");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id]), "validation failure leaves every old membership intact");
        f.Sql($"CREATE TRIGGER fail_second_reference BEFORE INSERT ON ProjectItem WHEN NEW.project_id='{f.B.Id}' AND NEW.item_id='{ids[1]}' BEGIN SELECT RAISE(ABORT,'injected membership failure'); END;");
        Throws<SqliteException>(() => f.Service.TransferResources(ids, f.B.Id, f.A.Id, move: true), "second-item database failure aborts move");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id]), "database failure rolls back both earlier addition and source removal");
        f.AssertOriginals();
    }

    private static void ImportReferences()
    {
        using var f = new Fixture();
        var saved = f.Service.AddResource("file", f.File, "Saved alias", "Saved description", "Saved note", [f.A.Id]).Item;
        var result = f.Service.ImportReferences([f.File, f.Folder, Fixture.Url, f.File], f.B.Id);
        Assert(result.Added == 2 && result.Existing >= 1 && result.Items.Select(item => item.Id).Distinct().Count() == 3, "mixed paths and URLs create missing Items and deduplicate repeated input");
        var existing = f.Service.GetResource(saved.Id, false);
        Assert(existing.Alias == saved.Alias && existing.Description == saved.Description && existing.Note == saved.Note && existing.FileIdentity == saved.FileIdentity, "import preserves existing hand-edited context and identity");
        Assert(Memberships(f.Service, saved.Id).SetEquals([f.A.Id, f.B.Id]), "import adds target without replacing earlier membership");
        var repeated = f.Service.ImportReferences([f.File, f.Folder, Fixture.Url], f.B.Id);
        Assert(repeated.Added == 0 && repeated.AddedMemberships == 0 && f.Service.GetSnapshot(false).Items.Count == 3, "repeated paste creates neither Items nor associations");
        Assert(f.Service.GetSnapshot(false).Items.All(item => item.Target != f.Child), "folder paste never indexes child files");
        Assert(f.Service.ImportReferences([Fixture.Url.ToUpperInvariant()], f.B.Id).Added == 1, "URL import retains existing exact-spelling deduplication policy");
        f.AssertOriginals();
    }

    private static void ImportRollback()
    {
        using var f = new Fixture();
        var saved = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
        Throws<FileNotFoundException>(() => f.Service.ImportReferences([f.File, f.Folder, Fixture.Url, Path.Combine(f.Root, "missing.txt")], f.B.Id), "missing input rejects mixed batch");
        Assert(f.Service.GetSnapshot(false).Items.Count == 1 && Memberships(f.Service, saved.Id).SetEquals([f.A.Id]), "invalid import rolls back new Items and earlier existing-item membership");
        f.Sql("CREATE TRIGGER fail_url_import BEFORE INSERT ON Item WHEN NEW.type='url' BEGIN SELECT RAISE(ABORT,'injected import failure'); END;");
        Throws<SqliteException>(() => f.Service.ImportReferences([f.File, f.Folder, Fixture.Url], f.B.Id), "database failure rejects complete import");
        Assert(f.Service.GetSnapshot(false).Items.Count == 1 && Memberships(f.Service, saved.Id).SetEquals([f.A.Id]), "database import failure rolls back all prior writes");
        f.AssertOriginals();
    }

    private static void BatchHistory()
    {
        using var f = new Fixture();
        var ids = f.Service.ImportReferences([f.File, f.Folder, Fixture.Url], f.A.Id).Items.Select(item => item.Id).ToArray();
        f.Service.ChangeResourceProjectsBatch(ids, [f.C.Id]);
        f.Service.EnableHistory = true;
        f.Service.TransferResources(ids, f.B.Id, f.A.Id, move: true);
        Assert(f.Service.UndoState.CanUndo && !f.Service.UndoState.CanRedo, "complete move is one available undo action");
        Assert(f.Service.Undo().Applied && !f.Service.UndoState.CanUndo && f.Service.UndoState.CanRedo, "one undo consumes entire batch");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.C.Id]), "undo move restores original memberships");
        Assert(f.Service.Redo().Applied, "redo reapplies complete batch");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.B.Id, f.C.Id]), "redo move retains unrelated project");
        f.Service.Undo();
        f.Service.TransferResources(ids, f.C.Id);
        Assert(f.Service.UndoState.CanRedo, "idempotent transfer adds no history entry");
        f.Service.ChangeResourceProjectsBatch(ids, [f.B.Id]);
        Assert(!f.Service.UndoState.CanRedo, "new successful action clears old redo history");
        Assert(f.Service.Undo().Applied, "copy batch can be undone");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.C.Id]), "copy undo removes only newly added associations");
        f.AssertOriginals();
    }

    private static void ImportHistory()
    {
        using var f = new Fixture();
        var old = f.Service.AddResource("file", f.File, "Existing alias", projectIds: [f.A.Id]).Item;
        f.Service.EnableHistory = true;
        var pasted = f.Service.ImportReferences([f.File, f.Folder, Fixture.Url], f.B.Id).Items;
        var newIds = pasted.Where(item => item.Id != old.Id).Select(item => item.Id).Order().ToArray();
        Assert(f.Service.Undo().Applied, "paste is one undo unit");
        Assert(f.Service.GetSnapshot(false).Items.Single().Id == old.Id && Memberships(f.Service, old.Id).SetEquals([f.A.Id]), "undo paste removes only new Items and target memberships");
        Assert(f.Service.Redo().Applied, "paste can be redone");
        Assert(f.Service.GetSnapshot(false).Items.Where(item => item.Id != old.Id).Select(item => item.Id).Order().SequenceEqual(newIds), "redo paste reuses original allocated Item IDs");
        Assert(f.Service.GetResource(old.Id, false).Alias == "Existing alias", "redo does not replace existing metadata");
        f.AssertOriginals();
    }

    private static void DeleteHistory()
    {
        using var f = new Fixture();
        var resources = f.Service.ImportReferences([f.File, f.Folder, Fixture.Url], f.A.Id).Items;
        f.Service.ChangeResourceProjectsBatch(resources.Select(item => item.Id), [f.B.Id, f.C.Id]);
        var before = f.Service.GetSnapshot(false).Items.ToDictionary(item => item.Id);
        f.Service.EnableHistory = true;
        f.Service.RemoveItemsFromLibrary(resources.Select(item => item.Id));
        Assert(f.Service.GetSnapshot(false).Items.Count == 0 && f.Service.Undo().Applied, "batch logical deletion is reversible in one action");
        foreach (var item in f.Service.GetSnapshot(false).Items)
        {
            Assert(before.ContainsKey(item.Id) && item.FileIdentity == before[item.Id].FileIdentity && item.Target == before[item.Id].Target, "deleted Item and File identities restore unchanged");
            Assert(Memberships(f.Service, item.Id).SetEquals([f.A.Id, f.B.Id, f.C.Id]), "delete undo restores every original project membership");
        }
        Assert(f.Service.Redo().Applied && f.Service.GetSnapshot(false).Items.Count == 0, "redo deletes the same logical records only");
        f.AssertOriginals();
    }

    private static void ProjectHistory()
    {
        using var f = new Fixture();
        var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id, f.C.Id]).Item;
        f.Service.EnableHistory = true;
        var group = f.Service.CreateProjectGroup("Work");
        Assert(f.Service.Undo().Applied && !(f.Service.GetSnapshot(false).ProjectGroups ?? []).Any(), "new group creation can be undone");
        Assert(f.Service.Redo().Applied && f.Service.GetProjectGroup(group.Id).Id == group.Id, "group redo retains original Group ID");
        var project = f.Service.CreateProject("New project", "description", "blue", group.Id);
        Assert(f.Service.Undo().Applied && !f.Service.GetSnapshot(false).Projects.Any(value => value.Id == project.Id), "new project creation can be undone");
        Assert(f.Service.Redo().Applied && f.Service.GetProject(project.Id).Id == project.Id, "project redo retains original Project ID");
        f.Service.PatchProject(f.A.Id, name: "Renamed", color: "teal", groupId: group.Id);
        Assert(f.Service.Undo().Applied && f.Service.GetProject(f.A.Id) == f.A, "name, color and group property patch undo together");
        Assert(f.Service.Redo().Applied && f.Service.GetProject(f.A.Id).GroupId == group.Id, "project properties can be redone");
        f.Service.PatchProjectGroup(group.Id, "Renamed group");
        Assert(f.Service.Undo().Applied && f.Service.GetProjectGroup(group.Id).Name == "Work", "group rename undo preserves Group ID");
        f.Service.DeleteProjectGroup(group.Id);
        Assert(f.Service.GetProject(f.A.Id).GroupId == null && f.Service.GetProject(project.Id).GroupId == null, "group delete moves projects to ungrouped");
        Assert(f.Service.Undo().Applied && f.Service.GetProject(f.A.Id).GroupId == group.Id && f.Service.GetProject(project.Id).GroupId == group.Id, "group delete undo restores group and original grouping");
        f.Service.DeleteProject(f.A.Id);
        Assert(Memberships(f.Service, item.Id).SetEquals([f.C.Id]), "project delete removes only that project association");
        Assert(f.Service.Undo().Applied && f.Service.GetProject(f.A.Id).Id == f.A.Id && Memberships(f.Service, item.Id).SetEquals([f.A.Id, f.C.Id]), "project delete undo restores original ID and resource associations");
        f.AssertOriginals();
    }

    private static void ExternalConflicts()
    {
        using var f = new Fixture();
        var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
        f.Service.EnableHistory = true;
        f.Service.TransferResources([item.Id], f.B.Id);
        var peer = new LibraryService(f.Database);
        peer.RemoveFromProject(item.Id, f.B.Id);
        var result = f.Service.Undo();
        Assert(result.Conflict && !result.Applied && Memberships(f.Service, item.Id).SetEquals([f.A.Id]), "external change to same membership rejects stale undo");
        Assert(!f.Service.UndoState.CanUndo && !f.Service.UndoState.CanRedo, "conflict clears obsolete session history");
        var imported = f.Service.ImportReferences([f.Folder], f.B.Id).Items.Single();
        peer.AddToProject(imported.Id, f.C.Id);
        result = f.Service.Undo();
        Assert(result.Conflict && !result.Applied && Memberships(f.Service, imported.Id).SetEquals([f.B.Id, f.C.Id]), "undo import refuses to delete an Item referenced by new external membership");
        var importedUrl = f.Service.ImportReferences([Fixture.Url], f.B.Id).Items.Single();
        peer.PatchResourceMetadata(new ResourceMetadataPatch(importedUrl.Id, Note: "External note on imported item"));
        result = f.Service.Undo();
        Assert(result.Conflict && !result.Applied && f.Service.GetResource(importedUrl.Id, false).Note == "External note on imported item", "undo import refuses to delete new Item with externally modified metadata");
        f.Service.TransferResources([item.Id], f.B.Id);
        Assert(f.Service.Undo().Applied, "copy can undo before external redo conflict");
        peer.AddToProject(item.Id, f.B.Id);
        result = f.Service.Redo();
        Assert(result.Conflict && !result.Applied && Memberships(f.Service, item.Id).Contains(f.B.Id), "redo rejects external change to a membership it would restore");
        f.AssertOriginals();
    }

    private static void UndoRollback()
    {
        using var f = new Fixture();
        var ids = f.Service.ImportReferences([f.File, f.Folder], f.A.Id).Items.Select(item => item.Id).ToArray();
        f.Service.EnableHistory = true;
        f.Service.TransferResources(ids, f.B.Id);
        f.Sql($"CREATE TRIGGER fail_second_undo BEFORE DELETE ON ProjectItem WHEN OLD.project_id='{f.B.Id}' AND OLD.item_id='{ids[1]}' BEGIN SELECT RAISE(ABORT,'injected undo failure'); END;");
        var result = f.Service.Undo();
        Assert(!result.Applied, "undo fails safely when database rejects a later membership change");
        foreach (var id in ids) Assert(Memberships(f.Service, id).SetEquals([f.A.Id, f.B.Id]), "failed undo rolls back prior membership changes");
        f.AssertOriginals();
    }

    private static void ExternalMembershipRevisions()
    {
        using (var f = new Fixture())
        {
            var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
            f.Service.EnableHistory = true;
            f.Service.TransferResources([item.Id], f.B.Id);
            var peer = new LibraryService(f.Database);
            peer.RemoveFromProject(item.Id, f.B.Id);
            peer.AddToProject(item.Id, f.B.Id);
            var result = f.Service.Undo();
            Assert(result.Conflict && !result.Applied && Memberships(f.Service, item.Id).SetEquals([f.A.Id, f.B.Id]), "externally removed/re-added copy relation cannot be deleted by old undo");
            Assert(!f.Service.UndoState.CanUndo && !f.Service.UndoState.CanRedo, "remove/re-add conflict clears stale session stacks");
            f.AssertOriginals();
        }
        using (var f = new Fixture())
        {
            var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id, f.C.Id]).Item;
            f.Service.EnableHistory = true;
            f.Service.TransferResources([item.Id], f.B.Id, f.A.Id, move: true);
            var peer = new LibraryService(f.Database);
            peer.AddToProject(item.Id, f.A.Id);
            peer.RemoveFromProject(item.Id, f.A.Id);
            var result = f.Service.Undo();
            Assert(result.Conflict && !result.Applied && Memberships(f.Service, item.Id).SetEquals([f.B.Id, f.C.Id]), "external source toggle returning to absent cannot be overwritten by move undo");
            f.AssertOriginals();
        }
        using (var f = new Fixture())
        {
            var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
            f.Service.EnableHistory = true;
            f.Service.TransferResources([item.Id], f.B.Id);
            var peer = new LibraryService(f.Database);
            peer.RemoveFromProject(item.Id, f.B.Id);
            peer.AddToProject(item.Id, f.B.Id);
            f.Service.PatchProject(f.C.Id, color: "blue");
            Assert(f.Service.Undo().Applied && f.Service.GetProject(f.C.Id).Color == f.C.Color, "new user action after external edits has its own valid undo entry");
            Assert(!f.Service.UndoState.CanUndo && !f.Service.Undo().Applied, "new user action discards invalid older frames rather than rebaselining stale relations");
            Assert(Memberships(f.Service, item.Id).SetEquals([f.A.Id, f.B.Id]), "external replacement relation survives new user action and undo");
            f.AssertOriginals();
        }
        using (var f = new Fixture())
        {
            var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
            f.Service.EnableHistory = true;
            f.Service.TransferResources([item.Id], f.B.Id);
            var peer = new LibraryService(f.Database);
            peer.AddToProject(item.Id, f.B.Id);
            Assert(f.Service.Undo().Applied && Memberships(f.Service, item.Id).SetEquals([f.A.Id]), "idempotent external add does not invalidate unchanged membership history");
        }
    }

    private static void RepeatedMembershipHistory()
    {
        using var f = new Fixture();
        var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id, f.C.Id]).Item;
        f.Service.EnableHistory = true;
        f.Service.TransferResources([item.Id], f.B.Id);
        f.Service.RemoveItemsFromProject([item.Id], f.B.Id);
        f.Service.TransferResources([item.Id], f.B.Id);
        for (var index = 0; index < 3; index++)
        {
            Assert(f.Service.Undo().Applied, "owned repeated membership changes undo without false external conflict");
            var expected = index == 1 ? new[] { f.A.Id, f.B.Id, f.C.Id } : [f.A.Id, f.C.Id];
            Assert(Memberships(f.Service, item.Id).SetEquals(expected), "multi-step undo restores correct intermediate membership state");
        }
        Assert(!f.Service.UndoState.CanUndo && f.Service.UndoState.CanRedo, "all three owned operations undo as three complete units");
        for (var index = 0; index < 3; index++)
        {
            Assert(f.Service.Redo().Applied, "owned repeated membership changes redo without false external conflict");
            var expected = index == 1 ? new[] { f.A.Id, f.C.Id } : [f.A.Id, f.B.Id, f.C.Id];
            Assert(Memberships(f.Service, item.Id).SetEquals(expected), "multi-step redo restores correct intermediate membership state");
        }
        Assert(!f.Service.UndoState.CanRedo, "complete redo consumes every redo unit");
        Assert(f.Service.Undo().Applied, "last copy undo allows a new operation on the same relation");
        f.Service.TransferResources([item.Id], f.B.Id);
        Assert(!f.Service.UndoState.CanRedo, "new operation on repeated relation clears the old redo branch");
        for (var index = 0; index < 3; index++) Assert(f.Service.Undo().Applied, "older valid owned frames remain undoable after replacing redo branch");
        Assert(Memberships(f.Service, item.Id).SetEquals([f.A.Id, f.C.Id]), "repeated reference history never alters unrelated memberships");
        f.AssertOriginals();
    }

    private static void UnrelatedExternalWrites()
    {
        using var f = new Fixture();
        var item = f.Service.AddResource("file", f.File, projectIds: [f.A.Id]).Item;
        f.Service.EnableHistory = true;
        f.Service.TransferResources([item.Id], f.B.Id);
        var peer = new LibraryService(f.Database);
        peer.PatchResourceMetadata(new ResourceMetadataPatch(item.Id, Description: "External description"));
        peer.AddResource("url", Fixture.Url, projectIds: [f.C.Id]);
        Assert(f.Service.Undo().Applied, "unrelated columns and new resources do not prevent reference undo");
        Assert(f.Service.GetResource(item.Id, false).Description == "External description" && Memberships(f.Service, item.Id).SetEquals([f.A.Id]), "undo updates only its membership delta and preserves external metadata");
        Assert(f.Service.GetSnapshot(false).Items.Any(value => value.Target == Fixture.Url), "undo preserves unrelated external Items");
        Assert(f.Service.Redo().Applied && f.Service.GetResource(item.Id, false).Description == "External description", "redo also preserves external metadata");
        f.AssertOriginals();
    }

    private static void HistoryLifetime()
    {
        using var f = new Fixture();
        var item = f.Service.AddResource("file", f.File).Item;
        Assert(!f.Service.UndoState.CanUndo, "Core history is disabled by default for MCP peers");
        f.Service.EnableHistory = true;
        for (var step = 1; step <= 105; step++) f.Service.PatchResourceMetadata(new ResourceMetadataPatch(item.Id, Note: step.ToString()));
        var count = 0;
        while (f.Service.UndoState.CanUndo) { Assert(f.Service.Undo().Applied, "bounded history entry is applied"); count++; }
        Assert(count == 100 && f.Service.GetResource(item.Id, false).Note == "5", "oldest entries are trimmed at 100 actions");
        var reopened = new LibraryService(f.Database) { EnableHistory = true };
        Assert(!reopened.UndoState.CanUndo && !reopened.UndoState.CanRedo, "reopening does not restore session history");
        Assert(!reopened.Undo().Applied && !reopened.Redo().Applied, "empty undo and redo do not change database");
    }

    private static void ClipboardParsing()
    {
        using var f = new Fixture();
        var values = ResourceClipboard.ParseText($"\"{f.File}\"\r\n{f.Folder}\n{Fixture.Url}");
        Assert(values.SequenceEqual([f.File, f.Folder, Fixture.Url]), "clipboard supports quoted Unicode paths, multiple lines and URL");
        Throws<ArgumentException>(() => ResourceClipboard.ParseText("unrecognized prose"), "unknown clipboard text is rejected");
        Throws<ArgumentException>(() => ResourceClipboard.ParseText($"{f.File}\nrelative.txt"), "mixed valid/invalid text never partially imports");
        Throws<ArgumentException>(() => ResourceClipboard.ParseText("javascript:alert(1)"), "unsafe or unsupported URL rejected");
        Throws<ArgumentException>(() => ResourceClipboard.ParseText("file:///C:/private.txt"), "file URI is not silently treated as a saved web URL");
        var payload = new ClipboardReferencePayload(1, f.Database, ["stable-item-id"], f.A.Id, true, "session-token");
        var restored = ResourceClipboard.Decode(ResourceClipboard.Encode(payload), f.Database);
        Assert(restored.ItemIds.SequenceEqual(payload.ItemIds) && restored.SourceProjectId == f.A.Id && restored.IsCut && restored.Token == payload.Token, "internal payload round-trips stable IDs and source project");
        Throws<ArgumentException>(() => ResourceClipboard.Decode(ResourceClipboard.Encode(payload), Path.Combine(f.Root, "other.db")), "cross-library internal IDs cannot target another database");
        Throws<ArgumentException>(() => ResourceClipboard.Decode(ResourceClipboard.Encode(payload with { Version = 99 }), f.Database), "unsupported reference payload version rejected");
    }

    private static HashSet<string> Memberships(LibraryService service, string id) => service.GetResource(id, false).Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); assertions++; }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Url = "https://example.invalid/资源?source=clipboard#part";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary-InteractionChecks-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "library.db");
        public string File => Path.Combine(Root, "中文 resource.txt");
        public string Folder => Path.Combine(Root, "中文 folder");
        public string Child => Path.Combine(Folder, "unindexed-child.txt");
        public LibraryService Service { get; }
        public Project A { get; }
        public Project B { get; }
        public Project C { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Folder);
            System.IO.File.WriteAllText(File, "Original source content. 原文件保持原位置。");
            System.IO.File.WriteAllText(Child, "Unindexed folder child remains untouched.");
            Service = new LibraryService(Database, fileIdentityProvider: new IdentityProvider());
            A = Service.CreateProject("Source");
            B = Service.CreateProject("Target");
            C = Service.CreateProject("Unrelated");
        }

        public void Sql(string text)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }

        public void AssertOriginals()
        {
            Assert(System.IO.File.ReadAllText(File) == "Original source content. 原文件保持原位置。", "source file remains unchanged in place");
            Assert(System.IO.File.ReadAllText(Child) == "Unindexed folder child remains untouched.", "source folder and unindexed child remain unchanged in place");
            Assert(Directory.GetFiles(Root, "*.txt", SearchOption.AllDirectories).Length == 2, "reference operations create no physical file copies");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            var path = Path.GetFullPath(Root);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("LocalResourceLibrary-InteractionChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup path.");
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class IdentityProvider : IFileIdentityProvider
    {
        public FileIdentity? GetIdentity(string path) => new("test-volume", "stable-file-" + Path.GetFileName(path));
        public string? ResolvePath(FileIdentity identity) => null;
    }
}
