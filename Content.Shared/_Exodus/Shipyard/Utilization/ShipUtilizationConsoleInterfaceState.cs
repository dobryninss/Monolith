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
    /// shows the progress view with cancel button instead.
    /// </summary>
    public readonly bool IsActive;

    /// <summary>
    /// Net entity of the ship currently being processed by this console. Null when idle.
    /// </summary>
    public readonly NetEntity? ActiveShip;

    /// <summary>
    /// Display name of the ship being utilized, captured at start.
    /// </summary>
    public readonly string? ActiveShipName;

    /// <summary>
    /// Seconds remaining on the active utilization.
    /// </summary>
    public readonly int ActiveSecondsRemaining;

    /// <summary>
    /// Total duration of the active utilization in seconds (used to size the progress bar).
    /// </summary>
    public readonly int ActiveTotalSeconds;

    /// <summary>
    /// Payout for the active utilization, in credits. Zero when idle.
    /// </summary>
    public readonly int ActivePayout;

    public ShipUtilizationConsoleInterfaceState(
        List<UtilizationShipEntry> ships,
        bool isActive,
        NetEntity? activeShip,
        string? activeShipName,
        int activeSecondsRemaining,
        int activeTotalSeconds,
        int activePayout)
    {
        Ships = ships;
        IsActive = isActive;
        ActiveShip = activeShip;
        ActiveShipName = activeShipName;
        ActiveSecondsRemaining = activeSecondsRemaining;
        ActiveTotalSeconds = activeTotalSeconds;
        ActivePayout = activePayout;
    }
}

/// <summary>
/// One row in the utilization console UI.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct UtilizationShipEntry(
    NetEntity Ship,
    string Name,
    int Payout,
    bool LockedByOtherConsole);
