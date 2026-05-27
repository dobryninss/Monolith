using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Emag;

/// <summary>
/// Appearance key used to swap a shuttle console's screen sprite to the emagged variant.
/// Server flips this on/off when the parent ship's grid lock flips.
/// </summary>
[Serializable, NetSerializable]
public enum ShuttleConsoleEmagVisuals : byte
{
    Emagged,
}
