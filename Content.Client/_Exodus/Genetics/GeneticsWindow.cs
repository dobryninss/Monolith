using System.Globalization;
using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Exodus.Genetics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Exodus.Genetics;

public sealed class GeneticsWindow : FancyWindow
{
    public event Action<GeneticsMessage>? OperationRequested;
    private readonly Label _status = new();
    private readonly Label _selectedLabel = new();
    private readonly RichTextLabel _instructions = new();
    private readonly BoxContainer _blocks = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private readonly BoxContainer _journal = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private readonly BoxContainer _adminEdit = new();
    private readonly LineEdit _hex = new() { Text = "000", MinWidth = 90 };
    private readonly List<Button> _patientButtons = new();
    private readonly List<Button> _scannedButtons = new();
    private readonly List<Button> _diskButtons = new();
    private readonly List<Button> _adminButtons = new();
    private readonly List<(Button Button, int Buffer)> _bufferButtons = new();
    private readonly List<Button> _digits = new();
    private GeneticsUiState? _state;
    private int _selected;

    public GeneticsWindow()
    {
        Title = Loc.GetString("genetics-title");
        MinSize = new Vector2(800, 700);
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        ContentsContainer.AddChild(root);
        root.AddChild(_status);
        root.AddChild(_instructions);
        var operations = new BoxContainer();
        root.AddChild(operations);
        AddButton(operations, "genetics-scan", GeneticsOperation.Scan, false);
        _adminButtons.Add(AddButton(operations, "genetics-reset", GeneticsOperation.Reset));
        var scroll = new ScrollContainer { VerticalExpand = true, MinHeight = 250 };
        scroll.AddChild(_blocks);
        root.AddChild(scroll);

        var edit = new BoxContainer();
        edit.AddChild(_selectedLabel);
        for (var digit = 0; digit < 3; digit++)
        {
            var button = AddButton(edit, "genetics-edit", GeneticsOperation.Edit, digit: digit);
            button.MinWidth = 50;
            button.ToolTip = Loc.GetString("genetics-digit-tooltip", ("number", digit + 1));
            _digits.Add(button);
        }
        root.AddChild(edit);
        _adminEdit.AddChild(_hex);
        AddButton(_adminEdit, "genetics-set-block", GeneticsOperation.SetBlock);
        root.AddChild(_adminEdit);
        var printing = new BoxContainer();
        AddButton(printing, "genetics-print", GeneticsOperation.PrintInjector);
        AddButton(printing, "genetics-print-genome", GeneticsOperation.PrintGenome);
        root.AddChild(printing);

        for (var i = 0; i < 3; i++)
        {
            var row = new BoxContainer();
            row.AddChild(new Label { Text = Loc.GetString("genetics-buffer", ("number", i + 1)), MinWidth = 90 });
            AddButton(row, "genetics-store", GeneticsOperation.StoreBuffer, buffer: i);
            var restore = AddButton(row, "genetics-restore", GeneticsOperation.RestoreBuffer, buffer: i);
            _adminButtons.Add(restore);
            _bufferButtons.Add((restore, i));
            _diskButtons.Add(AddButton(row, "genetics-read-disk", GeneticsOperation.ReadDisk, buffer: i));
            _bufferButtons.Add((AddButton(row, "genetics-print-buffer-block", GeneticsOperation.PrintBufferInjector, buffer: i), i));
            _bufferButtons.Add((AddButton(row, "genetics-print-buffer-genome", GeneticsOperation.PrintBufferGenome, buffer: i), i));
            root.AddChild(row);
        }
        var diskRow = new BoxContainer();
        _diskButtons.Add(AddButton(diskRow, "genetics-write-disk", GeneticsOperation.WriteDisk));
        root.AddChild(diskRow);
        var journalScroll = new ScrollContainer { MaxHeight = 120 };
        journalScroll.AddChild(_journal);
        root.AddChild(journalScroll);
        _adminEdit.Visible = false;
        foreach (var button in _adminButtons)
            button.Visible = false;
    }

