using LocalResourceLibrary.WinUI.Services;
using LocalResourceLibrary.WinUI.Controls;
using LocalResourceLibrary.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LocalResourceLibrary.WinUI;

public sealed partial class MainWindow
{
    private readonly ShellIconProvider _shellIcons = new();
    private readonly Dictionary<ResourceRow, (string Target, int Size)> _loadedIconSizes = [];
    private readonly HashSet<(ResourceRow Row, string Target, int Size)> _loadingIcons = [];
    private readonly Queue<ResourceRow> _iconOrder = [];
    private bool _detailsOpen;
    private bool _explorerInitialized;
    private bool _windowClosed;
    private double _navigationWidth = 226, _detailsWidth = 340;

    private string Label(string chinese, string english) => _text.IsEnglish ? english : chinese;
    private ListViewBase ActiveResources => UsesList ? ResourceList : ResourceGrid;
    private bool UsesList => _vm.ViewMode is ResourceViewMode.Details or ResourceViewMode.Content;
    private int IconSize => _vm.ViewMode switch
    {
        ResourceViewMode.ExtraLargeIcons => 160,
        ResourceViewMode.LargeIcons => 96,
        ResourceViewMode.MediumIcons or ResourceViewMode.Tiles or ResourceViewMode.Content => 48,
        _ => 20
    };

    private void InitializeExplorer()
    {
        _navigationWidth = _vm.NavigationWidth;
        _detailsWidth = _vm.DetailsWidth;
        _explorerInitialized = true;
        Closed += (_, _) => { _windowClosed = true; _loadedIconSizes.Clear(); };
        ApplyPresentation();
    }

    private void ApplyPresentation()
    {
        if (!_explorerInitialized) return;
        ViewButtonText.Text = Label("查看", "View");
        SortButtonText.Text = Label("排序", "Sort");
        DetailsButtonText.Text = Label("详细信息", "Details");
        FileSizeLabel.Text = Label("大小", "Size");
        FileModifiedLabel.Text = Label("修改日期", "Date modified");
        AutomationProperties.SetName(ViewButton, ViewButtonText.Text);
        AutomationProperties.SetName(SortButton, SortButtonText.Text);
        AutomationProperties.SetName(DetailsButton, DetailsButtonText.Text);
        ToolTipService.SetToolTip(ViewButton, _vm.ViewModeLabel);
        ToolTipService.SetToolTip(SortButton, _vm.SortKeyLabel);
        ToolTipService.SetToolTip(CloseDetailsButton, Label("关闭资源详情", "Close resource details"));
        ResourceList.Visibility = UsesList ? Visibility.Visible : Visibility.Collapsed;
        ResourceGrid.Visibility = UsesList ? Visibility.Collapsed : Visibility.Visible;
        DetailsHeader.Visibility = _vm.ViewMode == ResourceViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
        var template = _vm.ViewMode switch
        {
            ResourceViewMode.Details => "DetailsTemplate",
            ResourceViewMode.Content => "ContentTemplate",
            ResourceViewMode.ExtraLargeIcons => "ExtraLargeTemplate",
            ResourceViewMode.LargeIcons => "LargeTemplate",
            ResourceViewMode.MediumIcons => "MediumTemplate",
            ResourceViewMode.Tiles => "TilesTemplate",
            _ => "CompactTemplate"
        };
        if (UsesList) ResourceList.ItemTemplate = (DataTemplate)Root.Resources[template];
        else
        {
            ResourceGrid.ItemTemplate = (DataTemplate)Root.Resources[template];
            ResourceGrid.ItemsPanel = (ItemsPanelTemplate)Root.Resources[_vm.ViewMode == ResourceViewMode.List ? "VerticalWrap" : "HorizontalWrap"];
            ScrollViewer.SetHorizontalScrollBarVisibility(ResourceGrid, _vm.ViewMode == ResourceViewMode.List ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(ResourceGrid, _vm.ViewMode == ResourceViewMode.List ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollMode(ResourceGrid, _vm.ViewMode == ResourceViewMode.List ? ScrollMode.Enabled : ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollMode(ResourceGrid, _vm.ViewMode == ResourceViewMode.List ? ScrollMode.Disabled : ScrollMode.Enabled);
        }
        SetHeader(NameHeader, ResourceSortKey.Name, Label("名称", "Name"));
        SetHeader(ModifiedHeader, ResourceSortKey.Modified, Label("修改日期", "Date modified"));
        SetHeader(TypeHeader, ResourceSortKey.Type, _text["Type"]);
        SetHeader(SizeHeader, ResourceSortKey.Size, Label("大小", "Size"));
        UpdatePaneWidths();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_windowClosed) return;
            // Load only realized items; native ListView/GridView virtualization remains active.
            foreach (var row in _vm.Rows)
                if (ActiveResources.ContainerFromItem(row) != null) LoadIconAsync(row);
        });
    }

