using System.Globalization;
using System.Text.Json.Serialization;
using Cheatmaster.Core.Memory;

namespace Cheatmaster.Core.Cheats;

/// <summary>
/// Where a cheat lives. A raw address is only good for the session that found it, because the
/// process is laid out somewhere new next launch. Anchoring to a module plus an offset, or to a
/// pointer chain, is what lets a saved table still work tomorrow.
/// </summary>
public sealed class AddressSpec
{
    /// <summary>Module the offset is relative to. Null means <see cref="Offset"/> is an absolute address.</summary>
    public string? Module { get; set; }

    public ulong Offset { get; set; }

    /// <summary>Optional pointer chain applied after the base resolves.</summary>
    public int[] Pointers { get; set; } = [];

    [JsonIgnore]
    public bool IsModuleRelative => !string.IsNullOrEmpty(Module);

    [JsonIgnore]
    public bool IsPointerChain => Pointers.Length > 0;

    /// <summary>An absolute address only survives while the process lives.</summary>
    [JsonIgnore]
    public bool IsSessionOnly => !IsModuleRelative && !IsPointerChain;

    public static AddressSpec Absolute(ulong address) => new() { Offset = address };

    public static AddressSpec Relative(string module, ulong offset) => new() { Module = module, Offset = offset };

    /// <summary>
    /// Prefers a module-relative anchor so the entry survives a restart, falling back to the raw
    /// address when the value lives on the heap.
    /// </summary>
    public static AddressSpec ForAddress(TargetProcess process, ulong address)
    {
        foreach (var module in process.Modules)
        {
            if (address >= module.Base && address < module.End)
                return Relative(module.Name, address - module.Base);
        }
        return Absolute(address);
    }

    public ulong Resolve(TargetProcess process)
    {
        ulong baseAddress;
        if (IsModuleRelative)
        {
            var module = process.FindModule(Module!);
            if (module is null) return 0;
            baseAddress = module.Base + Offset;
        }
        else
        {
            baseAddress = Offset;
        }

        return Pointers.Length == 0 ? baseAddress : process.ResolvePointerChain(baseAddress, Pointers);
    }

    public string Display
    {
        get
        {
            string head = IsModuleRelative
                ? $"{Module}+{Offset.ToString("X", CultureInfo.InvariantCulture)}"
                : Offset.ToString("X", CultureInfo.InvariantCulture);

            if (Pointers.Length == 0) return head;

            var parts = new string[Pointers.Length];
            for (int i = 0; i < Pointers.Length; i++)
                parts[i] = Pointers[i].ToString("X", CultureInfo.InvariantCulture);
            return $"[{head}] + {string.Join(" + ", parts)}";
        }
    }

    public AddressSpec Clone() => new() { Module = Module, Offset = Offset, Pointers = (int[])Pointers.Clone() };

    public override string ToString() => Display;
}