    private Button AddButton(BoxContainer parent, string label, GeneticsOperation operation, bool scanned = true, int buffer = 0, int digit = 0)
    {
        var button = new Button { Text = Loc.GetString(label), Disabled = true };
        button.OnPressed += _ =>
        {
            if (_state == null)
                return;
            var value = 0;
            if (operation == GeneticsOperation.SetBlock &&
                (_hex.Text.Length is < 1 or > 3 ||
                 !int.TryParse(_hex.Text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)))
                return;
            OperationRequested?.Invoke(new GeneticsMessage(operation, _state.PatientEntity, _state.Revision, _selected, value, buffer, digit));
        };
        parent.AddChild(button);
        _patientButtons.Add(button);
        if (scanned)
            _scannedButtons.Add(button);
        return button;
    }

    public void UpdateState(GeneticsUiState state)
    {
        _state = state;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, state.Blocks.Count - 1));
        _instructions.SetMessage(Loc.GetString(state.Debug ? "genetics-admin-instructions" : "genetics-instructions"));
        var status = Loc.GetString(!state.Powered ? "genetics-offline" :
            state.PatientEntity != null && !state.Living ? "genetics-dead-patient" : state.Busy ? "genetics-busy" : "genetics-ready");
        _status.Text = state.Stability is { } stability
            ? Loc.GetString("genetics-admin-status", ("patient", state.Patient), ("stability", stability),
                ("mutagen", state.Mutagen), ("status", status))
            : Loc.GetString("genetics-status", ("patient", state.Patient), ("mutagen", state.Mutagen), ("status", status));
        var available = state.Powered && !state.Busy && state.PatientEntity != null && state.Living;
        foreach (var button in _patientButtons)
            button.Disabled = !available;
        foreach (var button in _scannedButtons)
            button.Disabled |= state.Revision < 0;
        foreach (var button in _diskButtons)
            button.Disabled |= !state.Disk;
        foreach (var (button, buffer) in _bufferButtons)
            button.Disabled |= buffer >= state.Buffers.Length || !state.Buffers[buffer];
        foreach (var button in _adminButtons)
            button.Visible = state.Debug;
        _adminEdit.Visible = state.Debug;
        _hex.Editable = available && state.Revision >= 0;
        var selectedValue = state.Blocks.Count > 0 ? state.Blocks[_selected].Value : 0;
        for (var digit = 0; digit < _digits.Count; digit++)
            _digits[digit].Text = ((selectedValue >> ((2 - digit) * 4)) & 0xF).ToString("X");

        _blocks.DisposeAllChildren();
        for (var i = 0; i < state.Blocks.Count; i++)
        {
            var index = i;
            var block = state.Blocks[i];
            var row = new Button
            {
                Text = state.Debug
                    ? Loc.GetString("genetics-admin-block", ("number", i + 1), ("value", block.Value.ToString("X3")),
                        ("name", block.Name ?? Loc.GetString("genetics-empty-block")),
                        ("state", Loc.GetString(block.Active == true ? "genetics-active" : "genetics-inactive")))
                    : Loc.GetString("genetics-block", ("number", i + 1), ("value", block.Value.ToString("X3"))),
                ToggleMode = true,
                ToolTip = block.Description,
                Pressed = i == _selected,
            };
            row.OnPressed += _ =>
            {
                // Use the latest state; selecting a row must not restore an old genome snapshot.
                if (_state == null || index >= _state.Blocks.Count)
                    return;
                _selected = index;
                _hex.Text = _state.Blocks[index].Value.ToString("X3");
                UpdateState(_state);
            };
            _blocks.AddChild(row);
        }
        _selectedLabel.Text = Loc.GetString("genetics-selected", ("number", _selected + 1));
        _journal.DisposeAllChildren();
        foreach (var line in state.Journal)
            _journal.AddChild(new Label { Text = line });
    }
}
