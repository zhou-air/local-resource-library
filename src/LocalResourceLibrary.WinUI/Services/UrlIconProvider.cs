using System.Runtime.InteropServices.WindowsRuntime;
using LocalResourceLibrary.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LocalResourceLibrary.WinUI.Services;

/// <summary>Decodes the stored website image without making network or Shell requests.</summary>
public sealed class UrlIconProvider
{
    /// <summary>Call on the WinUI UI thread; corrupt or unsupported images return null.</summary>
    public async Task<ImageSource?> GetAsync(byte[]? favicon, int size = 48)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is null || !dispatcher.HasThreadAccess || favicon == null || !ResourceUrls.IsSupportedFavicon(favicon)) return null;
        try
        {
            // BitmapImage must be created and started on its owning UI thread. SetSourceAsync
            // lets the native decoder work asynchronously while the resource list stays usable.
            var image = new BitmapImage { DecodePixelWidth = Math.Clamp(size, 16, 256) };
            using var bytes = new MemoryStream(favicon, writable: false);
            using var stream = bytes.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
