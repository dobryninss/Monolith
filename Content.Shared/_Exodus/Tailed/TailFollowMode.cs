using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Tailed;

[Serializable, NetSerializable]
public enum TailFollowMode : byte
{
    /// <summary>
    /// Follow the direction between neighbouring segments, independently of their visual rotation.
    /// </summary>
    ChainDirection,

    /// <summary>
    /// Follow the previous segment's rotation, preserving upstream tail movement and idle coiling.
    /// </summary>
    PreviousRotation,
}
