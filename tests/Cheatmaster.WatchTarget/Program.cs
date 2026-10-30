using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cheatmaster.WatchTarget;

/// <summary>
/// A stand-in for a game, so the watchpoint tests have a real process to attach to.
///
/// A test cannot debug itself: Windows freezes the whole process while a debug event is pending,
/// including the thread that would have to handle it. The thing being watched therefore has to
/// live in a separate process, and this is the smallest one that behaves like a game — one number
/// at a fixed address, written and read through a base pointer, over and over.
/// </summary>
internal static class Program
{
    /// <summary>How far into the object the field sits. The tests expect to recover exactly this.</summary>
    private const int FieldOffset = 0x18;

    private const int BlockSize = 64;

    /// <summary>Bounded, so a test that dies without cleaning up cannot leave this running.</summary>
    private const int RunMilliseconds = 60_000;

    private static unsafe int Main(string[] args)
    {
        // "hold N": a value that only ever changes from outside, the way a score sits still until
        // the player does something. The UI tests search for it, then write and freeze it.
        bool hold = args.Length == 2 && args[0] == "hold";
        int held = hold ? int.Parse(args[1], CultureInfo.InvariantCulture) : 0;

        // Linux only lets a program debug its own children unless the target says otherwise, and
        // the tests deliberately start this one detached (see WatchTargetProcess). Saying so here
        // is the test stand-in's business alone; a real game is reached through the app's own
        // "Allow memory access" step.
        if (OperatingSystem.IsLinux()) _ = prctl(0x59616D61 /* PR_SET_PTRACER */, -1 /* any */, 0, 0, 0);

        // Native memory never moves, so the address printed here stays valid for the whole run.
        byte* block = (byte*)NativeMemory.AlignedAlloc(BlockSize, BlockSize);
        NativeMemory.Clear(block, BlockSize);

        if (hold) Store(block, held);
        Console.WriteLine(((ulong)block).ToString("X", CultureInfo.InvariantCulture));
        Console.WriteLine(FieldOffset.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();

        long deadline = Environment.TickCount64 + RunMilliseconds;
        int value = 1;
        long seen = 0;

        if (hold) Store(block, held);
        while (Environment.TickCount64 < deadline)
        {
            if (!hold) Store(block, value++);
            seen += Load(block);
            Thread.Sleep(2);
        }

        // Printed only so the read cannot be optimised away.
        Console.WriteLine(seen.ToString(CultureInfo.InvariantCulture));
        NativeMemory.AlignedFree(block);
        return 0;
    }

    // Kept out of line so the access really does go through a base pointer held in a register,
    // which is the thing the tests are checking can be recovered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void Store(byte* obj, int value) => *(int*)(obj + FieldOffset) = value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe int Load(byte* obj) => *(int*)(obj + FieldOffset);

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);
}
