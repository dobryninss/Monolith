using Content.Server.Shuttles.Systems;
using Content.Shared._Exodus.Nebula.Components;
using Content.Shared._Mono.CCVar;
using Prometheus;
using Robust.Server.DataMetrics;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Exodus.Metrics;

/// <summary>
/// Collects grid counts on the main thread when metrics are requested, without a per-tick scan.
/// </summary>
public sealed class GridMetricsSystem : EntitySystem
{
    private static readonly Gauge _gridCount = Prometheus.Metrics.CreateGauge(
        "exodus_grids_count",
        "Number of initialized grids on all maps, including paused grids and excluding map entities.");

    private static readonly Gauge _smallGridCount = Prometheus.Metrics.CreateGauge(
        "exodus_small_grids_count",
        "Number of grids at or below mono.grid_cleanup_aggressive_tiles using the cleanup fixture-mass estimate, including paused grids.");

    private static readonly Gauge _activeNebulaGridCount = Prometheus.Metrics.CreateGauge(
        "exodus_nebula_active_grids_count",
        "Number of unpaused grids with active nebula presence, including world-end zones.");

    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IMetricsManager _metrics = default!;

    private EntityQuery<MapComponent> _mapQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;
    private EntityQuery<NebulaPresenceComponent> _presenceQuery;
    private bool _metricsEnabled;
    private float _smallGridMaxMass;

    public override void Initialize()
    {
        base.Initialize();

        _mapQuery = GetEntityQuery<MapComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        _presenceQuery = GetEntityQuery<NebulaPresenceComponent>();

        Subs.CVar(_config, CVars.MetricsEnabled, value => _metricsEnabled = value, true);
        Subs.CVar(_config, MonoCVars.GridCleanupAggressiveTiles,
            value => _smallGridMaxMass = value * ShuttleSystem.TileDensityMultiplier, true);

        _metrics.UpdateMetrics += UpdateMetrics;
    }

    public override void Shutdown()
    {
        _metrics.UpdateMetrics -= UpdateMetrics;
        base.Shutdown();
    }

    private void UpdateMetrics()
    {
        if (!_metricsEnabled)
            return;

        var gridCount = 0;
        var smallGridCount = 0;
        var activeNebulaGridCount = 0;

        // Paused grids still contribute to the total and fragment counts, but not active nebula processing.
        var query = AllEntityQuery<MapGridComponent, MetaDataComponent>();
        while (query.MoveNext(out var uid, out _, out var metadata))
        {
            if (metadata.EntityLifeStage < EntityLifeStage.Initialized ||
                metadata.EntityLifeStage >= EntityLifeStage.Terminating ||
                _mapQuery.HasComp(uid))
            {
                continue;
            }

            gridCount++;

            // Match GridCleanupSystem's size estimate without enumerating tiles or checking cleanup eligibility.
            if (_physicsQuery.TryComp(uid, out var physics) && physics.FixturesMass <= _smallGridMaxMass)
                smallGridCount++;

            // A cleared presence can remain until deferred removal. World-end presence also uses index -1.
            if (!metadata.EntityPaused &&
                _presenceQuery.TryComp(uid, out var presence) &&
                presence.Running &&
                presence.Marker != default)
            {
                activeNebulaGridCount++;
            }
        }

        // Always publish zero as well, so deleting the last grid or restarting a round cannot leave stale counts.
        _gridCount.Set(gridCount);
        _smallGridCount.Set(smallGridCount);
        _activeNebulaGridCount.Set(activeNebulaGridCount);
    }
}
