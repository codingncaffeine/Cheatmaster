using System.Runtime.InteropServices;
using System.Text;

namespace Cheatmaster.Core.Native.Linux;

/// <summary>
/// Window titles by process, from the X server's list of managed windows.
///
/// Linux has no system-wide "main window title" for a process. X11 has the next best thing: the
/// window manager publishes every top-level window on the root, and each window names the pid
/// that owns it. That covers native X11 sessions and, on Wayland, everything under XWayland —
/// which includes games run through Wine and Proton. Native Wayland windows are invisible here,
/// and the picker simply ranks those by size instead.
/// </summary>
internal static partial class X11Windows
{
    private const string Library = "libX11.so.6";

    public static Dictionary<int, string> TitlesByPid()
    {
        var titles = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return titles;

        try
        {
            Collect(titles);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No X client library on this machine: no titles, nothing else lost.
        }
        return titles;
    }

    private static unsafe void Collect(Dictionary<int, string> titles)
    {
        nint display = XOpenDisplay(0);
        if (display == 0) return;

        try
        {
            nint root = XDefaultRootWindow(display);
            nint clientList = XInternAtom(display, "_NET_CLIENT_LIST", 0);
            nint pidAtom = XInternAtom(display, "_NET_WM_PID", 0);
            nint nameAtom = XInternAtom(display, "_NET_WM_NAME", 0);
            nint utf8 = XInternAtom(display, "UTF8_STRING", 0);

            if (!GetProperty(display, root, clientList, out nint data, out ulong count, out int format) || format != 32)
                return;

            try
            {
                for (ulong i = 0; i < count; i++)
                {
                    // Format-32 properties come back as one C long per item.
                    nint window = ((nint*)data)[i];

                    if (!GetProperty(display, window, pidAtom, out nint pidData, out ulong pidCount, out _)) continue;
                    int pid = pidCount > 0 ? (int)((nint*)pidData)[0] : 0;
                    XFree(pidData);
                    if (pid <= 0 || titles.ContainsKey(pid)) continue;

                    string title = ReadText(display, window, nameAtom, utf8);
                    if (title.Length > 0) titles[pid] = title;
                }
            }
            finally
            {
                XFree(data);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static unsafe string ReadText(nint display, nint window, nint atom, nint type)
    {
        if (XGetWindowProperty(display, window, atom, 0, 1024, 0, type, out _, out int format, out ulong count, out _, out nint data) != 0)
            return string.Empty;
        try
        {
            if (data == 0 || format != 8 || count == 0) return string.Empty;
            return Encoding.UTF8.GetString((byte*)data, (int)count).Trim();
        }
        finally
        {
            if (data != 0) XFree(data);
        }
    }

    private static bool GetProperty(nint display, nint window, nint atom, out nint data, out ulong count, out int format)
    {
        int status = XGetWindowProperty(display, window, atom, 0, 65536, 0, 0 /* AnyPropertyType */,
            out _, out format, out count, out _, out data);
        if (status == 0 && data != 0) return true;
        if (data != 0) XFree(data);
        data = 0;
        return false;
    }

    [LibraryImport(Library)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(Library)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Library)]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(Library)]
    private static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length,
        int delete, nint requestedType, out nint actualType, out int actualFormat, out ulong itemCount,
        out ulong bytesAfter, out nint data);

    [LibraryImport(Library)]
    private static partial int XFree(nint data);
}
