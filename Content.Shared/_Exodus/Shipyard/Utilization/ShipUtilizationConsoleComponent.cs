using Content.Shared.Radio;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Exodus.Shipyard.Utilization;

/// <summary>
/// Console placed on Camelot that lets anyone utilize an emagged docked ship for a fixed payout.
/// Only one ship can be processed per console at a time; another console on the same grid can
/// process a different ship in parallel.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShipUtilizationConsoleComponent : Component
{
    /// <summary>
    /// EntityUid of the ship currently being processed by this console. Null when idle.
    /// </summary>
    [DataField]
    public EntityUid? ActiveShip;

    /// <summary>
    /// Game time at which the active utilization started. Null when idle.
    /// </summary>
    [DataField]
    public TimeSpan? ActiveStartedAt;

    /// <summary>
    /// Game time at which the active utilization will finish. Null when idle.
    /// </summary>
    [DataField]
    public TimeSpan? ActiveEndsAt;

    /// <summary>
    /// Payout (in credits) computed when the utilization started — locked in so UI doesn't flicker
    /// if the ship's appraisal changes mid-process.
    /// </summary>
    [DataField]
    public int ActivePayout;

    /// <summary>
    /// Display name of the ship being utilized, captured at start so UI shows the right name even
    /// if the grid gets renamed.
    /// </summary>
    [DataField]
    public string? ActiveShipName;

    /// <summary>
    /// The player entity that initiated the utilization (for admin logs / forensics).
    /// </summary>
    [DataField]
    public EntityUid? ActiveInitiator;

    /// <summary>
    /// Next time we push a UI state refresh for this console — limits update rate to ~1 Hz while
    /// the timer is running.
    /// </summary>
    [DataField]
    public TimeSpan NextUiUpdate;

    /// <summary>
    /// Next time we scan the ship for living beings. While paused this drives the resume check.
    /// </summary>
    [DataField]
    public TimeSpan NextOrganicCheck;

    /// <summary>
    /// True while utilization is on hold because organic life was detected aboard.
    /// </summary>
    [DataField]
    public bool Paused;

    /// <summary>
    /// Radio channel used for utilization announcements (start, pause, resume, finish, cancel).
    /// </summary>
    [DataField]
    public ProtoId<RadioChannelPrototype> AnnouncementChannel = "Traffic";

    [DataField]
    public SoundSpecifier ConfirmSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier ErrorSound = new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");
}
