using Avalonia.Media.Imaging;

namespace Cheatmaster.App.Infrastructure;

public static class ImageLoader
{
    /// <summary>
    /// Decodes a picture from disk without holding the file open, so replacing a cover on disk
    /// shows the new one. Safe to call off the UI thread, which is where it belongs: decoding a
    /// cover is real work, and the library grid asks for dozens at once.
    /// </summary>
    public static Bitmap? Load(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
