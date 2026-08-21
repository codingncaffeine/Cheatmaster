using System.Globalization;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Cheats;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

public enum NoticeKind { Info, Success, Warning, Error }

/// <summary>What a cheat row needs from the shell it lives in.</summary>
public interface ICheatHost
{
    TargetProcess? Process { get; }
    void CheatsChanged();
    void Notify(string message, NoticeKind kind = NoticeKind.Info);
}

public sealed class CheatRow : ObservableObject
{
    private readonly ICheatHost _host;
    private string _valueText = "—";
    private bool _resolved;

    public CheatRow(CheatEntry entry, ICheatHost host)
    {
        Entry = entry;
        _host = host;
    }

    public CheatEntry Entry { get; }

    public string Description
    {
        get => Entry.Description;
        set
        {
            if (Entry.Description == value) return;
            Entry.Description = value;
            Raise();
            _host.CheatsChanged();
        }
    }

    public string AddressText => Entry.Address.Display;

    public string TypeLabel => Entry.Interpretation.Label;

    public bool IsSessionOnly => Entry.Address.IsSessionOnly;

    /// <summary>False when the module or pointer chain no longer resolves, so the row can grey out.</summary>
    public bool IsResolved
    {
        get => _resolved;
        private set => Set(ref _resolved, value);
    }

    public bool Frozen
    {
        get => Entry.Frozen;
        set => SetFrozen(value, notifyHost: true);
    }

    /// <summary>
    /// Freezes or thaws this entry. Bulk operations pass notifyHost false and tell the host once
    /// at the end, so freezing two hundred rows is one save and one freeze-set rebuild.
    /// </summary>
    public void SetFrozen(bool value, bool notifyHost)
    {
        if (Entry.Frozen == value) return;
        Entry.Frozen = value;

        // Freezing with no target value pins whatever is on screen right now.
        if (value && string.IsNullOrWhiteSpace(Entry.FreezeValue))
            Entry.FreezeValue = _valueText == "—" ? "0" : _valueText;

        Raise(nameof(Frozen));
        Raise(nameof(FreezeValue));
        if (notifyHost) _host.CheatsChanged();
    }

    public string FreezeValue
    {
        get => Entry.FreezeValue;
        set
        {
            if (Entry.FreezeValue == value) return;
            Entry.FreezeValue = value;
            Raise();
            _host.CheatsChanged();
        }
    }

    public string Hotkey
    {
        get => Entry.Hotkey;
        set
        {
            if (Entry.Hotkey == value) return;
            Entry.Hotkey = value;
            Raise();
            _host.CheatsChanged();
        }
    }

    public string Notes
    {
        get => Entry.Notes;
        set
        {
            if (Entry.Notes == value) return;
            Entry.Notes = value;
            Raise();
            _host.CheatsChanged();
        }
    }

    /// <summary>The live value. Assigning writes it into the target.</summary>
    public string ValueText
    {
        get => _valueText;
        set
        {
            if (_valueText == value) return;

            if (_host.Process is null)
            {
                _host.Notify("Attach to a process before editing values.", NoticeKind.Warning);
                Raise();
                return;
            }

            if (!TrySetValue(value))
            {
                _host.Notify($"Could not write {value} to {AddressText}.", NoticeKind.Error);
                Raise();
                return;
            }

            if (Entry.Frozen) _host.CheatsChanged();
        }
    }

    /// <summary>
    /// Writes a value into the target without raising a notice, for bulk edits that report once
    /// at the end. Returns false when the value could not be written.
    /// </summary>
    public bool TrySetValue(string text)
    {
        var process = _host.Process;
        if (process is null) return false;
        if (!Entry.TryWriteDisplayValue(process, text)) return false;

        // A frozen entry would immediately put the old value back.
        if (Entry.Frozen)
        {
            Entry.FreezeValue = text;
            Raise(nameof(FreezeValue));
        }

        _valueText = text;
        Raise(nameof(ValueText));
        return true;
    }

    public void Refresh(TargetProcess? process)
    {
        if (process is null)
        {
            IsResolved = false;
            return;
        }

        if (!Entry.TryReadValue(process, out ulong bits))
        {
            IsResolved = false;
            if (_valueText != "—")
            {
                _valueText = "—";
                Raise(nameof(ValueText));
            }
            return;
        }

        IsResolved = true;
        string text = Entry.Interpretation.FormatDisplay(bits);
        if (text == _valueText) return;
        _valueText = text;
        Raise(nameof(ValueText));
    }

    public void RaiseAddressChanged()
    {
        Raise(nameof(AddressText));
        Raise(nameof(TypeLabel));
        Raise(nameof(IsSessionOnly));
    }

    public string ToolTipText
    {
        get
        {
            var parts = new List<string>
            {
                $"Address: {Entry.Address.Display}",
                $"Stored as: {Entry.Interpretation.Label}",
                Entry.Interpretation.Hint
            };
            if (Entry.Address.IsSessionOnly)
                parts.Add("This address is not anchored to a module, so it will not survive a restart.");
            if (!string.IsNullOrWhiteSpace(Entry.Notes))
                parts.Add(Entry.Notes);
            return string.Join(Environment.NewLine, parts);
        }
    }
}

/// <summary>One surviving storage theory, shown as a filter chip beside the results.</summary>
public sealed class InterpretationChip : ObservableObject
{
    private bool _isPinned;

    public InterpretationChip(InterpretationGroup group, bool isBest)
    {
        Group = group;
        IsBest = isBest;
    }

    public InterpretationGroup Group { get; }
    public bool IsBest { get; }

    public int InterpId => Group.InterpId;
    public string Label => Group.Label;
    public string Hint => Group.Hint;
    public string CountText => Group.Count.ToString("N0", CultureInfo.InvariantCulture) + (Group.Capped ? "+" : "");
    public bool Capped => Group.Capped;

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    public string ToolTipText => Group.Capped
        ? $"{Hint}\nToo many matches to be useful — this encoding is almost certainly wrong."
        : $"{Hint}\n{CountText} address(es) matched.";
}
