using Cheatmaster.Core.Native;

namespace Cheatmaster.Core.Memory;

/// <summary>A single committed span of the target address space.</summary>
public readonly record struct MemoryRegion(ulong Base, ulong Size, uint Protect, uint Type)
{
    public ulong End => Base + Size;

    public bool IsWritable => (Protect & PageProtect.WritableMask) != 0;
    public bool IsExecutable => (Protect & PageProtect.ExecutableMask) != 0;
    public bool IsGuarded => (Protect & PageProtect.Guard) != 0;
    public bool IsCopyOnWrite => (Protect & (PageProtect.WriteCopy | PageProtect.ExecuteWriteCopy)) != 0;

    public bool IsImage => Type == MemType.Image;
    public bool IsMapped => Type == MemType.Mapped;
    public bool IsPrivate => Type == MemType.Private;

    public string TypeName => Type switch
    {
        MemType.Image => "Image",
        MemType.Mapped => "Mapped",
        MemType.Private => "Private",
        _ => "?"
    };

    public string ProtectName => (Protect & PageProtect.AccessMask) switch
    {
        PageProtect.NoAccess => "---",
        PageProtect.ReadOnly => "R--",
        PageProtect.ReadWrite => "RW-",
        PageProtect.WriteCopy => "RWc",
        PageProtect.Execute => "--X",
        PageProtect.ExecuteRead => "R-X",
        PageProtect.ExecuteReadWrite => "RWX",
        PageProtect.ExecuteWriteCopy => "RWXc",
        _ => "?"
    };

    public override string ToString() => $"{Base:X}-{End:X} {ProtectName} {TypeName} ({Size / 1024} KB)";
}
