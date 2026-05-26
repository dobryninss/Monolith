using Robust.Shared.GameStates;

namespace Content.Shared._Exodus.Shipyard.Utilization;

/// <summary>
/// Marker for shuttle consoles that were part of a ship at purchase time (or restored from the SRD
/// snapshot of such a console). Only emagging a native console marks the ship eligible for
/// utilization — placing a foreign or freshly-constructed console on a ship doesn't count.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class NativeShuttleConsoleComponent : Component
{
}
