using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.Core;
using LocalResourceLibrary.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LocalResourceLibrary.WinUI.ViewModels;

public enum ResourceViewMode { ExtraLargeIcons, LargeIcons, MediumIcons, SmallIcons, List, Details, Tiles, Content }
public enum ResourceSortKey { Name, Modified, Type, Size, LastOpened }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record NavigationEntry(string Id, string Name, int Count, bool IsProject)
{
    public string IconGlyph => Id switch
    {
        "@all" => "\uE80F",
        "@recent" => "\uE823",
        "@missing" => "\uE7BA",
        _ => "\uE8B7"
    };
}

public sealed class MembershipChoice(Project project, bool selected) : Observable
{
    public string Id => project.Id;
    public string Name => project.Name;
    private bool _isSelected = selected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Notify(); } }
}

public sealed class ResourceRow : Observable
{
    private ResourceItem _item;
    private readonly Localizer _text;
    private ImageSource? _icon;
    private string? _shellType;
    public ResourceRow(ResourceItem item, Localizer text) : this(item, text, FileMetadata.Read(item)) { }
    internal ResourceRow(ResourceItem item, Localizer text, FileMetadata metadata)
    {
        _item = item;
        _text = text;
        FileModifiedAt = metadata.ModifiedAt;
        FileSize = metadata.Size;
    }
    public ResourceItem Item => _item;
    public string Id => _item.Id;
    public string Name => _item.DisplayName;
    public string RealName => _item.RealName;
    public string Target => _item.Target;
    public string Description => _item.Description;
    public bool IsFolder => _item.Type == "folder";
    public string Type => IsFolder ? _text["Folder"] : _shellType ?? FriendlyType();
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE8A5";
    public ImageSource? Icon { get => _icon; set { if (ReferenceEquals(_icon, value)) return; _icon = value; Notify(); Notify(nameof(IconVisibility)); Notify(nameof(FallbackIconVisibility)); } }
    public Visibility IconVisibility => Icon == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackIconVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public string ProjectNames => _item.Projects.Count == 0 ? _text["Unassigned"] : string.Join(" · ", _item.Projects.Select(p => p.Name));
    public string Status => _text[_item.IsMissing ? "Missing" : "Available"];
    public bool IsMissing => _item.IsMissing;
    public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;
    public DateTimeOffset? FileModifiedAt { get; private set; }
    public long? FileSize { get; private set; }
    public string ModifiedText => FileModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string SizeText => IsFolder ? "" : FileSize is { } size ? FormatSize(size) : "—";
    public string LastOpened => _item.LastOpenedAt is { } date ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : _text["Never"];

    public void SetShellType(string? description)
    {
        var next = string.IsNullOrWhiteSpace(description) ? null : description;
        if (_shellType == next) return;
        _shellType = next;
        Notify(nameof(Type));
    }

    internal void Update(ResourceItem item, FileMetadata metadata)
    {
        _item = item;
        FileModifiedAt = metadata.ModifiedAt;
        FileSize = metadata.Size;
        Notify("");
    }

    private string FriendlyType()
    {
        if (_item.Type != "file") return _item.Type;
        var extension = Path.GetExtension(Target).TrimStart('.').ToLowerInvariant();
        var label = extension switch
        {
            "txt" => ("文本文档", "Text document"),
            "pdf" => ("PDF 文档", "PDF document"),
            "doc" or "docx" => ("Word 文档", "Word document"),
            "xls" or "xlsx" => ("Excel 工作簿", "Excel workbook"),
            "ppt" or "pptx" => ("PowerPoint 演示文稿", "PowerPoint presentation"),
            "jpg" or "jpeg" => ("JPEG 图像", "JPEG image"),
            "png" => ("PNG 图像", "PNG image"),
            "gif" => ("GIF 图像", "GIF image"),
            "svg" => ("SVG 图像", "SVG image"),
            "mp3" => ("MP3 音频", "MP3 audio"),
            "wav" => ("WAV 音频", "WAV audio"),
            "mp4" => ("MP4 视频", "MP4 video"),
            "zip" => ("ZIP 压缩文件", "ZIP archive"),
            "7z" => ("7Z 压缩文件", "7Z archive"),
            "exe" => ("应用程序", "Application"),
            "lnk" => ("快捷方式", "Shortcut"),
            "dwg" => ("AutoCAD 图形", "AutoCAD drawing"),
            "md" => ("Markdown 文档", "Markdown document"),
            _ => ("", "")
        };
        if (label.Item1.Length > 0) return _text.IsEnglish ? label.Item2 : label.Item1;
        return extension.Length == 0 ? _text["File"] : extension.ToUpperInvariant() + (_text.IsEnglish ? " file" : " 文件");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.##} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.##} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }
}

internal sealed record FileMetadata(DateTimeOffset? ModifiedAt, long? Size)
{
    public static FileMetadata Read(ResourceItem item)
    {
        if (item.IsMissing) return new(null, null);
        try
        {
            if (item.Type == "folder")
            {
                var directory = new DirectoryInfo(item.Target);
                return directory.Exists ? new(new DateTimeOffset(directory.LastWriteTimeUtc), null) : new(null, null);
            }
            var file = new FileInfo(item.Target);
            return file.Exists ? new(new DateTimeOffset(file.LastWriteTimeUtc), file.Length) : new(null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(null, null);
        }
    }
}

public sealed class MainViewModel : Observable
{
    private readonly ISearchProvider _search = new LocalTextSearchProvider();
    private readonly ExplorerPreferencesStore _preferencesStore;
    private ExplorerPreferences _preferences;
    private readonly Dictionary<string, CachedMetadata> _metadata = [];
    private readonly Dictionary<string, ResourceRow> _rowCache = [];
    private sealed record CachedMetadata(string Target, string Type, bool Missing, DateTimeOffset UpdatedAt, FileMetadata Value);

