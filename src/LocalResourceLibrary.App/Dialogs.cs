using System.Windows;
using System.Windows.Controls;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.App;

internal static class Dialogs
{
    private static Window Create(Window owner, string title, StackPanel body)
    {
        var window = new Window { Owner = owner, Title = title, Content = body, Width = 470, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        window.SetResourceReference(Control.BackgroundProperty, "Surface");
        return window;
    }

    private static TextBlock Label(Window owner, string value, Thickness margin)
        => new() { Text = value, Style = (Style)owner.FindResource("Label"), TextWrapping = TextWrapping.Wrap, Margin = margin };

    public static (string Value, string Description)? Input(Window owner, Localizer text, string title, string label,
        string value, string? help = null, string? description = null, string confirm = "Save")
    {
        var body = new StackPanel { Margin = new Thickness(26) };
        if (help != null)
        {
            var guidance = new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Margin = new Thickness(0, 0, 0, 20) };
            guidance.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(guidance);
        }
        body.Children.Add(Label(owner, label, new Thickness(0, 0, 0, 8)));
        var input = new TextBox { Text = value, MinWidth = 300 };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        body.Children.Add(input);
        TextBox? desc = null;
        if (description != null)
        {
            body.Children.Add(Label(owner, text["ProjectDescription"], new Thickness(0, 20, 0, 8)));
            desc = new TextBox { Text = description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            body.Children.Add(desc);
        }
        var window = Create(owner, title, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        var ok = new Button { Content = text[confirm], Style = (Style)owner.FindResource("PrimaryButton"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        ok.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.TextChanged += (_, _) => ok.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = text["Cancel"], Style = (Style)owner.FindResource("SecondaryButton"), IsCancel = true, MinWidth = 80, Margin = new Thickness(0) });
        body.Children.Add(buttons);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? (input.Text, desc?.Text ?? "") : null;
    }
    public static Project? ChooseProject(Window owner, Localizer text, string title, IEnumerable<Project> projects)
    {
        var body = new StackPanel { Margin = new Thickness(26) };
        body.Children.Add(Label(owner, text["ChooseProject"], new Thickness(0, 0, 0, 10)));
        var list = new ComboBox { ItemsSource = projects.ToArray(), DisplayMemberPath = nameof(Project.Name), SelectedIndex = 0 };
        body.Children.Add(list);
        var window = Create(owner, title, body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 26, 0, 0) };
        var ok = new Button { Content = text["Confirm"], Style = (Style)owner.FindResource("PrimaryButton"), IsDefault = true, IsEnabled = list.SelectedItem != null, MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = text["Cancel"], Style = (Style)owner.FindResource("SecondaryButton"), IsCancel = true, MinWidth = 80, Margin = new Thickness(0) }); body.Children.Add(buttons);
        return window.ShowDialog() == true ? list.SelectedItem as Project : null;
    }
}
