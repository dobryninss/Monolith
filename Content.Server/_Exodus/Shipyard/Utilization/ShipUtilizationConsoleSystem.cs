using Content.Server.Cargo.Systems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared._Exodus.Shipyard.Utilization;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;

namespace Content.Server._Exodus.Shipyard.Utilization;

/// <summary>
/// Drives the ship utilization console UI.
/// Lists docked, emagged ships that can be utilized by anyone on board Camelot.
/// Start/cancel handlers are stubbed in this commit — actual processing lands in commit 5.
/// </summary>
public sealed class ShipUtilizationConsoleSystem : EntitySystem
{
    /// <summary>
    /// Multiplier applied to the appraisal of a regularly-purchased ship to compute the utilization
    /// payout. Owner-driven shipyard sales pay 0.85 × appraisal; utilization pays 25 percentage
    /// points less (absolute).
    /// </summary>
    private const float UtilizationSaleRate = 0.60f;

    /// <summary>
    /// Flat payout for voucher-purchased ships, which can't be appraised back into credits.
    /// </summary>
    private const int VoucherPayout = 50_000;

    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipUtilizationConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ShipUtilizationConsoleComponent, ShipUtilizationStartMessage>(OnStart);
        SubscribeLocalEvent<ShipUtilizationConsoleComponent, ShipUtilizationCancelMessage>(OnCancel);
    }

    private void OnUiOpened(Entity<ShipUtilizationConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        RefreshState(ent);
    }

    // TODO commit 5: start the 5-minute utilization process.
    private void OnStart(Entity<ShipUtilizationConsoleComponent> ent, ref ShipUtilizationStartMessage args)
    {
        RefreshState(ent);
    }

    // TODO commit 5: cancel the active utilization.
    private void OnCancel(Entity<ShipUtilizationConsoleComponent> ent, ref ShipUtilizationCancelMessage args)
    {
        RefreshState(ent);
    }

    private void RefreshState(Entity<ShipUtilizationConsoleComponent> ent)
    {
        var ships = GetEligibleShips(ent);
        var activePayout = ent.Comp.ActiveShip is { } active ? CalculatePayout(active) : 0;

        var state = new ShipUtilizationConsoleInterfaceState(
            ships,
            isActive: ent.Comp.ActiveShip != null,
            activeShip: ent.Comp.ActiveShip is { } activeUid ? GetNetEntity(activeUid) : null,
            activeSecondsRemaining: 0,
            activePayout: activePayout);

        _ui.SetUiState(ent.Owner, ShipUtilizationConsoleUiKey.Key, state);
    }

    /// <summary>
    /// Voucher-bought ships pay a flat <see cref="VoucherPayout"/>; everything else pays
    /// <see cref="UtilizationSaleRate"/> of the appraisal.
    /// </summary>
    public int CalculatePayout(EntityUid shipGrid)
    {
        if (TryComp<ShuttleDeedComponent>(shipGrid, out var deed) && deed.PurchasedWithVoucher)
            return VoucherPayout;

        var appraisal = _pricing.AppraiseGrid(shipGrid, null);
        return (int)(appraisal * UtilizationSaleRate);
    }

    /// <summary>
    /// Walks the docks on the console's own grid (Camelot) to find docked ships whose grid lock has
    /// been emag-broken and which still own at least one native shuttle console.
    /// </summary>
    private List<UtilizationShipEntry> GetEligibleShips(Entity<ShipUtilizationConsoleComponent> ent)
    {
        var result = new List<UtilizationShipEntry>();

        if (Transform(ent.Owner).GridUid is not { } consoleGrid)
            return result;

        var seen = new HashSet<EntityUid>();
        var consoleDocks = _docking.GetDocks(consoleGrid);

        foreach (var dock in consoleDocks)
        {
            if (dock.Comp.DockedWith is not { } otherDock)
                continue;

            if (Transform(otherDock).GridUid is not { } shipGrid || shipGrid == consoleGrid)
                continue;

            if (!seen.Add(shipGrid))
                continue;

            if (!TryComp<ShipGridLockComponent>(shipGrid, out var gridLock) || !gridLock.LockDisabled)
                continue;

            if (!HasNativeShuttleConsole(shipGrid, gridLock.ShuttleId))
                continue;

            var voucher = TryComp<ShuttleDeedComponent>(shipGrid, out var deed) && deed.PurchasedWithVoucher;
            result.Add(new UtilizationShipEntry(
                Ship: GetNetEntity(shipGrid),
                Name: Name(shipGrid),
                Payout: CalculatePayout(shipGrid),
                VoucherPurchased: voucher,
                LockedByOtherConsole: IsShipActiveOnAnyConsole(shipGrid, ent.Owner)));
        }

        return result;
    }

    /// <summary>
    /// A console is considered native to its ship when it carries a matching shuttle ID — that's
    /// the same tag installed at purchase, and the SRD rebuilder re-applies it via the grid state.
    /// </summary>
    private bool HasNativeShuttleConsole(EntityUid shipGrid, string? expectedShuttleId)
    {
        if (string.IsNullOrEmpty(expectedShuttleId))
            return false;

        var query = EntityQueryEnumerator<ShuttleConsoleComponent, ShuttleConsoleLockComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var lockComp, out var xform))
        {
            if (xform.GridUid != shipGrid)
                continue;

            if (lockComp.ShuttleId == expectedShuttleId)
                return true;
        }

        return false;
    }

    private bool IsShipActiveOnAnyConsole(EntityUid shipGrid, EntityUid exceptConsole)
    {
        var query = EntityQueryEnumerator<ShipUtilizationConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (uid == exceptConsole)
                continue;

            if (comp.ActiveShip == shipGrid)
                return true;
        }

        return false;
    }
}
