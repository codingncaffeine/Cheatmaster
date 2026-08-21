using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Cheatmaster.App.Services;

/// <summary>
/// System-wide hotkeys, so a cheat can be toggled without leaving the game. Registration is
/// global, which means another application may already own a combination; that is reported
/// rather than swallowed.
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    private readonly Dictionary<int, string> _idToKey = [];
    private readonly Dictionary<string, int> _keyToId = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _source;
    private int _nextId = 0xC000;

    public event Action<string>? Pressed;

    public void Attach(HwndSource source)
    {
        _source = source;
        source.AddHook(Hook);
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey) return 0;
        if (_idToKey.TryGetValue((int)wParam, out string? key))
        {
            handled = true;
            Pressed?.Invoke(key);
        }
        return 0;
    }

    public bool Register(string combination)
    {
        if (_source is null || string.IsNullOrWhiteSpace(combination)) return false;
        if (_keyToId.ContainsKey(combination)) return true;
        if (!TryParse(combination, out uint modifiers, out uint virtualKey)) return false;

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | 0x4000u /* MOD_NOREPEAT */, virtualKey))
            return false;

        _idToKey[id] = combination;
        _keyToId[combination] = id;
        return true;
    }

    public void Unregister(string combination)
    {
        if (_source is null) return;
        if (!_keyToId.Remove(combination, out int id)) return;
        _idToKey.Remove(id);
        UnregisterHotKey(_source.Handle, id);
    }

    public void UnregisterAll()
    {
        if (_source is null) return;
        foreach (int id in _idToKey.Keys) UnregisterHotKey(_source.Handle, id);
        _idToKey.Clear();
        _keyToId.Clear();
    }

    /// <summary>Re-registers exactly the given set, leaving already-registered keys alone.</summary>
    public IReadOnlyList<string> Sync(IEnumerable<string> wanted)
    {
        var target = new HashSet<string>(wanted.Where(static k => !string.IsNullOrWhiteSpace(k)), StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();

        foreach (string existing in _keyToId.Keys.ToList())
        {
            if (!target.Contains(existing)) Unregister(existing);
        }

        foreach (string key in target)
        {
            if (!Register(key)) failed.Add(key);
        }

        return failed;
    }

    public static bool TryParse(string combination, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        string[] parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= 0x0002; break;
                case "ALT": modifiers |= 0x0001; break;
                case "SHIFT": modifiers |= 0x0004; break;
                case "WIN": modifiers |= 0x0008; break;
                default: return false;
            }
        }

        string last = parts[^1];
        if (!Enum.TryParse(last, ignoreCase: true, out Key key)) return false;

        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(Hook);
        _source = null;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);
}
