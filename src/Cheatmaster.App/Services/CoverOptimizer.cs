using SkiaSharp;

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

    /// <summary>Far beyond any cover; a picture claiming more is built to exhaust memory when decoded.</summary>
    private const long MaxPixels = 40_000_000;

    /// <summary>
    /// Returns the re-encoded image, or the original bytes if re-encoding does not help. Returns an
    /// empty array for something that is not a picture, or one too large to decode safely, so it is
    /// never saved.
    /// </summary>
    public static byte[] Shrink(byte[] original)
    {
        if (original.Length == 0) return original;

        try
        {
            // The header says how big the picture is before a single pixel is decoded.
            using (var codec = SKCodec.Create(new SKMemoryStream(original)))
            {
                if (codec is null || (long)codec.Info.Width * codec.Info.Height > MaxPixels) return [];
            }

            using var decoded = SKBitmap.Decode(original);
            if (decoded is null) return original;

            double scale = Math.Min(1.0, Math.Min((double)MaxWidth / decoded.Width, (double)MaxHeight / decoded.Height));
            SKBitmap source = decoded;
            SKBitmap? scaled = null;
            if (scale < 1.0)
            {
                var size = new SKImageInfo(Math.Max(1, (int)Math.Round(decoded.Width * scale)),
                    Math.Max(1, (int)Math.Round(decoded.Height * scale)));
                scaled = decoded.Resize(size, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (scaled is not null) source = scaled;
            }

            using (scaled)
            using (var image = SKImage.FromBitmap(source))
            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality))
            {
                byte[] shrunk = data?.ToArray() ?? [];
                // Re-encoding a small or already-tight image can make it bigger.
                return shrunk.Length > 0 && shrunk.Length < original.Length ? shrunk : original;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            return original;
        }
    }
}
