namespace Cheatmaster.Core.Memory;

/// <summary>
/// The operating-system half of <see cref="TargetProcess"/>: how memory is read, written and
/// mapped out. Everything above this — region merging, page-by-page recovery, pointer chains —
/// is the same on every platform.
/// </summary>
internal interface IProcessBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Bytes read from the start of <paramref name="buffer"/>; fewer than asked means the rest failed.</summary>
    int Read(ulong address, Span<byte> buffer);

    /// <summary>Writes, lifting page protection if that is what it takes.</summary>
    bool Write(ulong address, ReadOnlySpan<byte> data);

    /// <summary>Loaded modules, main executable first.</summary>
    List<ModuleEntry> LoadModules();

    /// <summary>Every committed region overlapping [start, end), in address order and unmerged.</summary>
    void QueryRegions(ulong start, ulong end, IReadOnlyList<ModuleEntry> modules, List<MemoryRegion> into);
}