    public MainViewModel(Localizer text, string databasePath)
    {
        Text = text;
        DatabasePath = databasePath;
        _status = text["Ready"];
        _preferencesStore = new(databasePath);
        _preferences = _preferencesStore.Load();
    }

    public Localizer Text { get; }
    public string DatabasePath { get; }
    public ResourceViewMode ViewMode
    {
        get => _preferences.ViewMode;
        set { if (!Enum.IsDefined(value) || value == ViewMode) return; _preferences = _preferences with { ViewMode = value }; SavePreferences(); Notify(); Notify(nameof(ViewModeLabel)); }
    }
    public ResourceSortKey SortKey
    {
        get => _preferences.SortKey;
        set { if (!Enum.IsDefined(value) || value == SortKey) return; _preferences = _preferences with { SortKey = value }; SavePreferences(); Notify(); Notify(nameof(SortKeyLabel)); }
    }
    public bool SortDescending
    {
        get => _preferences.SortDescending;
        set { if (value == SortDescending) return; _preferences = _preferences with { SortDescending = value }; SavePreferences(); Notify(); }
    }
    public bool FoldersFirst
    {
        get => _preferences.FoldersFirst;
        set { if (value == FoldersFirst) return; _preferences = _preferences with { FoldersFirst = value }; SavePreferences(); Notify(); }
    }
    public double NavigationWidth
    {
        get => _preferences.NavigationWidth;
        set { value = ExplorerPreferencesStore.ValidWidth(value, 226, 160, 480); if (value == NavigationWidth) return; _preferences = _preferences with { NavigationWidth = value }; SavePreferences(); Notify(); }
    }
    public double DetailsWidth
    {
        get => _preferences.DetailsWidth;
        set { value = ExplorerPreferencesStore.ValidWidth(value, 340, 280, 620); if (value == DetailsWidth) return; _preferences = _preferences with { DetailsWidth = value }; SavePreferences(); Notify(); }
    }
    public string ViewModeLabel => ViewMode switch
    {
        ResourceViewMode.ExtraLargeIcons => Text.IsEnglish ? "Extra large icons" : "超大图标",
        ResourceViewMode.LargeIcons => Text.IsEnglish ? "Large icons" : "大图标",
        ResourceViewMode.MediumIcons => Text.IsEnglish ? "Medium icons" : "中等图标",
        ResourceViewMode.SmallIcons => Text.IsEnglish ? "Small icons" : "小图标",
        ResourceViewMode.List => Text.IsEnglish ? "List" : "列表",
        ResourceViewMode.Tiles => Text.IsEnglish ? "Tiles" : "平铺",
        ResourceViewMode.Content => Text.IsEnglish ? "Content" : "内容",
        _ => Text.IsEnglish ? "Details" : "详细信息"
    };
    public string SortKeyLabel => SortKey switch
    {
        ResourceSortKey.Modified => Text.IsEnglish ? "Date modified" : "修改日期",
        ResourceSortKey.Type => Text["Type"],
        ResourceSortKey.Size => Text.IsEnglish ? "Size" : "大小",
        ResourceSortKey.LastOpened => Text["LastOpened"],
        _ => Text.IsEnglish ? "Name" : "名称"
    };
    private string? _preferencesSaveError;
    private void SavePreferences()
    {
        if (_preferencesStore.Save(_preferences))
        {
            if (_preferencesSaveError != null && Status == _preferencesSaveError) Status = Text["Ready"];
            _preferencesSaveError = null;
            return;
        }
        _preferencesSaveError = Text.IsEnglish
            ? "View settings apply for this session, but could not be saved."
            : "查看设置已在本次使用中生效，但未能保存。";
        Status = _preferencesSaveError;
    }
    public LibrarySnapshot Snapshot { get; private set; } = new([], []);
    public ObservableCollection<NavigationEntry> Navigation { get; } = [];
    public ObservableCollection<ResourceRow> Rows { get; } = [];
    public ObservableCollection<MembershipChoice> Memberships { get; } = [];
    public string NavigationId { get; set; } = "@all";
    public string Query { get; set; } = "";
    public ResourceRow? Selected { get; private set; }
    public bool HasSelection => Selected != null;
    public Visibility DetailsVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChooseVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MissingVisibility => Selected?.IsMissing == true ? Visibility.Visible : Visibility.Collapsed;
    public string MissingHelpText => Text.IsEnglish
        ? "This path is missing or temporarily unavailable. Its details and project membership are preserved."
        : "路径不存在或暂时无法访问。说明、笔记和项目归属仍然保留。";
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoProjectsVisibility => Memberships.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => Text[Snapshot.Items.Count == 0 ? "EmptyTitle" : "NoResults"];
    public string EmptyHelp => Text[Snapshot.Items.Count == 0 ? "EmptyHelp" : "NoResultsHelp"];
    public string ViewTitle => Navigation.FirstOrDefault(p => p.Id == NavigationId)?.Name ?? Text["All"];
    public string CountText => Text.Format("Count", Rows.Count);
    public string CreatedText => Selected?.Item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string UpdatedText => Selected?.Item.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string LastOpenedText => Selected?.LastOpened ?? "";
    public string SelectedFileSizeText => Selected?.SizeText ?? "";
    public string SelectedModifiedText => Selected?.ModifiedText ?? "";
    public string OpenCountText => Selected?.Item.OpenCount.ToString() ?? "";
    private bool _busy;
    public bool IsBusy { get => _busy; set { _busy = value; Notify(); Notify(nameof(IsReady)); } }
    public bool IsReady => !IsBusy;
    private string _status;
    public string Status { get => _status; set { _status = value; Notify(); } }
    private string _alias = "", _description = "", _note = "";
    public string Alias { get => _alias; set { _alias = value; Notify(); NotifyDirty(); } }
    public string Description { get => _description; set { _description = value; Notify(); NotifyDirty(); } }
    public string Note { get => _note; set { _note = value; Notify(); NotifyDirty(); } }
    public bool IsDirty => Selected is { } selected &&
        (_alias != selected.Item.Alias || _description != selected.Item.Description || _note != selected.Item.Note ||
        !Memberships.Where(p => p.IsSelected).Select(p => p.Id).ToHashSet().SetEquals(selected.Item.Projects.Select(p => p.Id)));
    public string SaveState => Text[IsDirty ? "Unsaved" : "Saved"];
    private void NotifyDirty() { Notify(nameof(IsDirty)); Notify(nameof(SaveState)); }
    public string? CurrentProjectId => Navigation.FirstOrDefault(p => p.Id == NavigationId && p.IsProject)?.Id;
    /// <summary>Call before an explicit refresh to read current dates and sizes again.</summary>
    public void InvalidateFileMetadata() => _metadata.Clear();

