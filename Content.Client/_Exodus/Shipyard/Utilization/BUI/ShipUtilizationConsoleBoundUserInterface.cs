using Content.Client._Exodus.Shipyard.Utilization.UI;
using Content.Shared._Exodus.Shipyard.Utilization;

namespace Content.Client._Exodus.Shipyard.Utilization.BUI;

public sealed class ShipUtilizationConsoleBoundUserInterface : BoundUserInterface
{
    private ShipUtilizationConsoleMenu? _menu;

    public ShipUtilizationConsoleBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _menu = new ShipUtilizationConsoleMenu();
        _menu.OnStart += ship => SendMessage(new ShipUtilizationStartMessage(ship));
        _menu.OnCancel += () => SendMessage(new ShipUtilizationCancelMessage());
        _menu.OnClose += Close;

        _menu.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not ShipUtilizationConsoleInterfaceState cast)
            return;

        _menu?.UpdateState(cast);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _menu?.Dispose();
    }
}
