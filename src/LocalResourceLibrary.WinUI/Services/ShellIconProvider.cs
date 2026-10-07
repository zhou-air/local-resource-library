using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Loads the icons registered with Windows without extracting them on the UI thread.</summary>
public sealed class ShellIconProvider
{
    private const int CacheCapacity = 512;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private readonly LinkedList<CacheKey> _recent = new();
    private readonly SemaphoreSlim _workers = new(4);

    /// <summary>
    /// Call on a WinUI UI thread. The returned source belongs to that thread; a null result allows
    /// the caller to keep its generic icon when Windows cannot provide an associated icon.
    /// </summary>
    public async Task<ImageSource?> GetAsync(string path, bool isFolder, int size = 48)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || !dispatcher.HasThreadAccess || string.IsNullOrWhiteSpace(path)) return null;

        size = Math.Clamp(size, 16, 256);
        // Keep the real path even for ordinary files: icon handlers can define per-file icons.
        var key = new CacheKey(path, isFolder, size);
        Task<IconPixels?> pending;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                _recent.Remove(cached.Node);
                _recent.AddLast(cached.Node);
                pending = cached.Pending;
            }
            else
            {
                pending = ExtractAsync(path, isFolder, size);
                var node = _recent.AddLast(key);
                _cache.Add(key, new(pending, node));
                while (_cache.Count > CacheCapacity && _recent.First is { } oldest)
                {
                    _cache.Remove(oldest.Value);
                    _recent.RemoveFirst();
                }
            }
        }

        var pixels = await pending.ConfigureAwait(false);
        if (pixels is null) return null;
        if (dispatcher.HasThreadAccess) return CreateSource(pixels);

        // WinUI does not always install a SynchronizationContext. Dispatch explicitly rather than
        // assuming the continuation of await has returned to the calling UI thread.
        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnShutdown(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args) => completion.TrySetResult(null);
        var subscribed = false;
        try
        {
            dispatcher.ShutdownStarting += OnShutdown;
            subscribed = true;
            if (!dispatcher.TryEnqueue(() => completion.TrySetResult(CreateSource(pixels)))) return null;
            return await completion.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (subscribed)
            {
                try { dispatcher.ShutdownStarting -= OnShutdown; }
                catch (Exception) { /* The dispatcher may already be shutting down. */ }
            }
        }
    }

    private async Task<IconPixels?> ExtractAsync(string path, bool isFolder, int size)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Extract(path, isFolder, size)).ConfigureAwait(false);
        }
        finally
        {
            _workers.Release();
        }
    }

    private static ImageSource? CreateSource(IconPixels pixels)
    {
        try
        {
            var image = new WriteableBitmap(pixels.Width, pixels.Height);
            using var stream = image.PixelBuffer.AsStream();
            stream.Write(pixels.Bytes, 0, pixels.Bytes.Length);
            image.Invalidate();
            return image;
        }
        catch (Exception)
        {
            // A window can close while a background Shell request is still finishing.
            return null;
        }
    }

    private static IconPixels? Extract(string path, bool isFolder, int size)
    {
        // Thread-pool threads use MTA. Balance successful calls, including S_FALSE, exactly once.
        var initialized = Native.CoInitializeEx(IntPtr.Zero, 0);
        var uninitialize = initialized >= 0;
        try
        {
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) return null;
            return FromShellItem(path, size) ?? FromAssociatedIcon(path, isFolder, size);
        }
        catch (Exception)
        {
            // Missing paths, unavailable network locations, and third-party icon handlers must
            // not stop the resource list from loading.
            return null;
        }
        finally
        {
            if (uninitialize) Native.CoUninitialize();
        }
    }

    private static IconPixels? FromShellItem(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory is null) return null;
            // ICONONLY avoids thumbnails; SCALEUP makes all Explorer view sizes usable even if
            // the association only supplies a smaller image.
            if (factory.GetImage(new NativeSize(size, size), 0x4 | 0x100, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            return ReadBitmap(bitmap);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.FinalReleaseComObject(factory);
        }
    }

    private static IconPixels? ReadBitmap(IntPtr bitmap)
    {
        if (Native.GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var details) == 0) return null;
        var width = details.Width;
        var height = Math.Abs(details.Height);
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024) return null;
        var bytes = new byte[checked(width * height * 4)];
        var info = BitmapInfo.ForSize(width, height);
        var dc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            // A top-down 32-bit DIB uses the same BGRA layout as WriteableBitmap.PixelBuffer.
            if (Native.GetDIBits(dc, bitmap, 0, (uint)height, bytes, ref info, 0) != height) return null;
            var hasAlpha = false;
            var straightAlpha = false;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var alpha = bytes[i + 3];
                hasAlpha |= alpha != 0;
                if (alpha > 0 && alpha < 255 && (bytes[i] > alpha || bytes[i + 1] > alpha || bytes[i + 2] > alpha)) straightAlpha = true;
            }
            // Older handlers may return a bitmap without alpha. Render an HICON instead so its
            // mask supplies transparency rather than adding an opaque rectangle.
            if (!hasAlpha) return null;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                var alpha = bytes[i + 3];
                if (alpha == 0) bytes[i] = bytes[i + 1] = bytes[i + 2] = 0;
                else if (straightAlpha)
                {
                    bytes[i] = (byte)((bytes[i] * alpha + 127) / 255);
                    bytes[i + 1] = (byte)((bytes[i + 1] * alpha + 127) / 255);
                    bytes[i + 2] = (byte)((bytes[i + 2] * alpha + 127) / 255);
                }
            }
            return new(width, height, bytes);
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }

    private static IconPixels? FromAssociatedIcon(string path, bool isFolder, int size)
    {
        var info = new ShellFileInfo();
        var flags = 0x100u | (size <= 16 ? 0x1u : 0u); // SHGFI_ICON, optional SMALLICON.
        try
        {
            // First retain custom icons and shortcut overlays if the real item is available.
            var result = Native.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x20u);
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            {
                if (info.Icon != IntPtr.Zero) Native.DestroyIcon(info.Icon);
                info = new();
                var extension = isFolder ? "folder" : Path.GetExtension(path);
                if (string.IsNullOrEmpty(extension)) extension = "file";
                result = Native.SHGetFileInfo(extension, isFolder ? 0x10u : 0x80u, ref info,
                    (uint)Marshal.SizeOf<ShellFileInfo>(), flags | 0x10u); // USEFILEATTRIBUTES.
            }
            if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            return RenderIcon(info.Icon, size);
        }
        finally
        {
            if (info.Icon != IntPtr.Zero) Native.DestroyIcon(info.Icon);
        }
    }

    private static IconPixels? RenderIcon(IntPtr icon, int size)
    {
        // Rendering on black and white reconstructs both 32-bit alpha and legacy icon masks.
        // The black rendering already contains premultiplied color channels.
        var black = RenderIconBackground(icon, size, 0);
        var white = RenderIconBackground(icon, size, 255);
        if (black is null || white is null) return null;
        for (var i = 0; i < black.Length; i += 4)
        {
            var difference = Math.Max(white[i] - black[i], Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2]));
            var alpha = (byte)Math.Clamp(255 - difference, 0, 255);
            black[i] = Math.Min(black[i], alpha);
            black[i + 1] = Math.Min(black[i + 1], alpha);
            black[i + 2] = Math.Min(black[i + 2], alpha);
            black[i + 3] = alpha;
        }
        return new(size, size, black);
    }

    private static byte[]? RenderIconBackground(IntPtr icon, int size, byte background)
    {
        var dc = Native.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            var info = BitmapInfo.ForSize(size, size);
            bitmap = Native.CreateDIBSection(dc, ref info, 0, out var address, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || address == IntPtr.Zero) return null;
            previous = Native.SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return null;
            var bytes = new byte[checked(size * size * 4)];
            for (var i = 0; i < bytes.Length; i += 4)
            {
                bytes[i] = bytes[i + 1] = bytes[i + 2] = background;
                bytes[i + 3] = 255;
            }
            Marshal.Copy(bytes, 0, address, bytes.Length);
            if (!Native.DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, 0x3)) return null;
            Native.GdiFlush();
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
        }
    }

    private readonly record struct CacheKey(string Path, bool IsFolder, int Size);
    private sealed record CacheEntry(Task<IconPixels?> Pending, LinkedListNode<CacheKey> Node);
    private sealed record IconPixels(int Width, int Height, byte[] Bytes);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant, Colors;

        public static BitmapInfo ForSize(int width, int height) => new()
        {
            Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
            SizeImage = checked((uint)(width * height * 4))
        };
    }

    private static class Native
    {
        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        internal static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        internal static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap details);
        [DllImport("gdi32.dll")] internal static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint count,
            [Out] byte[] pixels, ref BitmapInfo info, uint usage);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
            uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DrawIconEx(IntPtr dc,
            int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    }
}
