using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>A native notification-area icon owned by the WinUI dispatcher thread.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint RestoreMessage = 0x8002;
    private readonly WindowProcedure _procedure;
    private readonly string _className;
    private readonly string _openText;
    private readonly string _exitText;
    private readonly uint _taskbarCreated;
    private readonly IntPtr _module = GetModuleHandleW(null);
    private IntPtr _window;
    private IntPtr _icon;
    private NotifyIconData _data;
    private bool _registered;
    private bool _disposed;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;

    public TrayIcon(string instanceKey, string iconPath, string title, string openText, string exitText)
    {
        _className = ClassName(instanceKey);
        _openText = openText;
        _exitText = exitText;
        _procedure = ProcessMessage;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        try
        {
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = _module,
                ClassName = _className
            };
            if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
            // A hidden top-level window receives Explorer's TaskbarCreated broadcast.
            _window = CreateWindowExW(0, _className, "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _icon = LoadImageW(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
            if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                Window = _window,
                Id = 1,
                Flags = 1 | 2 | 4 | 0x80, // MESSAGE, ICON, TIP, SHOWTIP
                Callback = CallbackMessage,
                Icon = _icon,
                Tip = title,
                Info = "",
                InfoTitle = ""
            };
            if (!AddIcon()) throw new InvalidOperationException("无法创建托盘图标 / Could not create notification-area icon.");
        }
        catch { Dispose(); throw; }
    }

    private static string ClassName(string key) => "LocalResourceLibrary.Tray." + key;

    public static bool TryRestoreExisting(string instanceKey)
    {
        var window = FindWindowW(ClassName(instanceKey), null);
        if (window == IntPtr.Zero) return false;
        // Let the already running process foreground its restored window.
        GetWindowThreadProcessId(window, out var processId);
        AllowSetForegroundWindow(processId);
        return PostMessageW(window, RestoreMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public static void BringToFront(IntPtr window) => SetForegroundWindow(window);

    private bool AddIcon()
    {
        if (!Shell_NotifyIconW(0, ref _data)) return false;
        _data.Version = 4;
        if (Shell_NotifyIconW(4, ref _data)) return true;
        Shell_NotifyIconW(2, ref _data);
        return false;
    }

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!_disposed)
        {
            if (_taskbarCreated != 0 && message == _taskbarCreated)
            {
                // Keep the application reachable if Explorer cannot restore its icon.
                if (!AddIcon()) RestoreRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (message == RestoreMessage)
            {
                RestoreRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (message == CallbackMessage)
            {
                var notification = (uint)lParam.ToInt64() & 0xffff;
                if (notification is 0x400 or 0x401) RestoreRequested?.Invoke(); // NIN_SELECT / NIN_KEYSELECT
                else if (notification == 0x7b) ShowMenu(window, wParam); // WM_CONTEXTMENU
                return IntPtr.Zero;
            }
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    private void ShowMenu(IntPtr window, IntPtr coordinates)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, 0, 1, _openText);
            AppendMenuW(menu, 0x800, 0, null);
            AppendMenuW(menu, 0, 2, _exitText);
            SetMenuDefaultItem(menu, 1, false);
            var packed = coordinates.ToInt64();
            var x = (short)(packed & 0xffff);
            var y = (short)((packed >> 16) & 0xffff);
            if (x == -1 && y == -1 && GetCursorPos(out var cursor)) { x = (short)cursor.X; y = (short)cursor.Y; }
            SetForegroundWindow(window);
            var command = TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, x, y, window, IntPtr.Zero);
            PostMessageW(window, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == 1) RestoreRequested?.Invoke();
            else if (command == 2) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data.Window != IntPtr.Zero) Shell_NotifyIconW(2, ref _data);
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_registered) { UnregisterClassW(_className, _module); _registered = false; }
        GC.KeepAlive(_procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? module);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool UnregisterClassW(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y,
        int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr FindWindowW(string className, string? title);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, bool byPosition);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetCursorPos(out Point point);
}
