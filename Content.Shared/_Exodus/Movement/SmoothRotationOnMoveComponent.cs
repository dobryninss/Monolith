using Robust.Shared.GameStates;

namespace Content.Shared._Exodus.Movement;

/// <summary>
/// Smoothly rotates an entity towards its movement direction.
/// Pair with <see cref="Content.Shared.Movement.Components.NoRotateOnMoveComponent"/>
/// to disable the default instant rotation.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SmoothRotationOnMoveComponent : Component
{
    /// <summary>
    /// Maximum rotation speed in degrees per second.
    /// </summary>
    [DataField]
    public float RotationSpeed = 120f;

    /// <summary>
    /// Whether combat mode should hand rotation control over to the mouse rotator.
    /// </summary>
    [DataField]
    public bool DisableInCombatMode = true;
}
