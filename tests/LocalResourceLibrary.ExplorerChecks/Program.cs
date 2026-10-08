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
            ("Changed targets replace rows and discard the old target icon and metadata", TargetReplacement),
            ("File metadata caches across filters and snapshots and refreshes explicitly", MetadataCache),
            ("Unavailable metadata and folder sizes remain readable", UnavailableMetadata),
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
        System.IO.File.WriteAllText(path, "{\"ViewMode\":999,\"SortKey\":999,\"NavigationWidth\":-50,\"DetailsWidth\":5000}");
        vm = new MainViewModel(new("zh-CN"), fixture.Model.DatabasePath);
        Equal(ResourceViewMode.Details, vm.ViewMode);
        Equal(ResourceSortKey.Name, vm.SortKey);
        Equal(160d, vm.NavigationWidth);
        Equal(620d, vm.DetailsWidth);
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
