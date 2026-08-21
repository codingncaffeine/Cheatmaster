using System.Runtime.InteropServices;

namespace Cheatmaster.Core.Native;

/// <summary>
/// A thread's register state, held in aligned native memory and read by offset.
///
/// CONTEXT has to be 16-byte aligned on x64 and carries a 512-byte floating point save area in
/// the middle of it, so it is described here as offsets into a buffer rather than as a struct.
/// A 32-bit target under WoW64 has an entirely different layout and needs the Wow64 entry points,
/// which is why both flavours live behind the same small surface.
/// </summary>
internal sealed unsafe class ThreadContextBuffer : IDisposable
{
    // x64 CONTEXT.
    private const int Amd64Size = 1232;
    private const int Amd64Flags = 0x30;
    private const int Amd64Dr0 = 0x48;
    private const int Amd64Dr6 = 0x68;
    private const int Amd64Dr7 = 0x70;
    private const int Amd64Rax = 0x78;   // Rax..R15 run contiguously from here
    private const int Amd64Rip = 0xF8;

    // WOW64_CONTEXT.
    private const int I386Size = 716;
    private const int I386Flags = 0x00;
    private const int I386Dr0 = 0x04;
    private const int I386Dr6 = 0x14;
    private const int I386Dr7 = 0x18;
    private const int I386Eip = 0xB8;

    private static readonly string[] Amd64Names =
    [
        "RAX", "RCX", "RDX", "RBX", "RSP", "RBP", "RSI", "RDI",
        "R8", "R9", "R10", "R11", "R12", "R13", "R14", "R15"
    ];

    /// <summary>The 32-bit registers are not contiguous, so each carries its own offset.</summary>
    private static readonly (string Name, int Offset)[] I386Registers =
    [
        ("EAX", 0xB0), ("ECX", 0xAC), ("EDX", 0xA8), ("EBX", 0xA4),
        ("ESP", 0xC4), ("EBP", 0xB4), ("ESI", 0xA0), ("EDI", 0x9C)
    ];

    private byte* _buffer;

    public ThreadContextBuffer(bool is64Bit)
    {
        Is64Bit = is64Bit;
        int size = is64Bit ? Amd64Size : I386Size;
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16);
        NativeMemory.Clear(_buffer, (nuint)size);
    }

    public bool Is64Bit { get; }

    public uint FullFlags => Is64Bit ? ContextFlags.Amd64Full : ContextFlags.I386Full;
    public uint DebugRegisterFlags => Is64Bit ? ContextFlags.Amd64DebugRegisters : ContextFlags.I386DebugRegisters;

    private int FlagsOffset => Is64Bit ? Amd64Flags : I386Flags;
    private int Dr0Offset => Is64Bit ? Amd64Dr0 : I386Dr0;
    private int Dr6Offset => Is64Bit ? Amd64Dr6 : I386Dr6;
    private int Dr7Offset => Is64Bit ? Amd64Dr7 : I386Dr7;

    private ulong ReadSlot(int offset) => Is64Bit ? *(ulong*)(_buffer + offset) : *(uint*)(_buffer + offset);

    private void WriteSlot(int offset, ulong value)
    {
        if (Is64Bit) *(ulong*)(_buffer + offset) = value;
        else *(uint*)(_buffer + offset) = (uint)value;
    }

    private int SlotStride => Is64Bit ? 8 : 4;

    public bool Capture(nint thread, uint flags)
    {
        *(uint*)(_buffer + FlagsOffset) = flags;
        return Is64Bit ? DebugApi.GetThreadContext(thread, _buffer) : DebugApi.Wow64GetThreadContext(thread, _buffer);
    }

    /// <summary>
    /// Writes back only the parts named by <paramref name="flags"/>. Applying more than was
    /// changed risks stamping stale values over a register the thread has since moved on from.
    /// </summary>
    public bool Apply(nint thread, uint flags)
    {
        *(uint*)(_buffer + FlagsOffset) = flags;
        return Is64Bit ? DebugApi.SetThreadContext(thread, _buffer) : DebugApi.Wow64SetThreadContext(thread, _buffer);
    }

    public ulong Dr(int index) => ReadSlot(Dr0Offset + index * SlotStride);
    public void SetDr(int index, ulong value) => WriteSlot(Dr0Offset + index * SlotStride, value);

    public ulong Dr6
    {
        get => ReadSlot(Dr6Offset);
        set => WriteSlot(Dr6Offset, value);
    }

    public ulong Dr7
    {
        get => ReadSlot(Dr7Offset);
        set => WriteSlot(Dr7Offset, value);
    }

    public ulong InstructionPointer => Is64Bit ? *(ulong*)(_buffer + Amd64Rip) : *(uint*)(_buffer + I386Eip);

    /// <summary>The general purpose registers, in the order a disassembly listing names them.</summary>
    public void ReadRegisters(List<(string Name, ulong Value)> into)
    {
        into.Clear();
        if (Is64Bit)
        {
            for (int i = 0; i < Amd64Names.Length; i++)
                into.Add((Amd64Names[i], *(ulong*)(_buffer + Amd64Rax + i * 8)));
        }
        else
        {
            foreach (var (name, offset) in I386Registers)
                into.Add((name, *(uint*)(_buffer + offset)));
        }
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }
}