    public void Load(LibrarySnapshot snapshot)
    {
        Snapshot = snapshot;
        var currentIds = snapshot.Items.Select(item => item.Id).ToHashSet();
        foreach (var id in _metadata.Keys.Where(id => !currentIds.Contains(id)).ToArray()) _metadata.Remove(id);
        foreach (var id in _rowCache.Keys.Where(id => !currentIds.Contains(id)).ToArray()) _rowCache.Remove(id);
        Navigation.Clear();
        Navigation.Add(new("@all", Text["All"], snapshot.Items.Count, false));
        Navigation.Add(new("@recent", Text["Recent"], snapshot.Items.Count(i => i.LastOpenedAt != null), false));
        Navigation.Add(new("@missing", Text["Missing"], snapshot.Items.Count(i => i.IsMissing), false));
        foreach (var project in snapshot.Projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Navigation.Add(new(project.Id, project.Name, snapshot.Items.Count(i => i.Projects.Any(p => p.Id == project.Id)), true));
        if (!Navigation.Any(p => p.Id == NavigationId)) NavigationId = "@all";
        Filter();
    }
    public void Filter()
    {
        IEnumerable<ResourceItem> items = Snapshot.Items;
        if (NavigationId == "@recent") items = items.Where(i => i.LastOpenedAt != null);
        else if (NavigationId == "@missing") items = items.Where(i => i.IsMissing);
        else if (NavigationId != "@all") items = items.Where(i => i.Projects.Any(p => p.Id == NavigationId));
        var rows = _search.Search(items, Query).Select(GetRow).ToList();
        rows.Sort(CompareRows);
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        Notify(nameof(EmptyVisibility)); Notify(nameof(EmptyTitle)); Notify(nameof(EmptyHelp)); Notify(nameof(ViewTitle)); Notify(nameof(CountText));
    }

    /// <summary>Sort existing row objects without discarding selection or the details draft.</summary>
    public void Reorder()
    {
        var sorted = Rows.ToList();
        sorted.Sort(CompareRows);
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = Rows.IndexOf(sorted[index]);
            if (current != index) Rows.Move(current, index);
        }
    }

