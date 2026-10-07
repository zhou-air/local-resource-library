using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using LocalResourceLibrary.App.Localization;
using LocalResourceLibrary.App.Services;
using LocalResourceLibrary.Core;

namespace LocalResourceLibrary.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalResourceLibrary");
            if (e.Args.Length > 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--data-dir") throw new ArgumentException("Usage: LocalResourceLibrary.exe [--data-dir PATH]");
                dataDirectory = Path.GetFullPath(e.Args[1]);
            }
            Directory.CreateDirectory(dataDirectory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).TrimEnd('\\').ToUpperInvariant())));
            _mutex = new Mutex(true, "Local\\LocalResourceLibrary-" + key, out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("此资源库已在运行。请切换到已打开的窗口。\nThis library is already open. Please use the existing window.", "Local Resource Library");
                Shutdown(); return;
            }
            var settings = new SettingsStore(dataDirectory);
            var text = new Localizer(settings.Language);
            var service = new LibraryService(Path.Combine(dataDirectory, "library.db"));
            var window = new MainWindow(service, text, settings);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法启动资源库 / Could not start library\n\n" + ex.Message, "Local Resource Library", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
