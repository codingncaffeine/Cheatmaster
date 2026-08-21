using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Cheatmaster.Core.Native;

/// <summary>
/// SeDebugPrivilege is what lets the tool open handles to processes it does not own.
/// Without it, OpenProcess against many targets fails with ACCESS_DENIED even for an
/// administrator account.
/// </summary>
public static class Privileges
{
    private static bool _attempted;
    private static bool _enabled;

    public static bool DebugPrivilegeEnabled => _enabled;

    public static bool EnableDebugPrivilege()
    {
        if (_attempted) return _enabled;
        _attempted = true;

        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TokenAdjustPrivileges | Win32.TokenQuery, out nint token))
            return false;

        try
        {
            if (!Win32.LookupPrivilegeValue(null, "SeDebugPrivilege", out long luid))
                return false;

            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = Win32.SePrivilegeEnabled };
            if (!Win32.AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0))
                return false;

            // AdjustTokenPrivileges returns TRUE even when it only partially applied the set,
            // so the real answer is in the last error code.
            _enabled = Marshal.GetLastPInvokeError() == 0;
            return _enabled;
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
