using System.Buffers.Binary;
using System.Text;

namespace Cheatmaster.Core.Native.Linux;

/// <summary>What the header of a Windows module loaded in memory says about it.</summary>
internal readonly record struct PeInfo(ulong Base, uint SizeOfImage, bool Is64Bit, bool IsDll, string ExportName);

/// <summary>
/// Reads the header of a Windows program or library mapped into a process.
///
/// Under Wine a game's modules often do not appear by name in /proc/&lt;pid&gt;/maps at all: an
/// image whose sections are not page aligned is copied into anonymous memory rather than mapped
/// from its file. The header is still sitting at the start of the image, though, and it says
/// how large the image is and, for a library, what it is called.
/// </summary>
internal static class PeImage
{
    private const ushort MachineI386 = 0x014C;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort CharacteristicsDll = 0x2000;

    public delegate int Reader(ulong address, Span<byte> buffer);

    public static bool TryRead(Reader read, ulong imageBase, out PeInfo info)
    {
        info = default;

        Span<byte> header = stackalloc byte[0x400];
        if (read(imageBase, header) != header.Length) return false;
        if (header[0] != (byte)'M' || header[1] != (byte)'Z') return false;

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header[0x3C..]);
        if (peOffset <= 0 || peOffset > header.Length - 0x110) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[peOffset..]) != 0x00004550) return false; // "PE\0\0"

        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(header[(peOffset + 4)..]);
        if (machine is not (MachineI386 or MachineAmd64)) return false;
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header[(peOffset + 22)..]);

        int optional = peOffset + 24;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(header[optional..]);
        bool pe32Plus = magic == 0x20B;
        if (!pe32Plus && magic != 0x10B) return false;

        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(header[(optional + 56)..]);
        if (sizeOfImage == 0 || sizeOfImage > 0x4000_0000) return false;

        int exportEntry = optional + (pe32Plus ? 112 : 96);
        uint exportRva = BinaryPrimitives.ReadUInt32LittleEndian(header[exportEntry..]);

        info = new PeInfo(imageBase, sizeOfImage, machine == MachineAmd64, (characteristics & CharacteristicsDll) != 0,
            ReadExportName(read, imageBase, exportRva, sizeOfImage));
        return true;
    }

    private static string ReadExportName(Reader read, ulong imageBase, uint exportRva, uint sizeOfImage)
    {
        if (exportRva == 0 || exportRva >= sizeOfImage) return string.Empty;

        Span<byte> directory = stackalloc byte[40];
        if (read(imageBase + exportRva, directory) != directory.Length) return string.Empty;

        uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(directory[12..]);
        if (nameRva == 0 || nameRva >= sizeOfImage) return string.Empty;

        Span<byte> name = stackalloc byte[128];
        int got = read(imageBase + nameRva, name);
        if (got <= 0) return string.Empty;

        int length = name[..got].IndexOf((byte)0);
        if (length <= 0) return string.Empty;

        foreach (byte b in name[..length])
        {
            if (b < 0x20 || b > 0x7E) return string.Empty;
        }
        return Encoding.ASCII.GetString(name[..length]);
    }
}

/// <summary>Recognises a Wine process and translates the Windows paths it reports.</summary>
internal static class Wine
{
    public static bool IsWineProcess(string[] cmdline, IReadOnlyList<MapLine> maps)
    {
        if (cmdline.Length > 0 && IsWindowsProgram(cmdline[0])) return true;
        foreach (var map in maps)
        {
            if (map.Path.EndsWith("/ntdll.so", StringComparison.Ordinal) ||
                map.Path.EndsWith("-preloader", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsWindowsProgram(string argument) =>
        argument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file name at the end of a Windows or Unix path.</summary>
    public static string FileName(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// Where a Windows path the process reports actually lives on this machine, or empty. Z: is
    /// the root of the Unix file system by Wine's default, and every other drive is a link in
    /// the prefix's dosdevices folder.
    /// </summary>
    public static string ToUnixPath(int pid, string windowsPath)
    {
        if (windowsPath.StartsWith('/')) return windowsPath;
        if (windowsPath.Length < 3 || windowsPath[1] != ':' || windowsPath[2] is not ('\\' or '/')) return string.Empty;

        string rest = windowsPath[3..].Replace('\\', '/');
        char drive = char.ToLowerInvariant(windowsPath[0]);

        string? prefix = ProcFs.ReadEnvironment(pid, "WINEPREFIX");
        if (string.IsNullOrEmpty(prefix))
        {
            string? compat = ProcFs.ReadEnvironment(pid, "STEAM_COMPAT_DATA_PATH");
            if (!string.IsNullOrEmpty(compat)) prefix = Path.Combine(compat, "pfx");
        }
        if (string.IsNullOrEmpty(prefix))
        {
            string? home = ProcFs.ReadEnvironment(pid, "HOME") ?? Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home)) prefix = Path.Combine(home, ".wine");
        }

        if (!string.IsNullOrEmpty(prefix))
        {
            try
            {
                // Resolve the drive link itself, so the path names the real folder: the library
                // reads storefront files next to the game, and Steam's are found by walking up.
                var link = new DirectoryInfo(Path.Combine(prefix, "dosdevices", drive + ":"));
                string root = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? link.FullName;
                string candidate = Path.GetFullPath(Path.Combine(root, rest));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall through to the Z: default.
            }
        }

        return drive == 'z' && File.Exists("/" + rest) ? "/" + rest : string.Empty;
    }
}
