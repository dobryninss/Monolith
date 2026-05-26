using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Shipyard.Utilization;

[Serializable, NetSerializable]
public sealed class ShipUtilizationConsoleInterfaceState : BoundUserInterfaceState
{
    /// <summary>
    /// Ships currently available to be utilized via this console.
    /// </summary>
    public readonly List<UtilizationShipEntry> Ships;

    /// <summary>
    /// True when this console is already running a utilization process — UI hides the ship list and
    /// shows the active row with cancel button instead.
    /// </summary>
    public readonly bool IsActive;

    /// <summary>
    /// Net entity of the ship currently being processed by this console. Null when idle.
    /// </summary>
    public readonly NetEntity? ActiveShip;

    /// <summary>
    /// Seconds remaining on the active utilization. 0 when idle or paused on the very first check.
    /// </summary>
    public readonly int ActiveSecondsRemaining;

    /// <summary>
    /// Payout for the active utilization, in credits. Zero when idle.
    /// </summary>
    public readonly int ActivePayout;

    public ShipUtilizationConsoleInterfaceState(
        List<UtilizationShipEntry> ships,
        bool isActive,
        NetEntity? activeShip,
        int activeSecondsRemaining,
        int activePayout)
    {
        Ships = ships;
        IsActive = isActive;
        ActiveShip = activeShip;
        ActiveSecondsRemaining = activeSecondsRemaining;
        ActivePayout = activePayout;
    }
}

/// <summary>
/// One row in the utilization console UI.
/// Voucher-purchased ships pay a fixed nominal amount; other ships use a fraction of their appraisal.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct UtilizationShipEntry(
    NetEntity Ship,
    string Name,
    int Payout,
    bool VoucherPurchased,
    bool LockedByOtherConsole);
