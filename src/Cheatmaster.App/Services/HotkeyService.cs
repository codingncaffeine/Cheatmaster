using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace Cheatmaster.App.Services;

/// <summary>A parsed combination such as Ctrl+Shift+F1.</summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, Key Key);

/// <summary>
/// System-wide hotkeys, so a cheat can be toggled without leaving the game. Registration is
/// global, which means another application may already own a combination; that is reported
/// rather than swallowed.
///
/// Each platform's mechanism lives on a thread of its own, so registering never waits on the UI
/// thread and a key press is handed back to it: RegisterHotKey on Windows, and on Linux a key
/// grab on the X server, which covers X11 sessions and, under Wayland, every game running through
/// XWayland — Wine and Proton games among them.
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private readonly IBackend? _backend;

    public event Action<string>? Pressed;

    public HotkeyService()
    {
        void Raise(string combination) => Dispatcher.UIThread.Post(() => Pressed?.Invoke(combination));

        if (OperatingSystem.IsWindows()) _backend = new WindowsBackend(Raise);
        else if (OperatingSystem.IsLinux()) _backend = new X11Backend(Raise);
    }

    /// <summary>False when this desktop offers no way to listen for keys system-wide.</summary>
    public bool IsAvailable => _backend?.IsAvailable ?? false;

    /// <summary>Registers exactly the given set. Returns the combinations that could not be registered.</summary>
    public async Task<IReadOnlyList<string>> SyncAsync(IEnumerable<string> wanted)
    {
        var chords = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);
        var failed = new List<string>();
        foreach (string text in wanted)
        {
            if (string.IsNullOrWhiteSpace(text) || chords.ContainsKey(text)) continue;
            if (TryParse(text, out var chord)) chords[text] = chord;
            else failed.Add(text);
        }

        if (_backend is null)
        {
            failed.AddRange(chords.Keys);
            return failed;
        }

        failed.AddRange(await _backend.SyncAsync(chords));
        return failed;
    }

    public static bool TryParse(string combination, out KeyChord chord)
    {
        chord = default;
        string[] parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = KeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": modifiers |= KeyModifiers.Control; break;
                case "ALT": modifiers |= KeyModifiers.Alt; break;
                case "SHIFT": modifiers |= KeyModifiers.Shift; break;
                case "WIN" or "SUPER" or "META": modifiers |= KeyModifiers.Meta; break;
                default: return false;
            }
        }

        string last = parts[^1];
        // A bare digit is what people type; the key behind it is D0-D9.
        if (last.Length == 1 && char.IsAsciiDigit(last[0])) last = "D" + last;
        if (!Enum.TryParse(last, ignoreCase: true, out Key key) || key == Key.None) return false;
        if (VirtualKey(key) == 0) return false;

        chord = new KeyChord(modifiers, key);
        return true;
    }

    public static string Describe(KeyModifiers modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>The Windows virtual-key code for a key; 0 for keys a hotkey cannot use.</summary>
    internal static uint VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('A' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
        Key.Multiply => 0x6A, Key.Add => 0x6B, Key.Subtract => 0x6D, Key.Decimal => 0x6E, Key.Divide => 0x6F,
        Key.Space => 0x20, Key.Enter => 0x0D, Key.Tab => 0x09, Key.Escape => 0x1B, Key.Back => 0x08,
        Key.PageUp => 0x21, Key.PageDown => 0x22, Key.End => 0x23, Key.Home => 0x24,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Pause => 0x13, Key.Scroll => 0x91,
        Key.OemPlus => 0xBB, Key.OemComma => 0xBC, Key.OemMinus => 0xBD, Key.OemPeriod => 0xBE,
        Key.OemTilde => 0xC0, Key.OemOpenBrackets => 0xDB, Key.OemCloseBrackets => 0xDD,
        Key.OemSemicolon => 0xBA, Key.OemQuotes => 0xDE, Key.OemQuestion => 0xBF, Key.OemPipe => 0xDC,
        _ => 0
    };

    /// <summary>The X11 keysym for a key; 0 for keys a hotkey cannot use.</summary>
    internal static uint KeySym(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0xFFB0 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0xFFBE + (key - Key.F1)),
        Key.Multiply => 0xFFAA, Key.Add => 0xFFAB, Key.Subtract => 0xFFAD, Key.Decimal => 0xFFAE, Key.Divide => 0xFFAF,
        Key.Space => 0x20, Key.Enter => 0xFF0D, Key.Tab => 0xFF09, Key.Escape => 0xFF1B, Key.Back => 0xFF08,
        Key.PageUp => 0xFF55, Key.PageDown => 0xFF56, Key.End => 0xFF57, Key.Home => 0xFF50,
        Key.Left => 0xFF51, Key.Up => 0xFF52, Key.Right => 0xFF53, Key.Down => 0xFF54,
        Key.Insert => 0xFF63, Key.Delete => 0xFFFF, Key.Pause => 0xFF13, Key.Scroll => 0xFF14,
        Key.OemPlus => 0x3D, Key.OemComma => 0x2C, Key.OemMinus => 0x2D, Key.OemPeriod => 0x2E,
        Key.OemTilde => 0x60, Key.OemOpenBrackets => 0x5B, Key.OemCloseBrackets => 0x5D,
        Key.OemSemicolon => 0x3B, Key.OemQuotes => 0x27, Key.OemQuestion => 0x2F, Key.OemPipe => 0x5C,
        _ => 0
    };

    public void Dispose() => _backend?.Dispose();

    private interface IBackend : IDisposable
    {
        bool IsAvailable { get; }

        /// <summary>Makes the registered set exactly these; completes with the ones refused.</summary>
        Task<List<string>> SyncAsync(Dictionary<string, KeyChord> wanted);
    }

    /// <summary>
    /// A thread that owns the registrations and runs a loop; requests reach it through a queue,
    /// and <see cref="Wake"/> gets its attention.
    /// </summary>
    private abstract class LoopBackend : IBackend
    {
        private readonly ConcurrentQueue<(Dictionary<string, KeyChord> Wanted, TaskCompletionSource<List<string>> Done)> _requests = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private Thread? _thread;
        protected readonly Action<string> Raise;
        protected volatile bool Stopping;

        protected LoopBackend(Action<string> raise) => Raise = raise;

        /// <summary>Called by the derived constructor once its own fields are set.</summary>
        protected void Start(string name)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    Run(_ready);
                }
                finally
                {
                    _ready.Set();
                    while (_requests.TryDequeue(out var request)) request.Done.TrySetResult([.. request.Wanted.Keys]);
                }
            })
            { IsBackground = true, Name = name };
            _thread.Start();
            _ready.Wait(3000);
        }

        public abstract bool IsAvailable { get; }

        public Task<List<string>> SyncAsync(Dictionary<string, KeyChord> wanted)
        {
            var done = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!IsAvailable)
            {
                done.SetResult([.. wanted.Keys]);
                return done.Task;
            }
            _requests.Enqueue((wanted, done));
            Wake();
            return done.Task;
        }

        /// <summary>Runs on the backend thread: sets up, signals ready, loops until stopping.</summary>
        protected abstract void Run(ManualResetEventSlim ready);

        protected abstract void Wake();

        protected void DrainRequests(Func<Dictionary<string, KeyChord>, List<string>> apply)
        {
            while (_requests.TryDequeue(out var request))
            {
                try
                {
                    request.Done.TrySetResult(apply(request.Wanted));
                }
                catch (Exception ex)
                {
                    request.Done.TrySetException(ex);
                }
            }
        }

        public void Dispose()
        {
            Stopping = true;
            Wake();
            _thread?.Join(2000);
        }
    }

    // ------------------------------------------------------------------ Windows

    private sealed partial class WindowsBackend : LoopBackend
    {
        private const uint WmHotkey = 0x0312;
        private const uint WmApp = 0x8000;

        private volatile uint _threadId;
        private readonly Dictionary<int, string> _idToKey = [];
        private readonly Dictionary<string, int> _keyToId = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId = 0xC000;

        public WindowsBackend(Action<string> raise) : base(raise) => Start("Cheatmaster hotkeys");

        public override bool IsAvailable => _threadId != 0;

        protected override unsafe void Run(ManualResetEventSlim ready)
        {
            // MSG on x64: hwnd(8) message(4)+pad(4) wParam(8) lParam(8) time(4) pt(8) private(4).
            byte* msg = stackalloc byte[48];
            // A thread only has a message queue once it has asked for a message; this creates it.
            PeekMessage(msg, 0, 0, 0, 0);
            _threadId = GetCurrentThreadId();
            ready.Set();

            while (!Stopping && GetMessage(msg, 0, 0, 0) > 0)
            {
                uint message = *(uint*)(msg + 8);
                if (message == WmHotkey && _idToKey.TryGetValue((int)*(nint*)(msg + 16), out string? key))
                    Raise(key);
                else if (message == WmApp)
                    DrainRequests(Apply);
            }

            foreach (int id in _idToKey.Keys) UnregisterHotKey(0, id);
        }

        private List<string> Apply(Dictionary<string, KeyChord> wanted)
        {
            foreach (string existing in _keyToId.Keys.ToList())
            {
                if (wanted.ContainsKey(existing)) continue;
                _keyToId.Remove(existing, out int id);
                _idToKey.Remove(id);
                UnregisterHotKey(0, id);
            }

            var failed = new List<string>();
            foreach (var (text, chord) in wanted)
            {
                if (_keyToId.ContainsKey(text)) continue;

                uint modifiers = 0x4000; // MOD_NOREPEAT
                if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= 0x0001;
                if (chord.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= 0x0002;
                if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= 0x0004;
                if (chord.Modifiers.HasFlag(KeyModifiers.Meta)) modifiers |= 0x0008;

                int id = _nextId++;
                if (!RegisterHotKey(0, id, modifiers, VirtualKey(chord.Key)))
                {
                    failed.Add(text);
                    continue;
                }
                _idToKey[id] = text;
                _keyToId[text] = id;
            }
            return failed;
        }

        protected override void Wake()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WmApp, 0, 0);
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(nint hWnd, int id);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        private static unsafe partial int GetMessage(byte* msg, nint hWnd, uint min, uint max);

        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        private static unsafe partial int PeekMessage(byte* msg, nint hWnd, uint min, uint max, uint remove);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();
    }

    // ------------------------------------------------------------------ X11

    private sealed unsafe partial class X11Backend : LoopBackend
    {
        private const string X11 = "libX11.so.6";
        private const int KeyPress = 2;
        private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 0x10, Mod4Mask = 0x40;

        /// <summary>Caps Lock and Num Lock must not stop a hotkey working, so each grab is made under all four.</summary>
        private static readonly uint[] IgnoredCombinations = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

        [ThreadStatic] private static bool t_grabFailed;
        private static nint s_ourDisplay;
        private static nint s_previousHandler;

        private volatile bool _available;
        private nint _display;
        private int _wakeRead = -1, _wakeWrite = -1;
        private readonly Dictionary<string, (uint Keycode, uint Mask)> _grabs = new(StringComparer.OrdinalIgnoreCase);

        public X11Backend(Action<string> raise) : base(raise) => Start("Cheatmaster hotkeys");

        public override bool IsAvailable => _available;

        protected override void Run(ManualResetEventSlim ready)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;

            try
            {
                _display = XOpenDisplay(0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return;
            }

            int* pipe = stackalloc int[2];
            if (_display == 0) return;
            if (Pipe(pipe) != 0)
            {
                XCloseDisplay(_display);
                return;
            }
            _wakeRead = pipe[0];
            _wakeWrite = pipe[1];

            // A grab another program already holds is reported as an asynchronous X error, which by
            // default ends the process. This handler only records it.
            s_ourDisplay = _display;
            s_previousHandler = XSetErrorHandler(&OnXError);
            nint root = XDefaultRootWindow(_display);
            int xfd = XConnectionNumber(_display);
            _available = true;
            ready.Set();

            byte* ev = stackalloc byte[192];
            byte* drain = stackalloc byte[64];
            // Two pollfd structs: { int fd; short events; short revents; }, 8 bytes each.
            byte* fds = stackalloc byte[16];
            while (!Stopping)
            {
                *(int*)fds = xfd; *(short*)(fds + 4) = 1; *(short*)(fds + 6) = 0;
                *(int*)(fds + 8) = _wakeRead; *(short*)(fds + 12) = 1; *(short*)(fds + 14) = 0;
                Poll(fds, 2, 250);

                if ((*(short*)(fds + 14) & 1) != 0)
                {
                    Read(_wakeRead, drain, 64);
                    DrainRequests(wanted => Apply(root, wanted));
                }

                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, ev);
                    if (*(int*)ev != KeyPress) continue;
                    uint state = *(uint*)(ev + 80) & ~(LockMask | Mod2Mask);
                    uint keycode = *(uint*)(ev + 84);
                    foreach (var (text, grab) in _grabs)
                    {
                        if (grab.Keycode == keycode && grab.Mask == state) Raise(text);
                    }
                }
            }

            _available = false;
            foreach (var grab in _grabs.Values) Ungrab(root, grab);
            XCloseDisplay(_display);
            Close(_wakeRead);
            Close(_wakeWrite);
            _wakeWrite = -1;
        }

        private List<string> Apply(nint root, Dictionary<string, KeyChord> wanted)
        {
            foreach (string existing in _grabs.Keys.ToList())
            {
                if (wanted.ContainsKey(existing)) continue;
                Ungrab(root, _grabs[existing]);
                _grabs.Remove(existing);
            }

            var failed = new List<string>();
            foreach (var (text, chord) in wanted)
            {
                if (_grabs.ContainsKey(text)) continue;

                uint keycode = XKeysymToKeycode(_display, (nint)KeySym(chord.Key));
                if (keycode == 0)
                {
                    failed.Add(text);
                    continue;
                }

                uint mask = 0;
                if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) mask |= ShiftMask;
                if (chord.Modifiers.HasFlag(KeyModifiers.Control)) mask |= ControlMask;
                if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) mask |= Mod1Mask;
                if (chord.Modifiers.HasFlag(KeyModifiers.Meta)) mask |= Mod4Mask;

                t_grabFailed = false;
                foreach (uint extra in IgnoredCombinations)
                    XGrabKey(_display, (int)keycode, mask | extra, root, 0, 1, 1);
                XSync(_display, 0);

                if (t_grabFailed)
                {
                    Ungrab(root, (keycode, mask));
                    XSync(_display, 0);
                    failed.Add(text);
                    continue;
                }
                _grabs[text] = (keycode, mask);
            }
            XFlush(_display);
            return failed;
        }

        private void Ungrab(nint root, (uint Keycode, uint Mask) grab)
        {
            foreach (uint extra in IgnoredCombinations)
                XUngrabKey(_display, (int)grab.Keycode, grab.Mask | extra, root);
        }

        protected override void Wake()
        {
            if (_wakeWrite < 0) return;
            byte one = 1;
            Write(_wakeWrite, &one, 1);
        }

        /// <summary>
        /// The handler is process-wide and the UI toolkit installs one of its own, so an error on any
        /// connection but ours goes on to the handler that was there before.
        /// </summary>
        [UnmanagedCallersOnly]
        private static int OnXError(nint display, nint error)
        {
            if (display == s_ourDisplay)
            {
                t_grabFailed = true;
                return 0;
            }
            return s_previousHandler != 0 ? ((delegate* unmanaged<nint, nint, int>)s_previousHandler)(display, error) : 0;
        }

        [LibraryImport(X11)] private static partial nint XOpenDisplay(nint name);
        [LibraryImport(X11)] private static partial int XCloseDisplay(nint display);
        [LibraryImport(X11)] private static partial nint XDefaultRootWindow(nint display);
        [LibraryImport(X11)] private static partial int XConnectionNumber(nint display);
        [LibraryImport(X11)] private static partial uint XKeysymToKeycode(nint display, nint keysym);
        [LibraryImport(X11)] private static partial int XGrabKey(nint display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
        [LibraryImport(X11)] private static partial int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
        [LibraryImport(X11)] private static partial int XSync(nint display, int discard);
        [LibraryImport(X11)] private static partial int XFlush(nint display);
        [LibraryImport(X11)] private static partial int XPending(nint display);
        [LibraryImport(X11)] private static partial int XNextEvent(nint display, byte* ev);
        [LibraryImport(X11)] private static partial nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);

        [LibraryImport("libc", EntryPoint = "pipe")] private static partial int Pipe(int* fds);
        [LibraryImport("libc", EntryPoint = "poll")] private static partial int Poll(byte* fds, nuint count, int timeout);
        [LibraryImport("libc", EntryPoint = "read")] private static partial nint Read(int fd, byte* buffer, nuint count);
        [LibraryImport("libc", EntryPoint = "write")] private static partial nint Write(int fd, byte* buffer, nuint count);
        [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int fd);
    }
}
