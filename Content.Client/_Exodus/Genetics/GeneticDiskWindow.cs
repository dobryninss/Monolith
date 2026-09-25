using System.Numerics;
using Content.Client.UserInterface.Controls;
using Content.Shared._Exodus.Genetics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Exodus.Genetics;

public sealed class GeneticDiskWindow : FancyWindow
{
    private readonly GeneticDiskContentsControl _contents = new();

    public GeneticDiskWindow()
    {
        Title = Loc.GetString("genetics-disk-title");
        MinSize = new Vector2(520, 520);
        SetSize = new Vector2(600, 600);
        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, SeparationOverride = 8 };
        var help = new RichTextLabel();
        help.SetMessage(Loc.GetString("genetics-disk-help"));
        root.AddChild(help);
        root.AddChild(_contents);
        ContentsContainer.AddChild(root);
    }

    public void UpdateState(GeneticDiskUiState state) => _contents.UpdateData(state.Disk);
}
