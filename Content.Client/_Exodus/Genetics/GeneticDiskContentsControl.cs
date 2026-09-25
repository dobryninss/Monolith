using Content.Shared._Exodus.Genetics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Exodus.Genetics;

/// <summary>Read-only block list used by both the portable disk viewer and the injector printer.</summary>
public sealed class GeneticDiskContentsControl : BoxContainer
{
    public event Action? SelectionChanged;
    public int SelectedBlock { get; private set; }
    private readonly RichTextLabel _status = new();
    private readonly BoxContainer _blocks = new() { Orientation = LayoutOrientation.Vertical };
    private GeneticDiskData? _data;

    public GeneticDiskContentsControl()
    {
        Orientation = LayoutOrientation.Vertical;
        VerticalExpand = true;
        AddChild(_status);
        var scroll = new ScrollContainer { VerticalExpand = true, MinHeight = 120, HScrollEnabled = false };
        scroll.AddChild(_blocks);
        AddChild(scroll);
    }

    public void UpdateData(GeneticDiskData data)
    {
        if (_data?.Disk != data.Disk)
            SelectedBlock = 0;
        _data = data;
        SelectedBlock = Math.Clamp(SelectedBlock, 0, Math.Max(0, data.Blocks.Count - 1));
        var status = data.Status switch
        {
            GeneticDiskStatus.Missing => "genetics-disk-missing",
            GeneticDiskStatus.Empty => "genetics-disk-empty",
            GeneticDiskStatus.Ready => "genetics-disk-recorded",
            _ => "genetics-disk-incompatible",
        };
        _status.SetMessage(Loc.GetString(status, ("count", data.Blocks.Count)));
        _blocks.DisposeAllChildren();
        for (var i = 0; i < data.Blocks.Count; i++)
        {
            var index = i;
            var row = new Button
            {
                Text = Loc.GetString("genetics-block", ("number", i + 1), ("value", data.Blocks[i].ToString("X3"))),
                ToggleMode = true,
                Pressed = i == SelectedBlock,
            };
            row.OnPressed += _ =>
            {
                SelectedBlock = index;
                if (_data != null)
                    UpdateData(_data);
                SelectionChanged?.Invoke();
            };
            _blocks.AddChild(row);
        }
    }
}
