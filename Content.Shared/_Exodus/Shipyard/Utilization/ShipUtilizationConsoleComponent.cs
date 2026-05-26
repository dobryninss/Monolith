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
    /// Radio channel used for utilization announcements (start, pause, resume, finish, cancel).
    /// </summary>
    [DataField]
    public ProtoId<RadioChannelPrototype> AnnouncementChannel = "Traffic";

    [DataField]
    public SoundSpecifier ConfirmSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier ErrorSound = new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");
}
