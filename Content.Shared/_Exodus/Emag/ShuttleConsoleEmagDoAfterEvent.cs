using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Emag;

/// <summary>
/// DoAfter event for the delayed emag of a shuttle console.
/// Fires on the emag tool when the 20-second hack progress completes (or cancels).
/// </summary>
[Serializable, NetSerializable]
public sealed partial class ShuttleConsoleEmagDoAfterEvent : SimpleDoAfterEvent
{
}
