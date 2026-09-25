using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Exodus.Genetics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Exodus.Genetics;

public sealed class GeneticPrinterWindow : FancyWindow
{
    public event Action<GeneticPrinterMessage>? OperationRequested;
    private readonly Label _status = new();
    private readonly GeneticDiskContentsControl _contents = new();
    private readonly BoxContainer _printing = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private readonly BoxContainer _editing = new() { Orientation = BoxContainer.LayoutOrientation.Vertical };
    private GeneticPrinterUiState? _state;

    public GeneticPrinterWindow()
    {
        Title = Loc.GetString("genetics-printer-title");
        MinSize = new Vector2(520, 520);
        SetSize = new Vector2(600, 600);
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        root.AddChild(_status);
        var help = new RichTextLabel();
        help.SetMessage(Loc.GetString("genetics-printer-help"));
        root.AddChild(help);
        root.AddChild(_contents);
        root.AddChild(_printing);
        root.AddChild(_editing);
        _contents.SelectionChanged += UpdateButtons;
        ContentsContainer.AddChild(root);
    }

    public void UpdateState(GeneticPrinterUiState state)
    {
        _state = state;
        _status.Text = Loc.GetString("genetics-printer-status", ("mutagen", state.Mutagen),
            ("status", Loc.GetString(!state.Powered ? "genetics-offline" : state.Busy ? "genetics-busy" : "genetics-ready")));
        _contents.UpdateData(state.Disk);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _printing.DisposeAllChildren();
        _editing.DisposeAllChildren();
        if (_state == null)
            return;
        var ready = _state.Powered && !_state.Busy && _state.Disk.Status == GeneticDiskStatus.Ready;
        var block = new Button
        {
            Text = Loc.GetString("genetics-printer-print-block", ("number", _contents.SelectedBlock + 1)),
            Disabled = !ready,
        };
        block.OnPressed += _ => Send(GeneticPrinterOperation.PrintBlock);
        _printing.AddChild(block);
        var genome = new Button { Text = Loc.GetString("genetics-printer-print-genome"), Disabled = !ready };
        genome.OnPressed += _ => Send(GeneticPrinterOperation.PrintGenome);
        _printing.AddChild(genome);
        var reset = new ConfirmButton
        {
            Text = Loc.GetString("genetics-printer-reset-block", ("number", _contents.SelectedBlock + 1)),
            ConfirmationText = Loc.GetString("genetics-printer-confirm-reset"),
            Disabled = !ready,
        };
        reset.OnPressed += _ => Send(GeneticPrinterOperation.ResetBlock);
        _editing.AddChild(reset);
        var clear = new ConfirmButton
        {
            Text = Loc.GetString("genetics-printer-clear"),
            ConfirmationText = Loc.GetString("genetics-printer-confirm-clear"),
            Disabled = !_state.Powered || _state.Busy ||
                       _state.Disk.Status is GeneticDiskStatus.Missing or GeneticDiskStatus.Empty,
        };
        clear.OnPressed += _ => Send(GeneticPrinterOperation.ClearDisk);
        _editing.AddChild(clear);
    }

    private void Send(GeneticPrinterOperation operation)
    {
        if (_state is not { Powered: true, Busy: false } state || state.Disk.Disk is not { } disk)
            return;
        OperationRequested?.Invoke(new GeneticPrinterMessage(operation, disk, state.Disk.Revision, _contents.SelectedBlock));
    }
}