    private void SetHeader(Button button, ResourceSortKey key, string text) =>
        button.Content = text + (_vm.SortKey == key ? (_vm.SortDescending ? "  ↓" : "  ↑") : "");

    private void View_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        (ResourceViewMode Mode, string Id, string Zh, string En, string Glyph)[] modes =
        [
            (ResourceViewMode.ExtraLargeIcons, "ViewExtraLarge", "超大图标", "Extra large icons", "\uE8B9"),
            (ResourceViewMode.LargeIcons, "ViewLarge", "大图标", "Large icons", "\uE8B9"),
            (ResourceViewMode.MediumIcons, "ViewMedium", "中等图标", "Medium icons", "\uE8B9"),
            (ResourceViewMode.SmallIcons, "ViewSmall", "小图标", "Small icons", "\uE80A"),
            (ResourceViewMode.List, "ViewList", "列表", "List", "\uE8FD"),
            (ResourceViewMode.Details, "ViewDetails", "详细信息", "Details", "\uE8A9"),
            (ResourceViewMode.Tiles, "ViewTiles", "平铺", "Tiles", "\uE8F1"),
            (ResourceViewMode.Content, "ViewContent", "内容", "Content", "\uE8A5")
        ];
        foreach (var mode in modes)
        {
            var item = new ToggleMenuFlyoutItem { Text = Label(mode.Zh, mode.En), IsChecked = _vm.ViewMode == mode.Mode, Icon = new FontIcon { Glyph = mode.Glyph } };
            AutomationProperties.SetAutomationId(item, mode.Id);
            item.Click += (_, _) => UpdateExplorer(() => _vm.ViewMode = mode.Mode);
            menu.Items.Add(item);
        }
        menu.ShowAt(ViewButton);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        (ResourceSortKey Key, string Id, string Zh, string En)[] keys =
        [
            (ResourceSortKey.Name, "SortName", "名称", "Name"),
            (ResourceSortKey.Modified, "SortModified", "修改日期", "Date modified"),
            (ResourceSortKey.Type, "SortType", "类型", "Type"),
            (ResourceSortKey.Size, "SortSize", "大小", "Size"),
            (ResourceSortKey.LastOpened, "SortLastOpened", "上次打开", "Last opened")
        ];
        foreach (var key in keys)
        {
            var item = new ToggleMenuFlyoutItem { Text = Label(key.Zh, key.En), IsChecked = _vm.SortKey == key.Key };
            AutomationProperties.SetAutomationId(item, key.Id);
            item.Click += (_, _) => UpdateExplorer(() => _vm.SortKey = key.Key, true);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var descending in new[] { false, true })
        {
            var item = new ToggleMenuFlyoutItem { Text = descending ? Label("递减", "Descending") : Label("递增", "Ascending"), IsChecked = _vm.SortDescending == descending };
            AutomationProperties.SetAutomationId(item, descending ? "SortDescending" : "SortAscending");
            item.Click += (_, _) => UpdateExplorer(() => _vm.SortDescending = descending, true);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var folders = new ToggleMenuFlyoutItem { Text = Label("文件夹优先", "Folders first"), IsChecked = _vm.FoldersFirst };
        AutomationProperties.SetAutomationId(folders, "FoldersFirst");
        folders.Click += (_, _) => UpdateExplorer(() => _vm.FoldersFirst = folders.IsChecked, true);
        menu.Items.Add(folders);
        menu.ShowAt(SortButton);
    }

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && Enum.TryParse<ResourceSortKey>(value, out var key))
            UpdateExplorer(() => { _vm.SortDescending = _vm.SortKey == key && !_vm.SortDescending; _vm.SortKey = key; }, true);
    }

    private async void UpdateExplorer(Action change, bool reorder = false)
    {
        if (_vm.IsBusy || _dialogOpen) return;
        SyncDraft();
        Exception? error = null;
        _rendering = true;
        try
        {
            change();
            if (reorder) _vm.Reorder();
            ApplyPresentation();
            ResourceList.SelectedItem = _vm.Selected;
            ResourceGrid.SelectedItem = _vm.Selected;
        }
        catch (Exception exception) { error = exception; }
        finally { _rendering = false; }
        if (error != null) await ShowErrorAsync(error);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSelection) return;
        _detailsOpen = !_detailsOpen;
        UpdatePaneWidths();
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePaneWidths();
    private void Body_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePaneWidths();

    private void UpdatePaneWidths()
    {
        if (!_explorerInitialized || Body.ActualWidth <= 0) return;
        var showDetails = _detailsOpen && _vm.HasSelection;
        var navigationMax = Math.Max(160, Math.Min(480, Body.ActualWidth - 312 - (showDetails ? 280 : 0)));
        var navigation = Math.Clamp(_navigationWidth, 160, navigationMax);
        NavigationColumn.Width = new GridLength(navigation);
        var detailsMax = Math.Max(280, Math.Min(620, Body.ActualWidth - navigation - 312));
        DetailsColumn.Width = new GridLength(showDetails ? Math.Clamp(_detailsWidth, 280, detailsMax) : 0);
        DetailsSplitterColumn.Width = new GridLength(showDetails ? 6 : 0);
        DetailsPanel.Visibility = DetailsSplitter.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_DragDelta(object? sender, PaneDragDeltaEventArgs e)
    {
        if (ReferenceEquals(sender, NavigationSplitter)) _navigationWidth = NavigationColumn.ActualWidth + e.HorizontalChange;
        else _detailsWidth = DetailsColumn.ActualWidth - e.HorizontalChange;
        UpdatePaneWidths();
        _navigationWidth = NavigationColumn.Width.Value;
        if (_detailsOpen) _detailsWidth = DetailsColumn.Width.Value;
    }

    private void Splitter_DragCompleted(object sender, RoutedEventArgs e) => SavePaneWidths();

    private void Splitter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        var change = e.Key == VirtualKey.Right ? 16 : -16;
        if (ReferenceEquals(sender, NavigationSplitter)) _navigationWidth = NavigationColumn.ActualWidth + change;
        else _detailsWidth = DetailsColumn.ActualWidth - change;
        UpdatePaneWidths();
        _navigationWidth = NavigationColumn.Width.Value;
        if (_detailsOpen) _detailsWidth = DetailsColumn.Width.Value;
        SavePaneWidths();
        e.Handled = true;
    }

    private async void SavePaneWidths()
    {
        try { _vm.NavigationWidth = _navigationWidth; _vm.DetailsWidth = _detailsWidth; }
        catch (Exception exception) { await ShowErrorAsync(exception); }
    }

    private async void ClearSelectionAsync()
    {
        if (!_vm.HasSelection || !await EnsureEditsAsync()) return;
        _rendering = true;
        try { _vm.Select(null); ResourceList.SelectedItem = ResourceGrid.SelectedItem = null; _detailsOpen = false; UpdatePaneWidths(); }
        finally { _rendering = false; }
    }

    private void Resource_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        var parent = e.OriginalSource as DependencyObject;
        while (parent != null && !ReferenceEquals(parent, sender))
        {
            if (parent is ListViewItem or GridViewItem or ScrollBar or Thumb) return;
            parent = VisualTreeHelper.GetParent(parent);
        }
        ClearSelectionAsync();
    }

    private bool IsResourceFocus()
    {
        var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        while (element != null)
        {
            if (element == ResourceList || element == ResourceGrid) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Resource_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ResourceRow row)
        {
            AutomationProperties.SetName(args.ItemContainer, row.Name);
            AutomationProperties.SetAutomationId(args.ItemContainer, row.Id);
            LoadIconAsync(row);
        }
    }

    private void Navigation_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is NavigationEntry entry)
        {
            AutomationProperties.SetName(args.ItemContainer, entry.Name);
            AutomationProperties.SetAutomationId(args.ItemContainer, entry.Id);
        }
    }

    private async void LoadIconAsync(ResourceRow row)
    {
        var size = IconSize;
        var target = row.Target;
        var request = (row, target, size);
        if (_windowClosed || (_loadedIconSizes.TryGetValue(row, out var loaded) && loaded.Target == target && loaded.Size >= size) || !_loadingIcons.Add(request)) return;
        ImageSource? icon = null;
        try
        {
            icon = await _shellIcons.GetAsync(target, row.IsFolder, size);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        finally
        {
            void Complete()
            {
                if (!_windowClosed && row.Target == target && icon != null &&
                    (!_loadedIconSizes.TryGetValue(row, out var current) || current.Target != target || current.Size <= size))
                {
                    row.Icon = icon;
                    if (!_loadedIconSizes.ContainsKey(row)) _iconOrder.Enqueue(row);
                    _loadedIconSizes[row] = (target, size);
                    var attempts = _iconOrder.Count;
                    while (_loadedIconSizes.Count > 512 && attempts-- > 0 && _iconOrder.TryDequeue(out var oldest))
                    {
                        if (ReferenceEquals(oldest, _vm.Selected) || ActiveResources.ContainerFromItem(oldest) != null)
                            _iconOrder.Enqueue(oldest);
                        else
                        {
                            _loadedIconSizes.Remove(oldest);
                            oldest.Icon = null;
                        }
                    }
                }
                _loadingIcons.Remove(request);
            }
            if (DispatcherQueue.HasThreadAccess) Complete();
            else DispatcherQueue.TryEnqueue(Complete);
        }
    }
}
