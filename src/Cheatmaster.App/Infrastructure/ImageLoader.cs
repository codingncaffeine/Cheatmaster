using System.IO;
using System.Windows.Media.Imaging;

namespace Cheatmaster.App.Infrastructure;

public static class ImageLoader
{
    /// <summary>
    /// Loads a bitmap without holding the file open and without going through WPF's image cache,
    /// so replacing a cover on disk actually shows the new one.
    ///
    /// Deliberately does NOT set BitmapCreateOptions.IgnoreImageCache. That flag exists to defeat
    /// the cache WPF keys by URI, and setting it alongside a StreamSource throws
    /// ArgumentNullException("key") because there is no URI to key on — which reads as "the file
    /// simply would not load". Reading through a stream bypasses that cache anyway.
    /// </summary>
    public static BitmapImage? Load(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
