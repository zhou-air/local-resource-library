using LocalResourceLibrary.WinUI.Localization;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LocalResourceLibrary.ExplorerChecks;

internal static class Program
{
    private static int Main()
    {
        (string Name, Action Run)[] checks =
        [
            ("Project navigation preserves stored order and exposes pin state", ProjectNavigation),
            ("Groups separate pins, preserve project identity and remember collapse by group ID", GroupNavigation),
            ("Alias defaults to original name without creating unsaved changes", AliasDefault),
            ("System display language selects Chinese or English fallback", SystemLanguage),
            ("Details defaults to hidden and follows selection", Selection),
            ("Multiple selection hides individual editing and survives sorting and filtering", MultipleSelection),
            ("Deleted selections and projects leave no stale commands or drafts", RemovedSelection),
            ("Explorer natural ordering and folders first work in both directions", NaturalOrdering),
            ("File sizes sort numerically with unknown values last", SizeOrdering),
            ("Modification and open dates sort with unknown values last", DateOrdering),
            ("Recent filtering respects the selected global sort", RecentFiltering),
            ("Reordering and layout preserve row, icon, selection, and unsaved details", DraftPreservation),
            ("Local edit patches include only changed fields and membership deltas", EditPatch),
            ("External metadata reloads retain the original draft baseline until explicit discard", ExternalDraftBaseline),
            ("Explicit refresh selection sees externally added resources and projects", ExternalRefresh),
            ("Changed targets replace rows and discard the old target icon and metadata", TargetReplacement),
            ("File metadata caches across filters and snapshots and refreshes explicitly", MetadataCache),
            ("Unavailable metadata and folder sizes remain readable", UnavailableMetadata),
            ("URL rows use saved modification dates, URL labels and globe fallback", UrlMetadata),
            ("Mixed resources share modification, size and type sorting", MixedResourceSorting),
            ("URL metadata and memberships participate in search, recent and missing filters", UrlFiltering),
            ("URL drafts and mixed selection survive all eight view modes", UrlDraftAndViews),
            ("Stored favicon changes invalidate icons and pending revisions without replacing rows", FaviconReplacement),
            ("Explorer preferences persist independently of shared language settings", PreferencesRoundTrip),
            ("Corrupt, out-of-range, and unwritable preferences recover safely", InvalidPreferences)
        ];
        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception exception) { failures++; Console.WriteLine($"FAIL {name}: {exception}"); }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} Explorer model checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ProjectNavigation()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([], [new Project("z", "Z", "", true, 0), new Project("a", "A", "", true, 1), new Project("c", "C", "", false, 0), new Project("b", "B", "", false, 1)]));
        Equal("@all,@recent,@missing,@pinned,z,a,@unassigned,c,b", string.Join(",", vm.Navigation.Select(p => p.Id)));
        Equal(true, vm.Navigation[4].IsPinned);
        Equal(false, vm.Navigation[7].IsPinned);
        Equal(Visibility.Collapsed, vm.Navigation[0].PinVisibility);
        Equal(Visibility.Visible, vm.Navigation[4].PinVisibility);
        Equal(Visibility.Collapsed, vm.Navigation[7].PinVisibility);
    }

    private static void GroupNavigation()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        var first = new ProjectGroup("work", "Work", 0, DateTimeOffset.UtcNow);
        var second = new ProjectGroup("reference", "Reference", 1, DateTimeOffset.UtcNow);
        var pinned = new Project("pinned", "Pinned", "", true, 0, "blue", first.Id);
        var project = new Project("project", "Design", "", false, 0, "teal", first.Id);
        var ungrouped = new Project("loose", "Loose", "", Color: "purple");
        var item = Missing("source.txt") with { Projects = [project, pinned] };
        var snapshot = new LibrarySnapshot([item], [pinned, project, ungrouped], [first, second]);
        vm.Load(snapshot);
        Equal("@all,@recent,@missing,@pinned,pinned,@group:work,project,@group:reference,@unassigned,loose",
            string.Join(",", vm.Navigation.Select(entry => entry.Id)));
        Equal(1, vm.Navigation.Count(entry => entry.Id == pinned.Id));
        var entry = vm.Navigation.Single(entry => entry.Id == project.Id);
        Equal("teal", entry.Color);
        Equal("work", entry.GroupId);
        Equal(1, entry.Count);
        Equal(18d, entry.RowMargin.Left);
        Equal(Visibility.Collapsed, vm.Navigation.First(entry => entry.IsGroup).CountVisibility);
        vm.NavigationId = project.Id;
        vm.Filter();
        vm.Select(vm.Rows.Single());
        vm.Note = "unsaved draft";
        var row = vm.Selected!;
        foreach (var header in vm.Navigation.Where(entry => entry.IsHeader))
        {
            Equal(false, vm.TrySetNavigation(header));
            Equal(project.Id, vm.CurrentProjectId);
            Equal(true, vm.IsGroupExpanded(first.Id));
            Same(row, vm.Selected!);
            Equal("unsaved draft", vm.Note);
        }
        // Native keyboard traversal may pass a header and then navigate to the next project.
        var groupHeaderIndex = vm.Navigation.ToList().FindIndex(entry => entry.IsGroup && entry.GroupId == first.Id);
        vm.NavigationId = "@missing";
        Equal(false, vm.TrySetNavigation(vm.Navigation[groupHeaderIndex]));
        Equal("@missing", vm.NavigationId);
        Equal(true, vm.TrySetNavigation(vm.Navigation[groupHeaderIndex + 1]));
        Equal(project.Id, vm.CurrentProjectId);
        vm.SetGroupExpanded(first.Id, false);
        Equal(false, vm.Navigation.Any(entry => entry.Id == project.Id));
        Equal(project.Id, vm.CurrentProjectId);
        Equal("Design", vm.ViewTitle);
        Same(row, vm.Selected!);
        Equal("unsaved draft", vm.Note);
        Equal(true, vm.IsDirty);
        var reopened = new MainViewModel(new("en"), vm.DatabasePath);
        reopened.Load(snapshot with { ProjectGroups = [first with { Name = "Renamed group" }, second] });
        Equal(false, reopened.IsGroupExpanded(first.Id));
        Equal("Renamed group", reopened.Navigation.Single(entry => entry.GroupId == first.Id && entry.IsGroup).Name);
        vm.SetGroupExpanded(first.Id, true);
        Equal(true, vm.Navigation.Any(entry => entry.Id == project.Id));
        Equal(true, vm.IsDirty);
        // An external rename/move/color update keeps the current project's resource view and ID.
        var updated = project with { Name = "Renamed project", GroupId = second.Id, Color = "rose" };
        vm.Load(snapshot with { Projects = [pinned, updated, ungrouped] });
        Equal(project.Id, vm.CurrentProjectId);
        Equal("Renamed project", vm.ViewTitle);
        Equal(item.Id, vm.Rows.Single().Id);
        Equal("rose", vm.Navigation.Single(entry => entry.Id == project.Id).Color);
        Equal(true, vm.IsDirty);
    }

    private static void AliasDefault()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([Missing("manual.txt")], []));
        vm.Select(vm.Rows[0]);
        Equal(vm.Selected!.Item.RealName, vm.Alias);
        Equal(false, vm.IsDirty);
        Equal(vm.Selected.Item.Alias, vm.AliasForSave);
        vm.Description = "updated";
        Equal(vm.Selected.Item.Alias, vm.AliasForSave);
        vm.Alias = "custom.txt";
        Equal(true, vm.IsDirty);
        Equal("custom.txt", vm.AliasForSave);
    }

    private static void SystemLanguage()
    {
        foreach (var name in new[] { "zh-CN", "zh-TW", "zh-HK" })
            Equal("zh-CN", LocalResourceLibrary.WinUI.Services.SettingsStore.ResolveLanguage(new System.Globalization.CultureInfo(name)));
        foreach (var name in new[] { "en-US", "en-GB", "fr-FR", "ja-JP", "" })
            Equal("en", LocalResourceLibrary.WinUI.Services.SettingsStore.ResolveLanguage(new System.Globalization.CultureInfo(name)));
        Equal(LocalResourceLibrary.WinUI.Services.SettingsStore.ResolveLanguage(System.Globalization.CultureInfo.CurrentUICulture),
            LocalResourceLibrary.WinUI.Services.SettingsStore.GetSystemLanguage());
    }

    private static void Selection()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        Equal(ResourceViewMode.Details, vm.ViewMode);
        Equal(Visibility.Collapsed, vm.DetailsVisibility);
        vm.Load(new([Missing("readme2.md")], []));
        vm.Select(vm.Rows[0]);
        Equal(Visibility.Visible, vm.DetailsVisibility);
        vm.Select(null);
        Equal(Visibility.Collapsed, vm.DetailsVisibility);
    }

    private static void MultipleSelection()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([Missing("file2.txt"), Missing("file10.txt"), Missing("other.txt")], []));
        var first = vm.Rows[0];
        var second = vm.Rows[1];
        vm.SelectMany([first, second, first]);
        Equal(2, vm.SelectedRows.Count);
        Equal(true, vm.HasSelection);
        Equal(false, vm.HasSingleSelection);
        Equal(false, vm.IsDirty);
        Equal(Visibility.Collapsed, vm.DetailsVisibility);
        Equal(true, vm.CanDeleteResources);
        vm.IsBusy = true;
        Equal(false, vm.CanDeleteResources);
        vm.IsBusy = false;
        vm.SortDescending = true;
        vm.Reorder();
        Equal(2, vm.SelectedRows.Count);
        vm.ViewMode = ResourceViewMode.LargeIcons;
        vm.Query = "file";
        vm.Filter();
        Equal(2, vm.SelectedRows.Count);
        vm.Query = "file2";
        vm.Filter();
        Equal(1, vm.SelectedRows.Count);
        Same(first, vm.Selected!);
        vm.Alias = "unfinished";
        vm.SelectMany([first], preserveDraft: true);
        vm.Filter();
        Equal("unfinished", vm.Alias);
        Equal(true, vm.IsDirty);
        vm.Select(first); // Explicit discard resets the single-item draft.
        Equal(false, vm.IsDirty);
    }

    private static void RemovedSelection()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        var project = new Project("project", "Temporary", "");
        var first = Missing("first") with { Projects = [project] };
        var second = Missing("second") with { Projects = [project] };
        vm.NavigationId = project.Id;
        vm.Load(new([first, second], [project]));
        Equal(true, vm.CanDeleteProject);
        vm.SelectMany(vm.Rows);
        vm.Load(new([second], [project]));
        Equal(1, vm.SelectedRows.Count);
        Equal(second.Id, vm.Selected!.Id);
        vm.Load(new([], []));
        Equal("@all", vm.NavigationId);
        Equal(false, vm.HasSelection);
        Equal(false, vm.CanDeleteResources);
        Equal(false, vm.CanDeleteProject);
        Equal(false, vm.IsDirty);
        Equal("", vm.SelectionText);
        Equal(Visibility.Collapsed, vm.DetailsVisibility);
    }

    private static void NaturalOrdering()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([Missing("file10.txt"), Missing("file2.txt"), Missing("Folder20", "folder"), Missing("Folder3", "folder")], []));
        Order(vm, "Folder3", "Folder20", "file2.txt", "file10.txt");
        vm.SortDescending = true;
        vm.Reorder();
        Order(vm, "Folder20", "Folder3", "file10.txt", "file2.txt");
        vm.FoldersFirst = false;
        vm.Reorder();
        Order(vm, "Folder20", "Folder3", "file10.txt", "file2.txt");
        vm.SortDescending = false;
        vm.Reorder();
        Order(vm, "file2.txt", "file10.txt", "Folder3", "Folder20");
    }

    private static void SizeOrdering()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([fixture.File("a2", 2), fixture.File("z20", 20), fixture.File("x0", 0), Missing("unknown")], []));
        vm.SortKey = ResourceSortKey.Size;
        vm.Reorder();
        Order(vm, "x0", "a2", "z20", "unknown");
        vm.SortDescending = true;
        vm.Reorder();
        Order(vm, "z20", "a2", "x0", "unknown");
    }

    private static void DateOrdering()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        var old = fixture.File("zOld", 2);
        var recent = fixture.File("aRecent", 2);
        var unknown = Missing("unknown");
        System.IO.File.SetLastWriteTimeUtc(old.Target, new DateTime(2020, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        System.IO.File.SetLastWriteTimeUtc(recent.Target, new DateTime(2025, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        old = old with { LastOpenedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 0, TimeSpan.Zero) };
        recent = recent with { LastOpenedAt = new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero) };
        vm.Load(new([recent, unknown, old], []));
        foreach (var key in new[] { ResourceSortKey.Modified, ResourceSortKey.LastOpened })
        {
            vm.SortKey = key;
            vm.SortDescending = false;
            vm.Reorder();
            Order(vm, "zOld", "aRecent", "unknown");
            vm.SortDescending = true;
            vm.Reorder();
            Order(vm, "aRecent", "zOld", "unknown");
        }
    }

    private static void RecentFiltering()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        var old = Missing("file2") with { LastOpenedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = Missing("file10") with { LastOpenedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        vm.NavigationId = "@recent";
        vm.Load(new([Missing("never"), newer, old], []));
        Order(vm, "file2", "file10");
        vm.SortKey = ResourceSortKey.LastOpened;
        vm.SortDescending = true;
        vm.Reorder();
        Order(vm, "file10", "file2");
        vm.Query = "file2";
        vm.Filter();
        Order(vm, "file2");
    }

    private static void DraftPreservation()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([Missing("file2.txt"), Missing("file10.txt")], []));
        var selected = vm.Rows[0];
        var icon = new TestIcon();
        selected.Icon = icon;
        vm.Select(selected);
        vm.Alias = "unfinished edit";
        vm.SortDescending = true;
        Order(vm, "file2.txt", "file10.txt"); // A preference setter does not mutate the collection.
        vm.Reorder();
        Order(vm, "file10.txt", "file2.txt");
        Same(selected, vm.Selected!);
        Same(icon, selected.Icon!);
        Equal("unfinished edit", vm.Alias);
        Equal(true, vm.IsDirty);
        vm.ViewMode = ResourceViewMode.ExtraLargeIcons;
        Same(selected, vm.Selected!);
        Same(selected, vm.Rows[1]);
        vm.Filter();
        Same(selected, vm.Rows[1]);
        Same(icon, vm.Rows[1].Icon!);
        Equal("unfinished edit", vm.Alias);
    }

    private static void EditPatch()
    {
        using var fixture = new Fixture();
        var first = new Project("first", "First", "");
        var second = new Project("second", "Second", "");
        var item = Missing("draft.txt") with { Description = "original description", Note = "original note", Projects = [first] };
        var vm = fixture.Model;
        vm.Load(new([item], [first, second]));
        vm.Select(vm.Rows[0]);
        vm.Description = ""; // Explicitly clearing a field differs from leaving it unchanged.
        vm.Memberships.Single(choice => choice.Id == first.Id).IsSelected = false;
        vm.Memberships.Single(choice => choice.Id == second.Id).IsSelected = true;
        var edit = vm.CreateEditDraft()!;
        Same(item, edit.Expected);
        Equal<string?>(null, edit.Metadata.Alias);
        Equal("", edit.Metadata.Description);
        Equal<string?>(null, edit.Metadata.Note);
        Equal(second.Id, string.Join(",", edit.AddProjectIds));
        Equal(first.Id, string.Join(",", edit.RemoveProjectIds));
        Equal<string?>(null, edit.Url);
        var url = Url("https://example.test/original", "Saved alias");
        vm.Load(new([url], []));
        vm.Select(vm.Rows[0]);
        vm.Note = "changed note";
        edit = vm.CreateEditDraft(url.Target)!;
        Equal<string?>(null, edit.Url);
        Equal("changed note", edit.Metadata.Note);
        Equal("https://example.test/new", vm.CreateEditDraft("https://example.test/new")!.Url);
    }

    private static void ExternalDraftBaseline()
    {
        using var fixture = new Fixture();
        var first = new Project("first", "First", "");
        var second = new Project("second", "Second", "");
        var item = Missing("draft.txt") with { Alias = "original alias", Description = "original description", Projects = [first] };
        var vm = fixture.Model;
        vm.Load(new([item], [first, second]));
        vm.Select(vm.Rows[0]);
        vm.Note = "unfinished local note";
        var external = item with { Alias = "agent alias", Description = "agent description", Projects = [first, second] };
        vm.Load(new([external], [first, second]));
        Equal("agent alias", vm.Selected!.Name);
        Equal("original alias", vm.Alias);
        Equal("unfinished local note", vm.Note);
        Equal(true, vm.IsDirty);
        Same(item, vm.DraftBaseline!);
        var edit = vm.CreateEditDraft()!;
        Equal<string?>(null, edit.Metadata.Alias);
        Equal<string?>(null, edit.Metadata.Description);
        Equal("unfinished local note", edit.Metadata.Note);
        Equal(0, edit.AddProjectIds.Length);
        Equal(0, edit.RemoveProjectIds.Length);
        // Target replacement also keeps the draft and original conflict-check baseline.
        vm.Load(new([external with { Target = item.Target + ".moved" }], [first, second]));
        Equal("unfinished local note", vm.Note);
        Same(item, vm.DraftBaseline!);
        vm.Select(vm.Selected); // Explicit discard/reselection adopts current external metadata.
        Equal(false, vm.IsDirty);
        Equal("agent alias", vm.Alias);
        Equal("agent description", vm.Description);
        Equal(true, vm.Memberships.Single(choice => choice.Id == second.Id).IsSelected);
    }

    private static void ExternalRefresh()
    {
        using var fixture = new Fixture();
        var item = Missing("existing.txt");
        var project = new Project("agent-project", "Agent project", "Agent description");
        var vm = fixture.Model;
        vm.Load(new([item], []));
        vm.Select(vm.Rows[0]);
        var external = item with { Description = "Agent metadata", Projects = [project] };
        vm.Load(new([external, Url("https://example.test/new", "Agent website")], [project]));
        vm.Select(vm.Rows.Single(row => row.Id == item.Id));
        Equal(2, vm.Rows.Count);
        Equal("Agent metadata", vm.Description);
        Equal(true, vm.Memberships.Single(choice => choice.Id == project.Id).IsSelected);
        Equal(true, vm.Navigation.Any(entry => entry.Id == project.Id));
        Equal(false, vm.IsDirty);
    }

    private static void MetadataCache()
    {
        using var fixture = new Fixture();
        var item = fixture.File("cache.txt", 3);
        var snapshot = new LibrarySnapshot([item], []);
        var vm = fixture.Model;
        vm.Load(snapshot);
        var row = vm.Rows[0];
        Equal(3L, row.FileSize!.Value);
        System.IO.File.WriteAllBytes(item.Target, new byte[8]);
        vm.Filter();
        Equal(3L, row.FileSize!.Value);
        vm.Load(snapshot);
        Equal(3L, row.FileSize!.Value);
        Same(row, vm.Rows[0]);
        vm.InvalidateFileMetadata();
        vm.Filter();
        Equal(8L, row.FileSize!.Value);
        Equal(8L, new FileInfo(item.Target).Length);
    }

    private static void TargetReplacement()
    {
        using var fixture = new Fixture();
        var original = fixture.File("old.txt", 3);
        var replacement = fixture.File("new.pdf", 11) with { Id = original.Id };
        var vm = fixture.Model;
        vm.Load(new([original], []));
        var oldRow = vm.Rows[0];
        oldRow.Icon = new TestIcon();
        oldRow.SetShellType("Old text association");
        vm.Load(new([replacement], []));
        var newRow = vm.Rows[0];
        Equal(false, ReferenceEquals(oldRow, newRow));
        Equal(original.Id, newRow.Id);
        Equal<ImageSource?>(null, newRow.Icon);
        Equal("PDF 文档", newRow.Type);
        Equal(11L, newRow.FileSize!.Value);
        Equal(original.Target, oldRow.Target);
    }

    private static void UnavailableMetadata()
    {
        using var fixture = new Fixture();
        var vm = fixture.Model;
        vm.Load(new([Missing("missing.pdf"), Missing("missingFolder", "folder")], []));
        var folder = vm.Rows.Single(row => row.IsFolder);
        var file = vm.Rows.Single(row => !row.IsFolder);
        Equal("", folder.SizeText);
        Equal("—", file.SizeText);
        Equal("—", folder.ModifiedText);
        Equal("PDF 文档", file.Type);
        file.SetShellType("System PDF type");
        Equal("System PDF type", file.Type);
        file.SetShellType(null);
        Equal("PDF 文档", file.Type);
    }

    private static void UrlMetadata()
    {
        using var fixture = new Fixture();
        var updated = new DateTimeOffset(2024, 2, 3, 4, 5, 0, TimeSpan.Zero);
        var item = Url("https://example.test/path?q=complete#fragment", "") with { UpdatedAt = updated };
        var vm = fixture.Model;
        vm.Load(new([item], []));
        var row = vm.Rows[0];
        Equal(true, row.IsUrl);
        Equal(false, row.IsFolder);
        Equal("example.test", row.Name);
        Equal("网址", row.Type);
        Equal("\uE774", row.IconGlyph);
        Equal<DateTimeOffset?>(updated, row.FileModifiedAt);
        Equal<long?>(null, row.FileSize);
        Equal("", row.SizeText);
        Equal(false, row.IsMissing);
        Equal(Visibility.Visible, row.FallbackIconVisibility);
        row.SetShellType("Windows file type");
        Equal("网址", row.Type);
        vm.Select(row);
        Equal(item.Target, vm.UrlTarget);
        Equal(Visibility.Visible, vm.UrlVisibility);
        Equal(Visibility.Collapsed, vm.PhysicalVisibility);
        Equal(vm.Text["Url"], vm.TargetLabel);
        Equal(vm.Text["CopyUrl"], vm.CopyTargetLabel);
        Equal(false, vm.IsDirty);
        vm.Load(new([item with { UpdatedAt = updated.AddDays(1) }], []));
        Same(row, vm.Rows[0]);
        Equal<DateTimeOffset?>(updated.AddDays(1), row.FileModifiedAt);
    }

    private static void MixedResourceSorting()
    {
        using var fixture = new Fixture();
        var old = fixture.File("file.txt", 12);
        System.IO.File.SetLastWriteTimeUtc(old.Target, new DateTime(2020, 1, 2, 3, 4, 0, DateTimeKind.Utc));
        var url = Url("https://example.test/", "saved website") with
        {
            UpdatedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 0, TimeSpan.Zero)
        };
        var folder = Missing("folder", "folder");
        var missing = Missing("unknown.txt");
        var vm = fixture.Model;
        vm.Load(new([url, missing, folder, old], []));
        vm.SortKey = ResourceSortKey.Modified;
        vm.Reorder();
        Order(vm, "folder", "file.txt", "saved website", "unknown.txt");
        vm.SortDescending = true;
        vm.Reorder();
        Order(vm, "folder", "saved website", "file.txt", "unknown.txt");
        vm.SortKey = ResourceSortKey.Size;
        vm.Reorder();
        Order(vm, "folder", "file.txt", "saved website", "unknown.txt");
        vm.SortDescending = false;
        vm.Reorder();
        Order(vm, "folder", "file.txt", "saved website", "unknown.txt");
        vm.SortKey = ResourceSortKey.Type;
        vm.Reorder();
        Equal("folder", vm.Rows[0].Name);
        Equal(4, vm.Rows.Select(row => row.Id).Distinct().Count());
        Equal(1, vm.Rows.Count(row => row.IsUrl));
    }

    private static void UrlFiltering()
    {
        using var fixture = new Fixture();
        var shared = new Project("shared", "Engineering reference", "");
        var second = new Project("second", "Design project", "");
        var url = Url("https://example.test/catalog?material=steel#valve", "Vendor catalog") with
        {
            Description = "Reference description", Note = "Reviewed supplier", Projects = [shared, second],
            LastOpenedAt = DateTimeOffset.UtcNow.AddMinutes(-1), OpenCount = 3
        };
        var file = fixture.File("drawing.txt", 1) with { Projects = [shared] };
        var missing = Missing("unavailable.pdf");
        var vm = fixture.Model;
        vm.Load(new([url, file, missing], [shared, second]));
        foreach (var query in new[] { "steel", "Vendor", "description", "supplier", "Engineering Design" })
        {
            vm.Query = query;
            vm.Filter();
            Order(vm, "Vendor catalog");
        }
        vm.Query = "";
        vm.NavigationId = shared.Id;
        vm.Filter();
        Equal(2, vm.Rows.Count);
        vm.NavigationId = second.Id;
        vm.Filter();
        Order(vm, "Vendor catalog");
        vm.NavigationId = "@recent";
        vm.Filter();
        Order(vm, "Vendor catalog");
        vm.Select(vm.Rows[0]);
        Equal("3", vm.OpenCountText);
        Equal(false, string.IsNullOrWhiteSpace(vm.LastOpenedText));
        vm.NavigationId = "@missing";
        vm.Filter();
        Order(vm, "unavailable.pdf");
        Equal(false, vm.Rows.Any(row => row.IsUrl));
        Equal(1, vm.Navigation.Single(entry => entry.Id == "@missing").Count);
    }

    private static void UrlDraftAndViews()
    {
        using var fixture = new Fixture();
        var project = new Project("project", "Shared", "");
        var url = Url("https://example.test/original", "Website");
        var vm = fixture.Model;
        vm.Load(new([url, fixture.File("local.txt", 2), Missing("Folder", "folder")], [project]));
        var row = vm.Rows.Single(row => row.IsUrl);
        vm.Select(row);
        Equal(false, vm.IsDirty);
        vm.UrlTarget = "https://example.test/edited#section";
        Equal(true, vm.IsDirty);
        vm.Alias = "My reference";
        vm.Description = "manual details";
        vm.Note = "manual note";
        vm.Memberships[0].IsSelected = true;
        foreach (var mode in Enum.GetValues<ResourceViewMode>())
        {
            vm.ViewMode = mode;
            vm.SortDescending = !vm.SortDescending;
            vm.Reorder();
            vm.Filter();
            Same(row, vm.Selected!);
            Equal("https://example.test/edited#section", vm.UrlTarget);
            Equal("My reference", vm.Alias);
            Equal("manual details", vm.Description);
            Equal("manual note", vm.Note);
            Equal(true, vm.Memberships[0].IsSelected);
            Equal(true, vm.IsDirty);
            var reopened = new MainViewModel(new("zh-CN"), vm.DatabasePath);
            Equal(mode, reopened.ViewMode);
        }
        vm.Select(row);
        Equal(url.Target, vm.UrlTarget);
        Equal(false, vm.IsDirty);
        vm.SelectMany(vm.Rows);
        Equal(3, vm.SelectedRows.Count);
        Equal(Visibility.Collapsed, vm.UrlVisibility);
        Equal(Visibility.Collapsed, vm.PhysicalVisibility);
        Equal(false, vm.IsDirty);
        foreach (var mode in Enum.GetValues<ResourceViewMode>())
        {
            vm.ViewMode = mode;
            vm.Filter();
            Equal(3, vm.SelectedRows.Count);
        }
        vm.Select(vm.Rows.Single(row => !row.IsUrl && !row.IsFolder));
        Equal("", vm.UrlTarget);
        Equal(Visibility.Collapsed, vm.UrlVisibility);
        Equal(Visibility.Visible, vm.PhysicalVisibility);
        vm.UrlTarget = "ignored hidden field";
        Equal(false, vm.IsDirty);
        Equal(vm.Text["Path"], vm.TargetLabel);
        Equal(vm.Text["CopyPath"], vm.CopyTargetLabel);
    }

    private static void FaviconReplacement()
    {
        using var fixture = new Fixture();
        byte[] favicon = [137, 80, 78, 71, 13, 10, 26, 10, 1];
        var url = Url("https://example.test/", "Website") with { Favicon = favicon };
        var vm = fixture.Model;
        vm.Load(new([url], []));
        var row = vm.Rows[0];
        var icon = new TestIcon();
        row.Icon = icon;
        var revision = row.IconRevision;
        vm.Select(row);
        vm.Description = "manual edit";
        vm.Load(new([url with { Favicon = favicon.ToArray() }], []));
        Same(row, vm.Rows[0]);
        Same(icon, row.Icon!);
        Equal(revision, row.IconRevision);
        vm.Load(new([url with { Favicon = [137, 80, 78, 71, 13, 10, 26, 10, 2] }], []));
        Same(row, vm.Rows[0]);
        Equal(revision + 1, row.IconRevision);
        Equal<ImageSource?>(null, row.Icon);
        Equal(Visibility.Visible, row.FallbackIconVisibility);
        Equal("manual edit", vm.Description);
        Equal(true, vm.IsDirty);
        vm.Load(new([url with { Favicon = null }], []));
        Equal(revision + 2, row.IconRevision);
        var changed = url with { Target = "https://example.test/changed" };
        vm.Load(new([changed], []));
        Equal(false, ReferenceEquals(row, vm.Rows[0]));
        Equal<ImageSource?>(null, vm.Rows[0].Icon);
        Equal(url.Target, vm.UrlTarget); // An external target edit must not discard the local draft.
        Equal("manual edit", vm.Description);
        Equal(true, vm.IsDirty);
        vm.Select(vm.Selected);
        Equal(changed.Target, vm.UrlTarget);
    }

    private static void PreferencesRoundTrip()
    {
        using var fixture = new Fixture();
        var languagePath = Path.Combine(fixture.DirectoryPath, "settings.json");
        const string originalLanguage = "{\"language\":\"en\",\"future\":\"preserve\"}";
        System.IO.File.WriteAllText(languagePath, originalLanguage);
        var vm = fixture.Model;
        vm.NavigationWidth = 315;
        vm.DetailsWidth = 440;
        vm.ViewMode = ResourceViewMode.LargeIcons;
        vm.SortKey = ResourceSortKey.Type;
        vm.SortDescending = true;
        vm.FoldersFirst = false;
        var reopened = new MainViewModel(new("zh-CN"), vm.DatabasePath);
        Equal(315d, reopened.NavigationWidth);
        Equal(440d, reopened.DetailsWidth);
        Equal(ResourceViewMode.LargeIcons, reopened.ViewMode);
        Equal(ResourceSortKey.Type, reopened.SortKey);
        Equal(true, reopened.SortDescending);
        Equal(false, reopened.FoldersFirst);
        Equal(originalLanguage, System.IO.File.ReadAllText(languagePath));
        Equal(false, System.IO.File.Exists(vm.DatabasePath)); // Presentation settings never open or write a database.
    }

    private static void InvalidPreferences()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.DirectoryPath, "explorer-settings.json");
        System.IO.File.WriteAllText(path, "{broken json");
        var vm = new MainViewModel(new("zh-CN"), fixture.Model.DatabasePath);
        Equal(ResourceViewMode.Details, vm.ViewMode);
        System.IO.File.WriteAllText(path, "{\"ViewMode\":999,\"SortKey\":999,\"NavigationWidth\":-50,\"DetailsWidth\":5000,\"CollapsedGroupIds\":null}");
        vm = new MainViewModel(new("zh-CN"), fixture.Model.DatabasePath);
        Equal(ResourceViewMode.Details, vm.ViewMode);
        Equal(ResourceSortKey.Name, vm.SortKey);
        Equal(160d, vm.NavigationWidth);
        Equal(620d, vm.DetailsWidth);
        Equal(true, vm.IsGroupExpanded("new-group"));
        vm.NavigationWidth = double.NaN;
        vm.DetailsWidth = double.PositiveInfinity;
        Equal(226d, vm.NavigationWidth);
        Equal(340d, vm.DetailsWidth);
        System.IO.File.Delete(path);
        Directory.CreateDirectory(path);
        vm.SortDescending = true; // Failed persistence must not stop working UI preferences.
        Equal(true, vm.SortDescending);
        Equal(true, vm.Status.Contains("未能保存", StringComparison.Ordinal));
        Directory.Delete(path);
        vm.ViewMode = ResourceViewMode.LargeIcons;
        Equal(vm.Text["Ready"], vm.Status);
    }

    private static ResourceItem Missing(string name, string type = "file") =>
        new(Guid.NewGuid().ToString(), type, name, "", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0, true, []);
    private static ResourceItem Url(string target, string alias) =>
        new(Guid.NewGuid().ToString(), ResourceUrls.Type, target, alias, "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 0, false, []);
    private static void Order(MainViewModel vm, params string[] names) =>
        Equal(string.Join("|", names), string.Join("|", vm.Rows.Select(row => row.Name)));
    private static void Same(object expected, object actual) { if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException("Object identity changed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    private sealed class TestIcon : ImageSource { }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.ExplorerChecks", Guid.NewGuid().ToString("N"));
        public MainViewModel Model { get; }
        public Fixture() { Directory.CreateDirectory(DirectoryPath); Model = new(new Localizer("zh-CN"), Path.Combine(DirectoryPath, "library.db")); }
        public ResourceItem File(string name, int length)
        {
            var path = Path.Combine(DirectoryPath, name);
            System.IO.File.WriteAllBytes(path, new byte[length]);
            return Missing(path) with { IsMissing = false };
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.ExplorerChecks")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(DirectoryPath).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture directory.");
            Directory.Delete(DirectoryPath, true);
        }
    }
}
