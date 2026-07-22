// (c) Space Exodus Team - EXDS-RL with CLA
// Authors: Lokilife
using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Exodus.Tailed;

/// <summary>
/// When given to an entity, creates X tailed entities that try to follow the entity with the component.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class TailedEntityComponent : Component
{
    /// <summary>
    /// amount of entities in between the tail and the head
    /// </summary>
    [DataField, AutoNetworkedField]
    public int Amount = 3;

    /// <summary>
    /// the entity/entities that should be spawned after the head
    /// </summary>
    [DataField(required: true), AutoNetworkedField]
    public EntProtoId Prototype;

    /// <summary>
    /// How much space between entities
    /// </summary>
    [DataField, AutoNetworkedField]
    public float Spacing = 1f;

    /// <summary>
    /// Multiplier applied only to the distance between the head and the first segment.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float StartSpacingMultiplier = 1f;

    /// <summary>
    /// Local offsets on the head where individual tails are attached.
    /// A single zero offset preserves the original one-tail behavior.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<Vector2> StartOffsets = new() { Vector2.Zero };

    /// <summary>
    /// Initial direction offsets in degrees for individual tails.
    /// Missing entries default to the head rotation.
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<float> StartAngleOffsets = new() { 0f };

    // oh my gosh, it so many fucking variables, to make it move smooth
    [DataField, AutoNetworkedField]
    public float Stiffness = 50.0f;

    [DataField, AutoNetworkedField]
    public float Damping = 2.0f;

    [DataField, AutoNetworkedField]
    public float MaxLengthMultiplier = 1.05f;

    [DataField, AutoNetworkedField]
    public float MinLengthMultiplier = 0.95f;

    [DataField, AutoNetworkedField]
    public bool EnableRotationControl = true;

    [DataField, AutoNetworkedField]
    public float RotationLerpSpeed = 10f;

    [DataField, AutoNetworkedField]
    public Vector2 AnchorAOffset = new(-0.15f, 0);

    [DataField, AutoNetworkedField]
    public Vector2 AnchorBOffset = new(0.15f, 0);

    [DataField, AutoNetworkedField]
    public Angle RotationModifier = Angle.FromDegrees(-90);

    [DataField, AutoNetworkedField]
    public float FollowSharpness = 5f;

    [DataField, AutoNetworkedField]
    public float MaxSegmentSpeed = 10f;

    [DataField, AutoNetworkedField]
    public float VelocitySmoothing = 10f;

    [DataField, AutoNetworkedField]
    public float SpacingTolerance = 0.2f;

    /// <summary>
    /// List of tail segments
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<EntityUid> TailSegments = new();
}
