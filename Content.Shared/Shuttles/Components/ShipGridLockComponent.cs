using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared.Shuttles.Components;

/// <summary>
/// Component that handles grid-level locking for ships with deeds.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
// Exodus emag-rework: access opened so EmaggedShuttleConsoleSystem can write lock state
public sealed partial class ShipGridLockComponent : Component
{
    /// <summary>
    /// Whether the ship grid is currently locked.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Locked = true;

    /// <summary>
    /// The ID of the shuttle this grid is locked to.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? ShuttleId;

    // Exodus-begin emag-rework
    /// <summary>
    /// If true, the ship's lock has been permanently broken by an emag on one of its shuttle consoles.
    /// While true, GetEffectiveLockState always returns false and no card/voucher can re-lock the grid.
    /// Cleared only by demagging a shuttle console.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool LockDisabled;

    /// <summary>
    /// Entity of the player who emagged the ship. Used for the demag paper.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? EmaggedBy;

    /// <summary>
    /// Character name of the emagger at the moment of the emag.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? EmaggerName;

    /// <summary>
    /// Job title (from the ID card the emagger was holding) at the moment of the emag.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string? EmaggerJob;

    /// <summary>
    /// Game time when the emag happened.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan? EmaggedAt;
    // Exodus-end emag-rework
}
