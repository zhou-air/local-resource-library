// These narrow types let the actual view-model sources run without starting a WinUI window.
// Window, XAML binding, layout, and native-icon integration require the WinUI checks separately.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public readonly record struct Thickness(double Left, double Top, double Right, double Bottom);
}
namespace Microsoft.UI.Xaml.Media
{
    public abstract class ImageSource { }
}