    private ResourceRow GetRow(ResourceItem item)
    {
        if (!_metadata.TryGetValue(item.Id, out var cached) || cached.Target != item.Target || cached.Type != item.Type ||
            cached.Missing != item.IsMissing || cached.UpdatedAt != item.UpdatedAt)
        {
            cached = new(item.Target, item.Type, item.IsMissing, item.UpdatedAt, FileMetadata.Read(item));
            _metadata[item.Id] = cached;
        }
        if (!_rowCache.TryGetValue(item.Id, out var row) || row.Target != item.Target || row.Item.Type != item.Type)
        {
            row = new(item, Text, cached.Value);
            _rowCache[item.Id] = row;
        }
        else row.Update(item, cached.Value);
        return row;
    }

    private int CompareRows(ResourceRow left, ResourceRow right)
    {
        if (FoldersFirst && left.IsFolder != right.IsFolder) return left.IsFolder ? -1 : 1;
        var comparison = SortKey switch
        {
            ResourceSortKey.Modified => CompareNullable(left.FileModifiedAt, right.FileModifiedAt),
            ResourceSortKey.Size => CompareNullable(left.FileSize, right.FileSize),
            ResourceSortKey.LastOpened => CompareNullable(left.Item.LastOpenedAt, right.Item.LastOpenedAt),
            ResourceSortKey.Type => Directed(NaturalStringComparer.Compare(left.Type, right.Type)),
            _ => Directed(NaturalStringComparer.Compare(left.Name, right.Name))
        };
        if (comparison != 0) return comparison;
        comparison = NaturalStringComparer.Compare(left.Name, right.Name);
        if (comparison != 0) return comparison;
        comparison = StringComparer.OrdinalIgnoreCase.Compare(left.Target, right.Target);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private int Directed(int comparison) => SortDescending ? -Math.Sign(comparison) : Math.Sign(comparison);
    private int CompareNullable<T>(T? left, T? right) where T : struct, IComparable<T>
    {
        if (left == null) return right == null ? 0 : 1;
        if (right == null) return -1;
        return Directed(left.Value.CompareTo(right.Value));
    }
    public void Select(ResourceRow? row)
    {
        Selected = row;
        _alias = row?.Item.Alias ?? ""; _description = row?.Item.Description ?? ""; _note = row?.Item.Note ?? "";
        Memberships.Clear();
        foreach (var project in Snapshot.Projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var choice = new MembershipChoice(project, row?.Item.Projects.Any(p => p.Id == project.Id) == true);
            choice.PropertyChanged += (_, _) => NotifyDirty();
            Memberships.Add(choice);
        }
        Notify("");
    }
}

internal static class NaturalStringComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);

    public static int Compare(string left, string right)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return StrCmpLogicalW(left, right); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return ComparePortable(left, right);
    }

    private static int ComparePortable(string left, string right)
    {
        var x = 0;
        var y = 0;
        while (x < left.Length && y < right.Length)
        {
            if (char.IsAsciiDigit(left[x]) && char.IsAsciiDigit(right[y]))
            {
                var xEnd = x;
                var yEnd = y;
                while (xEnd < left.Length && char.IsAsciiDigit(left[xEnd])) xEnd++;
                while (yEnd < right.Length && char.IsAsciiDigit(right[yEnd])) yEnd++;
                var xNumber = x;
                var yNumber = y;
                while (xNumber < xEnd && left[xNumber] == '0') xNumber++;
                while (yNumber < yEnd && right[yNumber] == '0') yNumber++;
                var comparison = (xEnd - xNumber).CompareTo(yEnd - yNumber);
                if (comparison == 0) comparison = left.AsSpan(xNumber, xEnd - xNumber).SequenceCompareTo(right.AsSpan(yNumber, yEnd - yNumber));
                if (comparison != 0) return comparison;
                x = xEnd;
                y = yEnd;
                continue;
            }
            var characterComparison = char.ToUpperInvariant(left[x]).CompareTo(char.ToUpperInvariant(right[y]));
            if (characterComparison != 0) return characterComparison;
            x++;
            y++;
        }
        return (left.Length - x).CompareTo(right.Length - y);
    }
}
