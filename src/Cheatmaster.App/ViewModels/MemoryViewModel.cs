using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.Core.Memory;
using Cheatmaster.Core.Scanning;

namespace Cheatmaster.App.ViewModels;

/// <summary>One byte on screen. It keeps its identity across refreshes so the grid is not rebuilt.</summary>
public sealed class MemoryCell : ObservableObject
{
    private string _text = "··";
    private bool _changed;
    private bool _isSelected;

    public MemoryCell(int offset) => Offset = offset;

    /// <summary>Where this byte sits relative to the address the view is showing.</summary>
    public int Offset { get; }

    public string Text
    {
        get => _text;
        private set => Set(ref _text, value);
    }

    /// <summary>Moved since the last look, which is how a field gives itself away.</summary>
    public bool Changed
    {
        get => _changed;
        private set => Set(ref _changed, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public void Update(byte value, bool readable, bool moved)
    {
        Text = readable ? value.ToString("X2", CultureInfo.InvariantCulture) : "··";
        Changed = moved;
    }
}

public sealed class MemoryLine : ObservableObject
{
    private string _addressText = string.Empty;
    private string _ascii = string.Empty;

    public MemoryLine(int index)
    {
        Index = index;
        Cells = new MemoryCell[MemoryViewModel.BytesPerLine];
        for (int i = 0; i < Cells.Length; i++)
            Cells[i] = new MemoryCell(index * MemoryViewModel.BytesPerLine + i);
    }

    public int Index { get; }

    public MemoryCell[] Cells { get; }

    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    public string Ascii
    {
        get => _ascii;
        set => Set(ref _ascii, value);
    }
}

/// <summary>How the bytes under the cursor read as one particular type.</summary>
public sealed class MemoryField : ObservableObject
{
    private string _value = "—";
    private string _detail = string.Empty;
    private bool _isReadable;

    public MemoryField(string label, ScanType type, int width)
    {
        Label = label;
        Type = type;
        Width = width;
    }

    public string Label { get; }
    public ScanType Type { get; }
    public int Width { get; }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public bool IsReadable
    {
        get => _isReadable;
        set => Set(ref _isReadable, value);
    }
}

/// <summary>
/// The bytes around an address, and what they could be.
///
/// This is the other half of watching an address: once the object a value belongs to is known,
/// everything else about that object is sitting next to it. Health, ammo, position and the rest of
/// a character are usually fields of one structure, and finding them one search at a time is the
/// slow way round. Watching which bytes move while the game is played finds the others.
/// </summary>
public sealed class MemoryViewModel : ObservableObject, IDisposable
{
    public const int BytesPerLine = 16;

    private readonly TargetProcess _process;
    private readonly DispatcherTimer _timer;
    private readonly List<ulong> _history = [];
    private readonly int _size;

    private byte[] _bytes;
    private byte[] _previous;
    private readonly bool[] _readable;
    private ulong _address;
    private int _selectedOffset;
    private string _addressInput = string.Empty;
    private string _status = string.Empty;
    private bool _live = true;

    public MemoryViewModel(TargetProcess process, ulong address, string description, int lines = 24)
    {
        _process = process;
        Description = description;

        Lines = [];
        for (int i = 0; i < lines; i++) Lines.Add(new MemoryLine(i));

        _size = lines * BytesPerLine;
        _bytes = new byte[_size];
        _previous = new byte[_size];
        _readable = new bool[_size];

        Fields =
        [
            new MemoryField("Int8", ScanType.Int8, 1),
            new MemoryField("Int16", ScanType.Int16, 2),
            new MemoryField("Int32", ScanType.Int32, 4),
            new MemoryField("Int64", ScanType.Int64, 8),
            new MemoryField("Float", ScanType.Float, 4),
            new MemoryField("Double", ScanType.Double, 8),
            new MemoryField("Pointer", ScanType.UInt64, process.Is64Bit ? 8 : 4)
        ];

        GoCommand = new RelayCommand(() => GoTo(AddressInput));
        BackCommand = new RelayCommand(Back, () => _history.Count > 0);
        FollowCommand = new RelayCommand(FollowPointer, () => CanFollow);
        PageUpCommand = new RelayCommand(() => Show(_address - (ulong)_size, remember: false));
        PageDownCommand = new RelayCommand(() => Show(_address + (ulong)_size, remember: false));

        Show(address, remember: false);

        // Half a second is fast enough to catch a field moving while the game is played, and slow
        // enough to cost nothing.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Refresh(markChanges: true);
        _timer.Start();
    }

    public string Description { get; }

    public ObservableCollection<MemoryLine> Lines { get; }

    /// <summary>Every way of reading the bytes under the cursor, so the right one can be recognised.</summary>
    public MemoryField[] Fields { get; }

    public RelayCommand GoCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand FollowCommand { get; }
    public RelayCommand PageUpCommand { get; }
    public RelayCommand PageDownCommand { get; }

    public ulong Address => _address;

    public string AddressText => _address.ToString("X", CultureInfo.InvariantCulture);

    public string AddressInput
    {
        get => _addressInput;
        set => Set(ref _addressInput, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Keep re-reading. Turning it off holds the view still for reading at leisure.</summary>
    public bool Live
    {
        get => _live;
        set
        {
            if (!Set(ref _live, value)) return;
            if (value) _timer.Start();
            else _timer.Stop();
        }
    }

    public int SelectedOffset
    {
        get => _selectedOffset;
        private set
        {
            if (!Set(ref _selectedOffset, value)) return;
            Raise(nameof(SelectedAddressText));
            UpdateFields();
        }
    }

    public ulong SelectedAddress => _address + (ulong)_selectedOffset;

    public string SelectedAddressText => SelectedAddress.ToString("X", CultureInfo.InvariantCulture);

    public void Select(MemoryCell cell)
    {
        foreach (var line in Lines)
        {
            foreach (var other in line.Cells) other.IsSelected = ReferenceEquals(other, cell);
        }

        SelectedOffset = cell.Offset;
    }

    private void UpdateFields()
    {
        foreach (var field in Fields)
        {
            if (field.Label == "Pointer")
            {
                bool ok = TryReadPointer(_selectedOffset, out ulong pointer);
                field.IsReadable = ok;
                field.Value = ok ? pointer.ToString("X", CultureInfo.InvariantCulture) : "—";
                field.Detail = ok ? DescribePointer(pointer) : string.Empty;
                continue;
            }

            if (!Readable(_selectedOffset, field.Width))
            {
                field.IsReadable = false;
                field.Value = "—";
                field.Detail = string.Empty;
                continue;
            }

            ulong bits = Raw.ReadBits(field.Type, _bytes.AsSpan(_selectedOffset, field.Width));
            var interpretation = new Interpretation(field.Type, 1, 1, false, 0, 0);

            field.IsReadable = true;
            field.Value = interpretation.FormatDisplay(bits);
            field.Detail = field.Type.IsFloat() ? string.Empty : "0x" + bits.ToString("X", CultureInfo.InvariantCulture);
        }

        Raise(nameof(CanFollow));
        FollowCommand.RaiseCanExecuteChanged();
    }

    private bool Readable(int offset, int width)
    {
        if (offset < 0 || offset + width > _bytes.Length) return false;
        for (int i = 0; i < width; i++)
        {
            if (!_readable[offset + i]) return false;
        }
        return true;
    }

    public bool CanFollow => TryReadPointer(_selectedOffset, out ulong pointer) && PointsSomewhere(pointer);

    private bool TryReadPointer(int offset, out ulong pointer)
    {
        pointer = 0;
        int width = _process.Is64Bit ? 8 : 4;
        if (!Readable(offset, width)) return false;

        pointer = width == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(offset, 8))
            : BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset, 4));
        return true;
    }

    private bool PointsSomewhere(ulong pointer)
    {
        if (pointer == 0) return false;
        Span<byte> probe = stackalloc byte[1];
        return _process.ReadExact(pointer, probe);
    }

    private string DescribePointer(ulong pointer)
    {
        if (pointer == 0) return "null";

        foreach (var module in _process.Modules)
        {
            if (pointer >= module.Base && pointer < module.End)
                return $"{module.Name}+{(pointer - module.Base).ToString("X", CultureInfo.InvariantCulture)}";
        }

        return PointsSomewhere(pointer) ? "points at readable memory" : "does not point anywhere readable";
    }

    private void FollowPointer()
    {
        if (!TryReadPointer(_selectedOffset, out ulong pointer) || !PointsSomewhere(pointer)) return;
        Show(pointer, remember: true);
    }

    private void Back()
    {
        if (_history.Count == 0) return;

        ulong previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Show(previous, remember: false);
        BackCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Accepts a plain address or a module and an offset, the way an entry displays one.</summary>
    public void GoTo(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return;

        int split = trimmed.IndexOf('+');
        if (split > 0)
        {
            string name = trimmed[..split].Trim().Trim('"');
            var module = _process.FindModule(name);
            if (module is null)
            {
                Status = $"There is no module called {name} in this game.";
                return;
            }

            if (!TryHex(trimmed[(split + 1)..], out ulong relative))
            {
                Status = "That offset is not a hex number.";
                return;
            }

            Show(module.Base + relative, remember: true);
            return;
        }

        if (!TryHex(trimmed, out ulong address))
        {
            Status = "That is not an address. Try a hex address, or game.exe+1A2B.";
            return;
        }

        Show(address, remember: true);
    }

    private static bool TryHex(string text, out ulong value)
    {
        string cleaned = text.Trim();
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[2..];
        return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public void Show(ulong address, bool remember)
    {
        if (remember && _address != 0 && _address != address)
        {
            _history.Add(_address);
            BackCommand.RaiseCanExecuteChanged();
        }

        _address = address;
        AddressInput = AddressText;
        Raise(nameof(AddressText));
        Raise(nameof(SelectedAddressText));

        // Nothing has moved when the view jumps somewhere new; it has only changed what it shows.
        Array.Clear(_previous);
        Refresh(markChanges: false);
    }

    private void Refresh(bool markChanges)
    {
        if (!_process.IsOpen) return;

        (_previous, _bytes) = (_bytes, _previous);
        Array.Clear(_bytes);
        Array.Clear(_readable);

        var runs = new List<ValidRun>();
        int read = _process.ReadRuns(_address, _bytes, runs);
        foreach (var run in runs)
        {
            for (int i = 0; i < run.Length; i++) _readable[run.Offset + i] = true;
        }

        Span<char> ascii = stackalloc char[BytesPerLine];

        for (int line = 0; line < Lines.Count; line++)
        {
            var row = Lines[line];
            int start = line * BytesPerLine;
            row.AddressText = (_address + (ulong)start).ToString("X", CultureInfo.InvariantCulture);

            for (int i = 0; i < BytesPerLine; i++)
            {
                int offset = start + i;
                bool readable = _readable[offset];
                byte value = _bytes[offset];
                row.Cells[i].Update(value, readable, markChanges && readable && value != _previous[offset]);
                ascii[i] = readable && value is >= 0x20 and < 0x7F ? (char)value : '.';
            }

            row.Ascii = new string(ascii);
        }

        Status = read == 0
            ? "Nothing here can be read. The game may have freed this memory."
            : read < _bytes.Length
                ? $"{read} of {_bytes.Length} bytes readable — part of this range is not mapped."
                : string.Empty;

        UpdateFields();
    }

    public void Dispose() => _timer.Stop();
}
