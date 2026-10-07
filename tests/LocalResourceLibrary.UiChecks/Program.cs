using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalResourceLibrary.App;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.App.Services;
using LocalResourceLibrary.App.ViewModels;
using LocalResourceLibrary.Core;
using Microsoft.Data.Sqlite;

namespace LocalResourceLibrary.UiChecks;

internal static class Program
{
    private static int passed;

    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.UiChecks", Guid.NewGuid().ToString("N"));
        var artifacts = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "artifacts", "qa"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(artifacts);
        var listener = new BindingTraceListener();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        // A blocked modal dialog or failed dispatcher operation cannot leave a test process running indefinitely.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("FAIL  UI checks exceeded 90 seconds; terminating this test process only.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        LocalResourceLibrary.App.App? app = null;
        MainWindow? window = null;
        try
        {
            app = CreateIsolatedApplication();
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var settings = new SettingsStore(root);
            var text = new Localizer(settings.Language);
            var platform = new TestPlatform();
            var library = new LibraryService(Path.Combine(root, "library.db"), platform);
            window = new MainWindow(library, text, settings) { Left = 30, Top = 30, WindowStartupLocation = WindowStartupLocation.Manual };
            var vm = (MainViewModel)window.DataContext;
            window.Show();
            PumpUntil(() => window.IsLoaded && !vm.IsBusy, "initial window load");
            PumpFor(80);
            var grid = Named<DataGrid>(window, "ResourceGrid");
            var navigation = Named<ListBox>(window, "NavigationList");
            var search = Named<TextBox>(window, "SearchBox");
            var language = Named<ComboBox>(window, "LanguageBox");
            var alias = Named<TextBox>(window, "AliasBox");
            var description = Named<TextBox>(window, "DescriptionBox");
            var note = Named<TextBox>(window, "NoteBox");

            Check("Real Chinese WPF window starts with an empty isolated library", () =>
            {
                Require(window.Title == "本地资源库", "Default window title must be Chinese.");
                Require(grid.Items.Count == 0 && vm.EmptyVisibility == Visibility.Visible, "The empty-library state must be visible.");
                Require(vm.DatabasePath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "The UI must use only its isolated test database.");
                Capture(window, Path.Combine(artifacts, "ui-empty.png"));
            });

            Check("Language dropdown opens, renders its choices and commits popup selection", () =>
            {
                SelectPopupOption(language, 1, "English", Path.Combine(artifacts, "ui-dropdown-language.png"));
                Require(text.IsEnglish && window.Title == "Local Resource Library", "Choosing English from the actual popup must update the window.");
                Require(new SettingsStore(root).Language == "en", "A popup language choice must persist in isolated settings.");
                RequireVisibleText(language, "English", "The closed language selector must display the chosen language.");
                SelectPopupOption(language, 0, "简体中文");
                Require(!text.IsEnglish && window.Title == "本地资源库", "The popup must allow switching back to Chinese.");
                Require(new SettingsStore(root).Language == "zh-CN", "The restored Chinese selection must persist.");
                RequireVisibleText(language, "简体中文", "The closed language selector must show Chinese again.");
            });

            Check("Project modal validates its input, cancels safely and creates the typed project", () =>
            {
                var originalCount = library.GetSnapshot().Projects.Count;
                RunModal(window, text["NewProject"], () => { ClickButton(window, text["NewProject"]); return null; }, dialog =>
                {
                    var input = ByAutomationName<TextBox>(dialog, text["ProjectName"]);
                    var confirm = Descendants<Button>(dialog).Single(button => button.IsDefault);
                    Require(!confirm.IsEnabled, "An empty project name must disable confirmation.");
                    SetText(input, " \t ");
                    Require(!confirm.IsEnabled, "A whitespace-only project name must remain invalid.");
                    SetText(input, "不应创建的测试项目");
                    Require(confirm.IsEnabled, "A meaningful project name must enable confirmation.");
                    InvokeButton(Descendants<Button>(dialog).Single(button => button.IsCancel));
                });
                PumpFor(30);
                Require(!vm.IsBusy && library.GetSnapshot().Projects.Count == originalCount, "Cancel must not create or persist a project.");

                const string projectName = "共享工程资料校对";
                const string projectDescription = "保存 PDMS 与管道工程的共享参考资料。";
                RunModal(window, text["NewProject"], () => { ClickButton(window, text["NewProject"]); return null; }, dialog =>
                {
                    var input = ByAutomationName<TextBox>(dialog, text["ProjectName"]);
                    var descriptionInput = Descendants<TextBox>(dialog).Single(box => box != input);
                    SetText(input, projectName);
                    SetText(descriptionInput, projectDescription);
                    Capture(dialog, Path.Combine(artifacts, "ui-dialog-project.png"));
                    InvokeButton(Descendants<Button>(dialog).Single(button => button.IsDefault));
                });
                PumpUntil(() => !vm.IsBusy && vm.Snapshot.Projects.Count == originalCount + 1, "modal project creation");
                var created = library.GetSnapshot().Projects.Single(project => project.Name == projectName);
                Require(created.Description == projectDescription, "The modal description must persist independently of the project name.");
                Require(vm.NavigationId == created.Id, "Confirming a new project must navigate to the created project.");
                library.DeleteProject(created.Id);
                ClickButton(window, text["Refresh"]);
                PumpUntil(() => !vm.IsBusy && vm.Snapshot.Projects.Count == originalCount, "restoring isolated project setup");
            });

            Check("Input modal returns the exact typed name and multiline description", () =>
            {
                const string inputValue = "  中文资料索引  ";
                const string inputDescription = "保留第一行的中文说明。\n第二行记录 C:\\工程资料\\参考.txt";
                var result = RunModal(window, text["EditProject"],
                    () => InvokeDialog("Input", window, text, text["EditProject"], text["ProjectName"], "", null, "", "Save"), dialog =>
                    {
                        var input = ByAutomationName<TextBox>(dialog, text["ProjectName"]);
                        var descriptionInput = Descendants<TextBox>(dialog).Single(box => box != input);
                        SetText(input, inputValue);
                        SetText(descriptionInput, inputDescription);
                        InvokeButton(Descendants<Button>(dialog).Single(button => button.IsDefault));
                    });
                Require(result is ValueTuple<string, string> typed && typed.Item1 == inputValue && typed.Item2 == inputDescription,
                    "The generic input dialog must return both typed fields intact, leaving normalization to its caller.");
                Require(library.GetSnapshot().Projects.Count == 0, "Calling the input dialog itself must not persist a project.");
            });

            var firstProject = library.CreateProject("PDMS 学习", "PDMS 格式与工程参考");
            var secondProject = library.CreateProject("Piping Reference", "共享工程资料");
            ClickButton(window, text["Refresh"]);
            PumpUntil(() => !vm.IsBusy && vm.Snapshot.Projects.Count == 2, "project refresh");

            Check("Project chooser renders project names, selects from its popup and respects cancel", () =>
            {
                var projects = new[] { firstProject, secondProject };
                var chosen = RunModal(window, text["AddToProject"],
                    () => InvokeDialog("ChooseProject", window, text, text["AddToProject"], projects), dialog =>
                    {
                        var chooser = Descendants<ComboBox>(dialog).Single();
                        RequireVisibleText(chooser, firstProject.Name, "The default project label must be readable instead of the Project record representation.");
                        SelectPopupOption(chooser, 1, secondProject.Name, Path.Combine(artifacts, "ui-dropdown-project.png"));
                        RequireVisibleText(chooser, secondProject.Name, "The closed project selector must display the selected project's name.");
                        Capture(dialog, Path.Combine(artifacts, "ui-dialog-choose.png"));
                        InvokeButton(Descendants<Button>(dialog).Single(button => button.IsDefault));
                    });
                Require(ReferenceEquals(chosen, secondProject), "Confirm must return the original selected Project object, preserving its identity and description.");

                var canceled = RunModal(window, text["RemoveFromProject"],
                    () => InvokeDialog("ChooseProject", window, text, text["RemoveFromProject"], projects), dialog =>
                    {
                        SelectPopupOption(Descendants<ComboBox>(dialog).Single(), 0, firstProject.Name);
                        InvokeButton(Descendants<Button>(dialog).Single(button => button.IsCancel));
                    });
                Require(canceled == null, "Cancel must return no project even when an item is selected.");
                var empty = RunModal(window, text["ChooseProject"],
                    () => InvokeDialog("ChooseProject", window, text, text["ChooseProject"], Array.Empty<Project>()), dialog =>
                    {
                        Require(!Descendants<Button>(dialog).Single(button => button.IsDefault).IsEnabled, "A project chooser with no options must disable confirmation.");
                        InvokeButton(Descendants<Button>(dialog).Single(button => button.IsCancel));
                    });
                Require(empty == null && library.GetSnapshot().Projects.Count == 2, "Empty and canceled chooser dialogs must leave isolated projects unchanged.");
            });

            var source = Path.Combine(root, "示例资源");
            var mainParent = Path.Combine(source, "PDMS出口样本");
            Directory.CreateDirectory(mainParent);
            var mainPath = Path.Combine(mainParent, "QICHUANG-SITE-2026-08-26(2).txt");
            File.WriteAllText(mainPath, "Example export for isolated UI checks.");
            var manualPath = Path.Combine(source, "PDMS_参考笔记.md");
            File.WriteAllText(manualPath, "# PDMS reference\nIllustrative test resource.");
            var csvPath = Path.Combine(source, "设备层级清单.csv");
            File.WriteAllText(csvPath, "Name,Type\nP-101,Pump\n");
            var folderPath = Path.Combine(source, "ModelCreator示例项目");
            Directory.CreateDirectory(folderPath);
            var paths = new[] { mainPath, manualPath, csvPath, folderPath };

            Check("Routed multi-resource drop adds files and folders without copying", () =>
            {
                var data = new DataObject(DataFormats.FileDrop, paths);
                var over = DragArgs(data, window, DragDrop.PreviewDragOverEvent);
                window.RaiseEvent(over);
                Require(over.Effects == DragDropEffects.Link, "Dragging resources must advertise a reference/link operation.");
                alias.RaiseEvent(DragArgs(data, alias, DragDrop.PreviewDropEvent));
                PumpUntil(() => !vm.IsBusy && vm.Rows.Count == 4, "multi-resource drop");
                Require(grid.Items.Count == 4, "The bound DataGrid must show each resource.");
                Require(library.GetSnapshot().Items.Count(item => item.Type == "folder") == 1, "The dropped folder must remain a folder resource.");
                Require(Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length == 3, "Adding must not create copied physical files.");
            });

            var mainItem = library.GetSnapshot().Items.Single(item => item.Target == mainPath);
            Select(grid, vm, mainItem.Id);
            Check("Bound text fields and project checkboxes save metadata without renaming", () =>
            {
                SetText(alias, "PDMS TXT 导出示例");
                SetText(description, "真实 PDMS TXT 导出，用于检查格式兼容性与 ModelCreator 导入导出。");
                SetText(note, "稍后核对设备层级和管线命名。");
                SetMembership(window, firstProject.Id, true);
                SetMembership(window, secondProject.Id, true);
                PumpFor(40);
                Require(vm.IsDirty, "Editing bound details must mark a draft dirty.");
                var save = ByAutomationName<Button>(window, "Save details");
                Require(save.IsEnabled, "The Save button must enable for an edited draft.");
                Click(save);
                PumpUntil(() => !vm.IsDirty, "details save");
                var persisted = library.GetSnapshot().Items.Single(item => item.Id == mainItem.Id);
                Require(persisted.Alias == alias.Text && persisted.Description == description.Text && persisted.Note == note.Text, "Each bound text field must persist independently.");
                Require(persisted.Projects.Count == 2, "Both selected project memberships must persist.");
                Require(persisted.Target == mainPath && File.Exists(mainPath), "Alias edits must not change the physical target.");
                Require(grid.Items.Cast<ResourceRow>().Single(row => row.Id == mainItem.Id).Name == "PDMS TXT 导出示例", "The list must prefer the saved alias.");
                Capture(window, Path.Combine(artifacts, "ui-zh.png"));
            });

            Check("Sidebar project filtering and repeated drop reuse the existing item", () =>
            {
                navigation.SelectedItem = vm.Navigation.Single(entry => entry.Id == firstProject.Id);
                PumpFor(50);
                Require(grid.Items.Count == 1, "The project view must contain only its member.");
                alias.RaiseEvent(DragArgs(new DataObject(DataFormats.FileDrop, new[] { mainPath }), alias, DragDrop.PreviewDropEvent));
                PumpUntil(() => !vm.IsBusy, "duplicate project drop");
                Require(library.GetSnapshot().Items.Count == 4, "Re-dropping in a project must not duplicate the item.");
                navigation.SelectedItem = vm.Navigation.Single(entry => entry.Id == "@all");
                PumpFor(50);
                Require(grid.Items.Count == 4, "All resources must restore the entire list.");
            });

            Check("Debounced search reaches alias, original name, path, description, note and project", () =>
            {
                foreach (var query in new[] { "TXT 导出示例", "QICHUANG-SITE", "PDMS出口样本", "格式兼容性", "稍后核对", "PDMS 学习" })
                {
                    SetText(search, query);
                    PumpUntil(() => vm.Query == query, "search debounce");
                    Require(grid.Items.Count == 1 && ((ResourceRow)grid.Items[0]).Id == mainItem.Id, $"Search '{query}' must show the relevant row only.");
                }
                SetText(search, "does-not-exist-in-this-library");
                PumpUntil(() => vm.Query == search.Text, "no-results search");
                Require(grid.Items.Count == 0 && vm.EmptyVisibility == Visibility.Visible, "A query with no matches must display no-results state.");
                Click(ByAutomationName<Button>(window, "Clear search"));
                PumpUntil(() => vm.Query.Length == 0 && grid.Items.Count == 4, "clear search");
            });

            Select(grid, vm, mainItem.Id);
            Check("Language switch preserves all unsaved fields and persists English settings", () =>
            {
                SetText(alias, "PDMS TXT 导出示例（待复核）");
                SetText(description, description.Text + " 保留此编辑草稿。");
                SetText(note, note.Text + " 语言切换后继续。");
                SetMembership(window, secondProject.Id, false);
                var draftAlias = alias.Text;
                var draftDescription = description.Text;
                var draftNote = note.Text;
                language.SelectedIndex = 1;
                PumpFor(70);
                Require(text.IsEnglish && window.Title == "Local Resource Library", "Changing the language must update the actual bound title.");
                Require(alias.Text == draftAlias && description.Text == draftDescription && note.Text == draftNote, "Switching language must preserve each unsaved field.");
                Require(vm.IsDirty && !vm.Memberships.Single(membership => membership.Id == secondProject.Id).IsSelected, "Switching language must preserve unsaved membership changes.");
                Require(library.GetSnapshot().Items.Single(item => item.Id == mainItem.Id).Alias != draftAlias, "A language switch must not implicitly save the draft.");
                Require(new SettingsStore(root).Language == "en", "English preference must survive reading settings again.");
                Require((string)grid.Columns[0].Header == "Name / description", "DataGrid column headers must change language too.");
                Require((string)ByAutomationName<Button>(window, "Save details").Content == "Save", "The Save label must update through binding.");
            });

            Check("English context menu distinguishes library operations from physical rename", () =>
            {
                OpenResourceMenu(grid);
                var labels = grid.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
                foreach (var key in new[] { "Open", "OpenLocation", "CopyPath", "EditAlias", "EditDescription", "EditNote", "AddToProject", "RemoveFromProject", "Repair", "RemoveLibrary", "RenamePhysical", "LibraryOperations", "FileOperations" })
                    Require(labels.Contains(text[key]), $"English resource menu is missing '{text[key]}'.");
                Click(ByAutomationName<Button>(window, "Save details"));
                PumpUntil(() => !vm.IsDirty, "save preserved draft");
                SetMembership(window, secondProject.Id, true);
                Click(ByAutomationName<Button>(window, "Save details"));
                PumpUntil(() => !vm.IsDirty, "restore shared membership");
                Capture(window, Path.Combine(artifacts, "ui-en.png"));
            });

            Check("Routed double-click and Open location dispatch the selected real path", () =>
            {
                grid.ScrollIntoView(grid.SelectedItem);
                grid.UpdateLayout();
                var row = (DataGridRow?)grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem);
                Require(row != null, "The selected row must be realized for double-click input.");
                grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
                PumpUntil(() => !vm.IsBusy && platform.OpenCalls == 1, "resource double-click");
                Require(platform.LastOpenedPath == mainPath, "Double-click must dispatch the real path, not its alias.");
                Require(library.GetSnapshot().Items.Single(item => item.Id == mainItem.Id).OpenCount == 1, "The UI open action must update history.");
                OpenResourceMenu(grid);
                var location = grid.ContextMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "Open location");
                Click(location);
                PumpUntil(() => !vm.IsBusy && platform.LocationCalls == 1, "open location");
                Require(platform.LastLocationPath == mainPath, "Open location must receive the selected real path.");
                navigation.SelectedItem = vm.Navigation.Single(entry => entry.Id == "@recent");
                PumpFor(50);
                Require(grid.Items.Count == 1, "Recently opened must show the dispatched item.");
                navigation.SelectedItem = vm.Navigation.Single(entry => entry.Id == "@all");
                Select(grid, vm, mainItem.Id);
            });

            Check("Minimum-size main window keeps bound controls and renders successfully", () =>
            {
                window.Width = 1080;
                window.Height = 650;
                PumpFor(90);
                window.UpdateLayout();
                Require(window.ActualWidth >= 1080 && window.ActualHeight >= 650, "The window must honor its minimum size.");
                Require(grid.ActualWidth > 0 && alias.ActualWidth > 0, "Resource list and detail fields must retain nonzero layout width.");
                Capture(window, Path.Combine(artifacts, "ui-compact.png"));
                window.Width = 1420;
                window.Height = 860;
                PumpFor(60);
            });

            Check("Detail scrollbar thumb drag moves the content and returns to the top", () =>
            {
                var scroller = Ancestor<ScrollViewer>(alias);
                Require(scroller.ScrollableHeight > 0, "The selected detail panel must have scrollable content for this check.");
                scroller.ScrollToTop();
                PumpFor(30);
                var scrollbar = scroller.Template.FindName("PART_VerticalScrollBar", scroller) as ScrollBar;
                Require(scrollbar is { IsVisible: true }, "Scrollable details must expose their real vertical scrollbar.");
                scrollbar!.ApplyTemplate();
                scrollbar.UpdateLayout();
                var track = scrollbar.Template.FindName("PART_Track", scrollbar) as Track;
                Require(track?.Thumb is { IsVisible: true, ActualHeight: > 0 }, "The custom scrollbar must realize an operable thumb.");
                var thumb = track!.Thumb;
                DragThumb(thumb, 55);
                PumpUntil(() => scroller.VerticalOffset > 0, "detail thumb drag scrolling");
                var moved = scroller.VerticalOffset;
                Require(moved <= scroller.ScrollableHeight, "Dragging must remain within the detail content's scroll range.");
                DragThumb(thumb, -1000);
                PumpUntil(() => scroller.VerticalOffset < 0.1, "detail thumb drag reset");
                Require(alias.Text == vm.Alias && !vm.IsDirty, "Moving the scrollbar must preserve the saved detail fields.");
            });

            Check("Refresh marks a missing path while retaining context and project membership", () =>
            {
                File.Delete(mainPath);
                ClickButton(window, text["Refresh"]);
                PumpUntil(() => !vm.IsBusy && vm.Snapshot.Items.Single(item => item.Id == mainItem.Id).IsMissing, "missing-path refresh");
                Select(grid, vm, mainItem.Id);
                Require(vm.Selected!.Status == "Missing" && vm.MissingVisibility == Visibility.Visible, "The selected resource must show missing status and repair help.");
                Require(alias.Text == "PDMS TXT 导出示例（待复核）" && vm.Memberships.Count(choice => choice.IsSelected) == 2, "Missing status must preserve the saved context and memberships.");
                Require(vm.Selected.Target == mainPath, "The missing resource must keep its original path.");
                navigation.SelectedItem = vm.Navigation.Single(entry => entry.Id == "@missing");
                PumpFor(50);
                Require(grid.Items.Count == 1, "The missing view must show only the missing item.");
                Select(grid, vm, mainItem.Id);
                Capture(window, Path.Combine(artifacts, "ui-missing.png"));
            });

            Check("Malformed settings safely fall back to Chinese", () =>
            {
                var settingsCases = Path.Combine(root, "settings-cases");
                Directory.CreateDirectory(settingsCases);
                foreach (var malformed in new[] { "[]", "{\"language\":123}", "null", "{broken-json" })
                {
                    File.WriteAllText(Path.Combine(settingsCases, "settings.json"), malformed);
                    Require(new SettingsStore(settingsCases).Language == "zh-CN", $"Malformed settings '{malformed}' must use the default language.");
                }
                File.WriteAllText(Path.Combine(settingsCases, "settings.json"), "{\"language\":\"en\"}");
                Require(new SettingsStore(settingsCases).Language == "en", "A valid saved language must still load.");
            });

            Check("English error translation preserves Chinese paths and recovery locations", () =>
            {
                var english = new Localizer("en");
                var chinese = new Localizer("zh-CN");
                const string rawPath = @"C:\临时资料\路径不存在或暂时无法访问。\测试文件.txt";
                var prefixed = rawPath + ": 路径不存在或暂时无法访问。";
                Require(ErrorText.FormatMessage(prefixed, english) == rawPath + ": The path does not exist or is temporarily unavailable.", "An add error must translate its message without modifying a Chinese path.");
                var recovery = new IOException("数据库更新失败，原名称也未能恢复。资源当前可能位于“" + rawPath + "”，请用修复路径重新关联。");
                var translated = ErrorText.Format(recovery, english);
                Require(translated.StartsWith("The database update failed", StringComparison.Ordinal) && translated.Contains(rawPath, StringComparison.Ordinal) && translated.EndsWith("Use Repair path to reconnect it.", StringComparison.Ordinal), "Recovery translation must preserve the exact recovery location.");
                Require(ErrorText.Format(recovery, chinese) == recovery.Message && ErrorText.FormatMessage(prefixed, chinese) == prefixed, "Chinese mode must preserve original application errors.");
                Require(ErrorText.Format(new IOException("Native OS diagnostic"), english) == "Native OS diagnostic", "Unknown OS diagnostic text must remain intact.");
            });

            Check("WPF reports no binding errors or warnings", () =>
            {
                PumpFor(60);
                Require(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
            });

            File.WriteAllText(Path.Combine(artifacts, "ui-checks.txt"), $"{passed} UI smoke scenarios passed.\nGenerated at {DateTimeOffset.UtcNow:O}.\nReal WPF controls, bindings and routed events; isolated test database/settings/files.\nShell dispatch was mocked. File/folder dialogs, OS drag gesture, actual default applications and manual visual review are outside this automated coverage.\nNo WPF binding errors or warnings were reported.\n", Encoding.UTF8);
            var previousFailure = Path.Combine(artifacts, "ui-checks-failure.txt");
            if (File.Exists(previousFailure)) File.Delete(previousFailure);
            Console.WriteLine($"\n{passed}/{passed} UI smoke scenarios passed.\nArtifacts: {artifacts}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {passed} passing scenarios\n{exception}");
            File.WriteAllText(Path.Combine(artifacts, "ui-checks-failure.txt"), exception + "\n" + string.Join("\n", listener.Messages));
            return 1;
        }
        finally
        {
            if (window?.DataContext is MainViewModel vm)
            {
                PumpUntil(() => !vm.IsBusy, "pending operation before closing");
                vm.Select(vm.Selected); // Discard only the isolated test draft before closing; do not show a modal save prompt.
                window.Close();
            }
            app?.Shutdown();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            SqliteConnection.ClearAllPools();
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalResourceLibrary.UiChecks"));
            var resolved = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the unique UI test directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Check(string name, Action action)
    {
        action();
        passed++;
        Console.WriteLine($"PASS  {name}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T Named<T>(Window window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"Missing named control {name}.");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T ByAutomationName<T>(Window window, string name) where T : FrameworkElement =>
        Descendants<T>(window).Single(element => AutomationProperties.GetName(element) == name);

    private static void SetText(TextBox box, string value)
    {
        box.SetCurrentValue(TextBox.TextProperty, value);
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        PumpFor(10);
    }

    private static void SetMembership(Window window, string projectId, bool selected)
    {
        var checkbox = Descendants<CheckBox>(window).Single(box => box.DataContext is MembershipChoice choice && choice.Id == projectId);
        checkbox.SetCurrentValue(ToggleButton.IsCheckedProperty, selected);
        checkbox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        PumpFor(10);
    }

    private static void Select(DataGrid grid, MainViewModel vm, string id)
    {
        grid.SelectedItem = vm.Rows.Single(row => row.Id == id);
        PumpFor(30);
        Require(vm.Selected?.Id == id, "Selection must propagate from the DataGrid into the details view.");
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
    private static void ClickButton(Window window, string label) => Click(Descendants<Button>(window).Single(button => button.Content is string text && text == label));

    private static void InvokeButton(Button button)
    {
        Require(button.IsEnabled, "A test must not invoke a disabled button.");
        var provider = new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) as IInvokeProvider;
        Require(provider != null, "The real Button must expose its invoke action.");
        provider!.Invoke();
        PumpFor(30); // Automation queues the real OnClick; this also exercises IsCancel behavior.
    }

    private static T Ancestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        throw new InvalidOperationException($"Missing {typeof(T).Name} ancestor for the tested control.");
    }

    private static void DragThumb(Thumb thumb, double verticalDistance)
    {
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(0, verticalDistance) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(0, verticalDistance, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static void SelectPopupOption(ComboBox combo, int index, string expectedLabel, string? capturePath = null)
    {
        combo.ApplyTemplate();
        combo.UpdateLayout();
        var toggle = Descendants<ToggleButton>(combo).Single();
        var provider = new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle) as IToggleProvider;
        Require(provider != null, "The dropdown button must expose its toggle action.");
        provider!.Toggle();
        var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
        PumpUntil(() => combo.IsDropDownOpen && popup?.IsOpen == true && popup.Child is FrameworkElement { IsVisible: true }, "dropdown popup opening");
        PumpUntil(() => combo.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem { IsVisible: true }, "dropdown option realization");
        var option = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(index)!;
        RequireVisibleText(option, expectedLabel, "A dropdown option must show the user-facing label.");
        if (capturePath != null) CapturePopup(popup!, capturePath);
        option.Focus();
        option.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent, Source = option });
        PumpUntil(() => combo.SelectedIndex == index && !combo.IsDropDownOpen && popup!.IsOpen == false, "dropdown selection and dismissal");
    }

    private static void RequireVisibleText(DependencyObject element, string expected, string message)
    {
        PumpFor(20);
        Require(Descendants<TextBlock>(element).Any(block => block.IsVisible && block.ActualWidth > 0 && block.Text == expected), message);
    }

    private static object? InvokeDialog(string method, params object?[] arguments)
    {
        var type = typeof(MainWindow).Assembly.GetType("LocalResourceLibrary.App.Dialogs", throwOnError: true)!;
        return type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, arguments);
    }

    private static object? RunModal(Window owner, string title, Func<object?> open, Action<Window> interact)
    {
        var previous = owner.OwnedWindows.Cast<Window>().ToHashSet();
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        Window? dialog = null;
        Exception? failure = null;
        var handled = false;
        timer.Tick += (_, _) =>
        {
            dialog = owner.OwnedWindows.Cast<Window>().FirstOrDefault(candidate => !previous.Contains(candidate) && candidate.Title == title);
            if (dialog is not { IsLoaded: true, IsVisible: true })
            {
                if (stopwatch.Elapsed < TimeSpan.FromSeconds(8)) return;
                failure = new TimeoutException($"Timed out waiting for modal dialog '{title}'.");
                timer.Stop();
                foreach (var pending in owner.OwnedWindows.Cast<Window>().Where(candidate => !previous.Contains(candidate)).ToArray()) pending.Close();
                return;
            }
            timer.Stop();
            handled = true;
            try
            {
                interact(dialog);
                PumpUntil(() => !dialog.IsVisible, "modal dialog dismissal");
            }
            catch (Exception exception)
            {
                failure = exception;
                if (dialog.IsVisible) dialog.Close();
            }
        };
        timer.Start();
        object? result;
        try { result = open(); }
        finally
        {
            timer.Stop();
            if (dialog?.IsVisible == true) dialog.Close();
        }
        if (failure != null) throw new InvalidOperationException($"Modal dialog check failed for '{title}'.", failure);
        Require(handled, $"The expected modal dialog '{title}' was not observed.");
        return result;
    }

    private static DragEventArgs DragArgs(IDataObject data, DependencyObject target, RoutedEvent routedEvent)
    {
        // WPF exposes no public DragEventArgs constructor. Construct only the event payload, then use the real routed handlers.
        var constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.None, DragDropEffects.All, target, new Point(40, 40)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static void OpenResourceMenu(DataGrid grid)
    {
        // ContextMenuEventArgs also has internal constructors; the tested menu is still built by the actual routed handler.
        var constructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(object), typeof(bool)], null)!;
        var args = (ContextMenuEventArgs)constructor.Invoke([grid, true]);
        args.RoutedEvent = ContextMenuService.ContextMenuOpeningEvent;
        grid.RaiseEvent(args);
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, string operation)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            PumpFor(15);
            if (condition()) return;
        } while (stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException($"Timed out waiting for {operation}.");
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        PumpFor(40);
        var content = (FrameworkElement)window.Content;
        // Modal content has outer margins and relies on the Window's surface. Capture its
        // actual client root so the margin, background and bottom buttons remain in frame.
        if (content.Margin != new Thickness(0) && VisualTreeHelper.GetChildrenCount(window) > 0)
            content = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        SaveVisual(content, path);
    }

    private static void CapturePopup(Popup popup, string path)
    {
        var content = (FrameworkElement)popup.Child;
        // Popup content lives in a separate WPF visual tree. Include its hosting root
        // to retain the dropdown's outer margin without clipping the lower border.
        while (VisualTreeHelper.GetParent(content) is FrameworkElement parent) content = parent;
        content.UpdateLayout();
        PumpFor(30);
        SaveVisual(content, path);
    }

    private static void SaveVisual(FrameworkElement content, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static LocalResourceLibrary.App.App CreateIsolatedApplication()
    {
        // Application posts its startup callback from the constructor. Suppress that callback before
        // pumping, so the real App styles can load without creating a production AppData library.
        var posted = new List<DispatcherOperation>();
        var dispatcher = Dispatcher.CurrentDispatcher;
        void CapturePosted(object? sender, DispatcherHookEventArgs args) => posted.Add(args.Operation);
        dispatcher.Hooks.OperationPosted += CapturePosted;
        LocalResourceLibrary.App.App app;
        try { app = new LocalResourceLibrary.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; }
        finally { dispatcher.Hooks.OperationPosted -= CapturePosted; }
        foreach (var operation in posted) operation.Abort();
        Require(posted.Count > 0, "The isolated harness must capture and suppress WPF's queued production startup.");
        return app;
    }

    private sealed class BindingTraceListener : TraceListener
    {
        private readonly StringBuilder current = new();
        public List<string> Messages { get; } = [];
        public override void Write(string? message) => current.Append(message);
        public override void WriteLine(string? message)
        {
            current.Append(message);
            if (current.Length > 0) Messages.Add(current.ToString());
            current.Clear();
        }
    }

    private sealed class TestPlatform : IResourcePlatform
    {
        public int OpenCalls { get; private set; }
        public int LocationCalls { get; private set; }
        public string? LastOpenedPath { get; private set; }
        public string? LastLocationPath { get; private set; }
        public bool FileExists(string path) => File.Exists(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void Open(string path, string type) { LastOpenedPath = path; OpenCalls++; }
        public void OpenLocation(string path, string type) { LastLocationPath = path; LocationCalls++; }
        public void Move(string oldPath, string newPath, string type) => throw new InvalidOperationException("The UI smoke check must not invoke physical rename dialogs.");
    }
}
