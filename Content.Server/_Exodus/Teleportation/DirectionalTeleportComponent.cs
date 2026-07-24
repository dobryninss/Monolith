using System.Numerics;
using Robust.Shared.Map;

namespace Content.Server._Exodus.Teleportation;

/// <summary>
/// Configures an ability that teleports its owner along its facing direction.
/// </summary>
[RegisterComponent, Access(typeof(DirectionalTeleportSystem))]
public sealed partial class DirectionalTeleportComponent : Component
{
    /// <summary>
    /// Primary teleport distance.
    /// </summary>
    [DataField]
    public float Distance = 1f;

    /// <summary>
    /// Ordered offsets from <see cref="Distance"/> that are tried when the primary destination intersects a grid.
    /// </summary>
    [DataField]
    public List<float> AlternativeDistanceOffsets = new();

    /// <summary>
    /// Time reserved for loading worldgen around the destination before teleporting.
    /// </summary>
    [DataField]
    public TimeSpan PreparationTime = TimeSpan.Zero;

    /// <summary>
    /// Whether existing linear velocity is cleared after a successful teleport.
    /// </summary>
    [DataField]
    public bool StopLinearVelocity = true;

    /// <summary>
    /// Optional popup shown when every configured destination intersects a grid.
    /// </summary>
    [DataField]
    public LocId? BlockedPopup;

    public bool Charging;
    public EntityUid? ChunkLoader;
    public MapId PendingMap = MapId.Nullspace;
    public Vector2 PendingOrigin;
    public Vector2 PendingDirection;
}
