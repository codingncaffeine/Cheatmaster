using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cheatmaster.App.Services;

/// <summary>
/// Shrinks a downloaded cover to the size the app actually draws it at.
///
/// Store covers arrive at 600x900 and around 50 KB. The grid draws them at 148x222 and the
/// decoder is asked for 300px, so everything past that is bytes on disk and, for a hand-set
/// cover, bytes in the backup repository forever — git keeps every version it is ever given.
/// </summary>
public static class CoverOptimizer
{
    private const int MaxWidth = 300;
    private const int MaxHeight = 450;
    private const int Quality = 80;

    /// <summary>Returns the re-encoded image, or the original bytes if anything goes wrong.</summary>
    public static byte[] Shrink(byte[] original)
    {
        if (original.Length == 0) return original;

        try
        {
            using var input = new MemoryStream(original, writable: false);
            var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

            double scale = Math.Min(1.0, Math.Min(
                (double)MaxWidth / frame.PixelWidth,
                (double)MaxHeight / frame.PixelHeight));

            BitmapSource source = frame;
            if (scale < 1.0)
            {
                var scaled = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
                scaled.Freeze();
                source = scaled;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Quality };
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var output = new MemoryStream();
            encoder.Save(output);
            byte[] shrunk = output.ToArray();

            // Re-encoding a small or already-tight image can make it bigger.
            return shrunk.Length > 0 && shrunk.Length < original.Length ? shrunk : original;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or OverflowException)
        {
            return original;
        }
    }
}
