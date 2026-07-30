using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Server._Exodus.Nebula.Components;
using Content.Server.Worldgen.Components.Debris;
using Content.Server.Worldgen.Systems.Debris;
using Content.Server.Worldgen.Tools;
using Content.Shared._Exodus.Nebula.Prototypes;
using Content.Shared.Storage;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Exodus.Nebula.Spawning;

/// <summary>
/// Replaces the ordinary debris selection inside configured nebulas with profile-driven content.
/// Selection runs only after the ordinary worldgen pre-filters have accepted a point.
/// </summary>
public sealed class NebulaContentSpawnerSystem : EntitySystem
{
    [Dependency] private SharedMapSystem _mapSystem = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IRobustRandom _random = default!;

    private readonly Dictionary<string, List<NebulaContentSpawnProfilePrototype>> _profilesByMarker = new();
    private readonly Dictionary<string, EntitySpawnCollectionCache> _spawnCaches = new();
    private readonly HashSet<string> _selectableProfiles = new();
    private readonly HashSet<string> _invalidProfileWarnings = new();
    private List<string?> _spawnBuffer = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<DebrisFeaturePlacerControllerComponent, TryGetPlaceableDebrisFeatureEvent>(
            OnTryGetPlaceableDebris,
            after: new[] { typeof(DebrisFeaturePlacerSystem), typeof(NoiseDrivenDebrisSelectorSystem) });
        BuildProfileCache();
    }

    /// <summary>
    /// Returns true only when ordinary debris must be cancelled before selection. Valid nebula
    /// profiles stay unhandled here so all worldgen carvers can finish their pre-checks.
    /// </summary>
    public bool ShouldBlockOrdinaryDebris(EntityCoordinates coordinates)
    {
        if (!TryGetNebulaProfileAt(coordinates, out var profile))
            return false;

        // Death-zone sub-zones and blob nebulas without a profile intentionally spawn nothing.
        if (profile == null)
            return true;

        if (_selectableProfiles.Contains(profile.ID))
            return false;

        LogInvalidProfile(profile);
        return true;
    }

    private void OnTryGetPlaceableDebris(
        Entity<DebrisFeaturePlacerControllerComponent> ent,
        ref TryGetPlaceableDebrisFeatureEvent args)
    {
        if (!TryGetNebulaProfileAt(args.Coords, out var profile) || profile == null)
            return;

        if (!TryGetSpawn(profile, out var prototype))
        {
            // ShouldBlockOrdinaryDebris has already rejected this profile in the pre-event.
            LogInvalidProfile(profile);
            return;
        }

        // This is ordered after the ordinary selectors, so a nebula profile always replaces
        // their choice without bypassing any pre-placement carvers.
        args.DebrisProto = prototype;
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NebulaContentSpawnProfilePrototype>() || args.WasModified<EntityPrototype>())
            BuildProfileCache();
    }

    private void BuildProfileCache()
    {
        _profilesByMarker.Clear();
        _spawnCaches.Clear();
        _selectableProfiles.Clear();
        _invalidProfileWarnings.Clear();

        foreach (var profile in _prototype.EnumeratePrototypes<NebulaContentSpawnProfilePrototype>())
        {
            for (var i = 0; i < profile.Markers.Count; i++)
            {
                if (profile.Markers[i].Id is not { } markerId)
                    continue;

                if (!_profilesByMarker.TryGetValue(markerId, out var profiles))
                {
                    profiles = new List<NebulaContentSpawnProfilePrototype>();
                    _profilesByMarker.Add(markerId, profiles);
                }

                profiles.Add(profile);
            }

            BuildSpawnCache(profile);
        }
    }

    private void BuildSpawnCache(NebulaContentSpawnProfilePrototype profile)
    {
        var validEntries = new List<EntitySpawnEntry>();
        for (var i = 0; i < profile.Spawns.Count; i++)
        {
            var entry = profile.Spawns[i];
            if (entry.Amount <= 0 ||
                entry.PrototypeId is not { } prototype ||
                !_prototype.HasIndex<EntityPrototype>(prototype))
            {
                continue;
            }

            validEntries.Add(entry);
        }

        _spawnCaches.Add(profile.ID, new EntitySpawnCollectionCache(validEntries));
        if (validEntries.Count > 0)
            _selectableProfiles.Add(profile.ID);
    }

    private bool TryGetNebulaProfileAt(
        EntityCoordinates coordinates,
        out NebulaContentSpawnProfilePrototype? profile)
    {
        var mapCoordinates = _transform.ToMapCoordinates(coordinates);
        if (mapCoordinates.MapId == MapId.Nullspace ||
            !_mapSystem.TryGetMap(mapCoordinates.MapId, out var mapUid) ||
            !TryComp<NebulaMapComponent>(mapUid, out var map))
        {
            profile = null;
            return false;
        }

        for (var i = 0; i < map.Nebulas.Count && i < map.NebulaPrototypes.Count; i++)
        {
            var nebula = map.Nebulas[i];
            var delta = mapCoordinates.Position - nebula.Center;
            if (delta.LengthSquared() > nebula.BoundingRadius * nebula.BoundingRadius ||
                !nebula.Contains(mapCoordinates.Position))
            {
                continue;
            }

            TryResolveProfile(map.NebulaPrototypes[i], nebula.Center.Length(), out profile);
            return true;
        }

        if (map.WorldEnd.IsGenerated && map.WorldEnd.TryGetZone(mapCoordinates.Position, out _))
        {
            profile = null;
            return true;
        }

        profile = null;
        return false;
    }

    private bool TryResolveProfile(
        EntProtoId marker,
        float distance,
        [NotNullWhen(true)] out NebulaContentSpawnProfilePrototype? profile)
    {
        profile = null;
        if (marker.Id is not { } markerId ||
            !_profilesByMarker.TryGetValue(markerId, out var profiles))
        {
            return false;
        }

        for (var i = 0; i < profiles.Count; i++)
        {
            var candidate = profiles[i];
            if (distance < candidate.DistanceRange.X ||
                distance >= candidate.DistanceRange.Y)
            {
                continue;
            }

            profile = candidate;
            return true;
        }

        return false;
    }

    private bool TryGetSpawn(
        NebulaContentSpawnProfilePrototype profile,
        [NotNullWhen(true)] out string? prototype)
    {
        prototype = null;
        _spawnBuffer.Clear();

        if (!_spawnCaches.TryGetValue(profile.ID, out var spawnCache))
            return false;

        spawnCache.GetSpawns(_random, ref _spawnBuffer);
        for (var i = 0; i < _spawnBuffer.Count; i++)
        {
            var candidate = _spawnBuffer[i];
            if (string.IsNullOrEmpty(candidate))
                continue;

            prototype = candidate;
            return true;
        }

        return false;
    }

    private void LogInvalidProfile(NebulaContentSpawnProfilePrototype profile)
    {
        if (_invalidProfileWarnings.Add(profile.ID))
        {
            Log.Error($"Nebula content profile {profile.ID} has no valid entity prototypes; ordinary debris remains blocked.");
        }
    }
}
