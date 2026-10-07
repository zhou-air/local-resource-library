using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record NavigationEntry(string Id, string Name, int Count, bool IsProject);
public sealed class MembershipChoice(Project project, bool selected) : Observable
{
    public string Id => project.Id;
    public string Name => project.Name;
    private bool _isSelected = selected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Notify(); } }
}
public sealed class ResourceRow(ResourceItem item, Localizer text)
{
    public ResourceItem Item => item;
    public string Id => item.Id;
    public string Name => item.DisplayName;
    public string RealName => item.RealName;
    public string Target => item.Target;
    public string Description => item.Description;
    public string Type => item.Type switch { "file" => text["File"], "folder" => text["Folder"], _ => item.Type };
    public string ProjectNames => item.Projects.Count == 0 ? text["Unassigned"] : string.Join(" · ", item.Projects.Select(p => p.Name));
    public string Status => text[item.IsMissing ? "Missing" : "Available"];
    public bool IsMissing => item.IsMissing;
    public string LastOpened => item.LastOpenedAt is { } date ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : text["Never"];
}

public sealed class MainViewModel(Localizer text, string databasePath) : Observable
{
    private readonly ISearchProvider _search = new LocalTextSearchProvider();
    public Localizer Text { get; } = text;
    public string DatabasePath { get; } = databasePath;
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
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoProjectsVisibility => Memberships.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => Text[Snapshot.Items.Count == 0 ? "EmptyTitle" : "NoResults"];
    public string EmptyHelp => Text[Snapshot.Items.Count == 0 ? "EmptyHelp" : "NoResultsHelp"];
    public string ViewTitle => Navigation.FirstOrDefault(p => p.Id == NavigationId)?.Name ?? Text["All"];
    public string CountText => Text.Format("Count", Rows.Count);
    public string CreatedText => Selected?.Item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string UpdatedText => Selected?.Item.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
    public string LastOpenedText => Selected?.LastOpened ?? "";
    public string OpenCountText => Selected?.Item.OpenCount.ToString() ?? "";
    private bool _busy;
    public bool IsBusy { get => _busy; set { _busy = value; Notify(); Notify(nameof(IsReady)); } }
    public bool IsReady => !IsBusy;
    private string _status = text["Ready"];
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
    public void Load(LibrarySnapshot snapshot)
    {
        Snapshot = snapshot;
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
        if (NavigationId == "@recent") items = items.Where(i => i.LastOpenedAt != null).OrderByDescending(i => i.LastOpenedAt);
        else
        {
            if (NavigationId == "@missing") items = items.Where(i => i.IsMissing);
            else if (NavigationId != "@all") items = items.Where(i => i.Projects.Any(p => p.Id == NavigationId));
            items = items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        }
        Rows.Clear();
        foreach (var item in _search.Search(items, Query)) Rows.Add(new(item, Text));
        Notify(nameof(EmptyVisibility)); Notify(nameof(EmptyTitle)); Notify(nameof(EmptyHelp)); Notify(nameof(ViewTitle)); Notify(nameof(CountText));
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
