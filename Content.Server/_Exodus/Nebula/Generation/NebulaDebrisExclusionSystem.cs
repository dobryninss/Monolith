using Content.Server._Exodus.Nebula.Spawning;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Systems.Debris;

namespace Content.Server._Exodus.Nebula.Generation;

/// <summary>
/// Blocks ordinary worldgen debris only in death-zones and nebulas that have no selectable
/// content profile. Valid nebula profiles are selected after all pre-placement carvers finish.
/// </summary>
public sealed partial class NebulaDebrisExclusionSystem : EntitySystem
{
    [Dependency] private NebulaContentSpawnerSystem _contentSpawner = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DebrisFeaturePlacerControllerComponent, PrePlaceDebrisFeatureEvent>(OnPrePlaceDebris);
    }

    private void OnPrePlaceDebris(Entity<DebrisFeaturePlacerControllerComponent> ent, ref PrePlaceDebrisFeatureEvent args)
    {
        if (!args.Handled && _contentSpawner.ShouldBlockOrdinaryDebris(args.Coords))
            args.Handled = true;
    }
}
