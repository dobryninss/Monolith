using System.Numerics;
using Content.Shared.Storage;
using Robust.Shared.Prototypes;

namespace Content.Shared._Exodus.Nebula.Prototypes;

/// <summary>
/// Configures replacement content for chunk-driven worldgen debris in selected nebula marker
/// types and one distance ring around the map origin.
/// </summary>
[Prototype("nebulaContentSpawnProfile")]
public sealed partial class NebulaContentSpawnProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = null!;

    [DataField(required: true)]
    public List<EntProtoId> Markers { get; private set; } = new();

    /// <summary>
    /// Inclusive minimum and exclusive maximum distance of the nebula center from the map origin.
    /// </summary>
    [DataField(required: true)]
    public Vector2 DistanceRange { get; private set; }

    /// <summary>
    /// Debris selected when an ordinary worldgen candidate lands inside this nebula.
    /// </summary>
    [DataField(required: true)]
    public List<EntitySpawnEntry> Spawns { get; private set; } = new();
}
