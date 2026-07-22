using System.Numerics;
using Robust.Shared.Prototypes;

namespace Content.Server._Exodus.FireControl;

/// <summary>
/// Exposes the entity's own gun and configured child weapons through the ship fire-control interface.
/// </summary>
[RegisterComponent]
public sealed partial class IntrinsicFireControlComponent : Component
{
    /// <summary>
    /// Maximum distance at which fire-control requests are accepted.
    /// The ammo prototype should use the same or a shorter range.
    /// </summary>
    [DataField]
    public float MaxRange = 512f;

    /// <summary>
    /// Optional localized name shown for the weapon in the fire-control interface.
    /// </summary>
    [DataField]
    public LocId? WeaponName;

    /// <summary>
    /// Additional independent weapons spawned as invisible children of the owner.
    /// </summary>
    [DataField]
    public List<IntrinsicFireControlWeaponDefinition> Weapons = new();

    /// <summary>
    /// Runtime weapon entities created from <see cref="Weapons"/>.
    /// </summary>
    public readonly List<IntrinsicFireControlSpawnedWeapon> SpawnedWeapons = new();
}

[DataDefinition]
public sealed partial class IntrinsicFireControlWeaponDefinition
{
    /// <summary>
    /// Prototype of the invisible child weapon entity.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId Prototype = string.Empty;

    /// <summary>
    /// Maximum target distance accepted for this weapon.
    /// </summary>
    [DataField]
    public float MaxRange = 512f;

    /// <summary>
    /// Optional localized name shown in the fire-control interface.
    /// </summary>
    [DataField]
    public LocId? WeaponName;

    /// <summary>
    /// Local spawn offset from the owner, used as the projectile origin.
    /// </summary>
    [DataField]
    public Vector2 Offset = Vector2.Zero;
}

public readonly record struct IntrinsicFireControlSpawnedWeapon(
    EntityUid Entity,
    float MaxRange,
    LocId? WeaponName);

/// <summary>
/// Links a spawned intrinsic weapon back to its owner for cleanup.
/// </summary>
[RegisterComponent]
public sealed partial class IntrinsicFireControlWeaponComponent : Component
{
    public EntityUid Owner = EntityUid.Invalid;
}
