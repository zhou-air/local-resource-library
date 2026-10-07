using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LocalResourceLibrary.WinUI.Localization;
using LocalResourceLibrary.WinUI.Services;
using LocalResourceLibrary.Core;
using Microsoft.UI.Xaml;

namespace LocalResourceLibrary.WinUI;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dataDirectory = GetDataDirectory(Environment.GetCommandLineArgs().Skip(1).ToArray());
            Directory.CreateDirectory(dataDirectory);

            // Keep the established path normalization and per-library lock identity
            // so multiple instances cannot write to the same library at the same time.
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.GetFullPath(dataDirectory).TrimEnd('\\').ToUpperInvariant())));
            _mutex = new Mutex(true, "Local\\LocalResourceLibrary-" + key, out _ownsMutex);
            if (!_ownsMutex)
            {
                ShowStartupMessage("此资源库已在运行。请关闭已打开的资源库后再试。\n" +
                    "This library is already open. Close the existing library before trying this version.");
                ReleaseLibraryLock();
                Exit();
                return;
            }

            var settings = new SettingsStore(dataDirectory);
            var text = new Localizer(settings.Language);
            var service = new LibraryService(Path.Combine(dataDirectory, "library.db"));
            _window = new MainWindow(service, text, settings);
            _window.Closed += (_, _) => ReleaseLibraryLock();
            _window.Activate();
        }
        catch (Exception exception)
        {
            ShowStartupMessage("无法启动资源库 / Could not start library\n\n" + exception.Message, true);
            ReleaseLibraryLock();
            Exit();
        }
    }

    private static string GetDataDirectory(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalResourceLibrary");
        }

        if (arguments.Length != 2 || arguments[0] != "--data-dir")
            throw new ArgumentException("Usage: LocalResourceLibrary.WinUI.exe [--data-dir PATH]");
        return Path.GetFullPath(arguments[1]);
    }

    private void ReleaseLibraryLock()
    {
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    private static void ShowStartupMessage(string message, bool error = false) =>
        MessageBoxW(IntPtr.Zero, message, "Local Resource Library · WinUI 3", error ? 0x10u : 0x40u);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
