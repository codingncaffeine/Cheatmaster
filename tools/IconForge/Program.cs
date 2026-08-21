using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Builds the application icon from the source art.
//
//   dotnet run --project tools/IconForge -- assets/logo1.png src/Cheatmaster.App/Assets
//
// Frames below 256 are written as uncompressed DIB. PNG-compressed frames inside an .ico
// are only reliably rendered at 256, and using PNG for the small sizes is the classic
// blank-taskbar-icon bug. The writer then checks that the last frame ends exactly at the
// end of the file, because a short final frame makes the whole icon fail to load while
// MSBuild still reports a clean build.

string sourcePath = args.Length > 0 ? args[0] : "assets/logo1.png";
string outputDirectory = args.Length > 1 ? args[1] : "src/Cheatmaster.App/Assets";

sourcePath = Path.GetFullPath(sourcePath);
outputDirectory = Path.GetFullPath(outputDirectory);
Directory.CreateDirectory(outputDirectory);

string iconPath = Path.Combine(outputDirectory, "cheatmaster.ico");
string logoPath = Path.Combine(outputDirectory, "logo.png");

var source = BitmapFrame.Create(new Uri(sourcePath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
Console.WriteLine($"source  {Path.GetFileName(sourcePath)}  {source.PixelWidth}x{source.PixelHeight}");

int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var payloads = new List<byte[]>();
foreach (int size in sizes)
{
    var rendered = Render(source, size);
    payloads.Add(size >= 256 ? EncodePng(rendered) : EncodeDib(rendered, size));
}

WriteIcon(iconPath, sizes, payloads);
File.WriteAllBytes(logoPath, EncodePng(Render(source, 256)));

Validate(iconPath, sizes);

Console.WriteLine($"icon    {iconPath}  ({new FileInfo(iconPath).Length:N0} bytes, {sizes.Length} frames)");
Console.WriteLine($"logo    {logoPath}  ({new FileInfo(logoPath).Length:N0} bytes)");

// -------------------------------------------------------------------------------------

static BitmapSource Render(BitmapSource source, int size)
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

    using (var context = visual.RenderOpen())
    {
        // The art is not quite square, so fit it inside the frame rather than stretching it.
        double scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        double width = source.PixelWidth * scale;
        double height = source.PixelHeight * scale;
        context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
    }

    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    target.Freeze();
    return target;
}

static byte[] EncodePng(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static byte[] EncodeDib(BitmapSource bitmap, int size)
{
    // Straight (non-premultiplied) BGRA is what a 32bpp DIB expects.
    var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
    int stride = size * 4;
    byte[] pixels = new byte[stride * size];
    converted.CopyPixels(pixels, stride, 0);

    int maskStride = (size + 31) / 32 * 4;
    int imageBytes = stride * size;
    int maskBytes = maskStride * size;

    using var stream = new MemoryStream(40 + imageBytes + maskBytes);
    using var writer = new BinaryWriter(stream);

    writer.Write(40);                       // biSize
    writer.Write(size);                     // biWidth
    writer.Write(size * 2);                 // biHeight: image plus the AND mask
    writer.Write((short)1);                 // biPlanes
    writer.Write((short)32);                // biBitCount
    writer.Write(0);                        // biCompression: BI_RGB
    writer.Write(imageBytes + maskBytes);   // biSizeImage
    writer.Write(0);                        // biXPelsPerMeter
    writer.Write(0);                        // biYPelsPerMeter
    writer.Write(0);                        // biClrUsed
    writer.Write(0);                        // biClrImportant

    // DIB rows run bottom to top.
    for (int y = size - 1; y >= 0; y--)
        writer.Write(pixels, y * stride, stride);

    // A 32bpp icon is masked by its alpha channel, so the AND mask stays clear. It still has
    // to be present and exactly the declared size.
    writer.Write(new byte[maskBytes]);

    writer.Flush();
    return stream.ToArray();
}

static void WriteIcon(string path, int[] sizes, List<byte[]> payloads)
{
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);              // reserved
    writer.Write((ushort)1);              // type: icon
    writer.Write((ushort)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        writer.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);            // palette entries
        writer.Write((byte)0);            // reserved
        writer.Write((ushort)1);          // colour planes
        writer.Write((ushort)32);         // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }

    foreach (byte[] payload in payloads)
        writer.Write(payload);

    writer.Flush();
}

static void Validate(string path, int[] sizes)
{
    byte[] bytes = File.ReadAllBytes(path);
    int count = BitConverter.ToUInt16(bytes, 4);
    if (count != sizes.Length) throw new InvalidOperationException($"frame count {count} != {sizes.Length}");

    int end = 0;
    for (int i = 0; i < count; i++)
    {
        int entry = 6 + 16 * i;
        int length = BitConverter.ToInt32(bytes, entry + 8);
        int offset = BitConverter.ToInt32(bytes, entry + 12);
        if (offset + length > bytes.Length)
            throw new InvalidOperationException($"frame {i} runs past the end of the file");
        end = Math.Max(end, offset + length);
    }

    // A short final frame is the failure that makes an icon load as nothing at all.
    if (end != bytes.Length)
        throw new InvalidOperationException($"frame data ends at {end} but the file is {bytes.Length} bytes");

    var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    var decoded = new List<int>();
    foreach (var frame in decoder.Frames) decoded.Add(frame.PixelWidth);
    decoded.Sort();

    foreach (int size in sizes)
    {
        if (!decoded.Contains(size))
            throw new InvalidOperationException($"the {size}px frame did not decode");
    }

    Console.WriteLine($"verify  {count} frames, {bytes.Length:N0} bytes, decoded [{string.Join(", ", decoded)}]");
}
