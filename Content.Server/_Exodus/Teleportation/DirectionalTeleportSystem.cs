using System.Numerics;
using Content.Server._Mono.Worldgen.Components;
using Content.Shared._Exodus.Teleportation;
using Content.Shared.DoAfter;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Spawners;

namespace Content.Server._Exodus.Teleportation;

/// <summary>
/// Handles configurable forward teleports and avoids destinations occupied by grids.
/// </summary>
public sealed partial class DirectionalTeleportSystem : EntitySystem
{
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DirectionalTeleportComponent, DirectionalTeleportActionEvent>(OnTeleportAction);
        SubscribeLocalEvent<DirectionalTeleportComponent, DirectionalTeleportDoAfterEvent>(OnTeleportDoAfter);
        SubscribeLocalEvent<DirectionalTeleportComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnTeleportAction(
        Entity<DirectionalTeleportComponent> ent,
        ref DirectionalTeleportActionEvent args)
    {
        if (args.Handled || ent.Comp.Charging || ent.Comp.Distance <= 0f)
            return;

        var xform = Transform(ent);
        if (xform.MapID == MapId.Nullspace)
            return;

        var direction = _transform.GetWorldRotation(xform).ToWorldVec();
        if (direction.LengthSquared() <= float.Epsilon)
            return;

        direction = Vector2.Normalize(direction);
        var origin = _transform.GetMapCoordinates((ent.Owner, xform));
        var primaryDestination = origin.Offset(direction * ent.Comp.Distance);
        var loader = Spawn(null, primaryDestination);

        var chunkLoader = EnsureComp<ChunkLoaderComponent>(loader);
        chunkLoader.LoadingDistance = GetLoadingDistance(ent, ent.Comp);

        var timedDespawn = EnsureComp<TimedDespawnComponent>(loader);
        timedDespawn.Lifetime = (float) ent.Comp.PreparationTime.TotalSeconds + 2f;

        ent.Comp.Charging = true;
        ent.Comp.ChunkLoader = loader;
        ent.Comp.PendingMap = origin.MapId;
        ent.Comp.PendingOrigin = origin.Position;
        ent.Comp.PendingDirection = direction;

        var doAfterArgs = new DoAfterArgs(
            EntityManager,
            ent.Owner,
            ent.Comp.PreparationTime,
            new DirectionalTeleportDoAfterEvent(),
            ent.Owner)
        {
            Hidden = false,
            MultiplyDelay = false,
            NeedHand = false,
            BreakOnMove = false,
            BreakOnDamage = false,
            RequireCanInteract = false,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
        {
            ResetPendingTeleport(ent);
            return;
        }

        args.Handled = true;
    }

    private void OnTeleportDoAfter(
        Entity<DirectionalTeleportComponent> ent,
        ref DirectionalTeleportDoAfterEvent args)
    {
        if (args.Handled)
            return;

        var mapId = ent.Comp.PendingMap;
        var origin = ent.Comp.PendingOrigin;
        var direction = ent.Comp.PendingDirection;
        ResetPendingTeleport(ent);

        if (args.Cancelled || mapId == MapId.Nullspace)
            return;

        var xform = Transform(ent);
        if (xform.MapID != mapId)
            return;

        if (!TryFindDestination(ent, mapId, origin, direction, out var destination))
        {
            if (ent.Comp.BlockedPopup is { } popup)
                _popup.PopupEntity(Loc.GetString(popup), ent, ent);

            args.Handled = true;
            return;
        }

        _transform.SetMapCoordinates((ent.Owner, xform), new MapCoordinates(destination, mapId));

        if (ent.Comp.StopLinearVelocity && TryComp<PhysicsComponent>(ent, out var physics))
            _physics.SetLinearVelocity(ent, Vector2.Zero, body: physics);

        args.Handled = true;
    }

    private void OnShutdown(Entity<DirectionalTeleportComponent> ent, ref ComponentShutdown args)
    {
        ResetPendingTeleport(ent);
    }

    private bool TryFindDestination(
        Entity<DirectionalTeleportComponent> ent,
        MapId mapId,
        Vector2 origin,
        Vector2 direction,
        out Vector2 destination)
    {
        if (TryDestination(ent, mapId, origin, direction, ent.Comp.Distance, out destination))
            return true;

        foreach (var offset in ent.Comp.AlternativeDistanceOffsets)
        {
            if (TryDestination(ent, mapId, origin, direction, ent.Comp.Distance + offset, out destination))
                return true;
        }

        destination = default;
        return false;
    }

    private bool TryDestination(
        EntityUid uid,
        MapId mapId,
        Vector2 origin,
        Vector2 direction,
        float distance,
        out Vector2 destination)
    {
        destination = origin + direction * distance;
        if (distance <= 0f)
            return false;

        var currentPosition = _transform.GetWorldPosition(uid);
        var bounds = GetWorldBounds(uid, currentPosition).Translated(destination - currentPosition);

        foreach (var _ in _mapManager.FindGridsIntersecting(mapId, bounds))
        {
            return false;
        }

        return true;
    }

    private int GetLoadingDistance(
        EntityUid uid,
        DirectionalTeleportComponent component)
    {
        var maxOffset = 0f;
        foreach (var offset in component.AlternativeDistanceOffsets)
        {
            maxOffset = MathF.Max(maxOffset, MathF.Abs(offset));
        }

        var position = _transform.GetWorldPosition(uid);
        var bounds = GetWorldBounds(uid, position);
        var bodyRadius = MathF.Max(bounds.Size.X, bounds.Size.Y) / 2f;
        return (int) MathF.Ceiling(maxOffset + bodyRadius);
    }

    private Box2 GetWorldBounds(EntityUid uid, Vector2 position)
    {
        if (TryComp<PhysicsComponent>(uid, out var physics) &&
            TryComp<FixturesComponent>(uid, out var fixtures))
        {
            return _physics.GetWorldAABB(uid, fixtures, physics);
        }

        return new Box2(position, position);
    }

    private void ResetPendingTeleport(Entity<DirectionalTeleportComponent> ent)
    {
        if (ent.Comp.ChunkLoader is { } loader && !TerminatingOrDeleted(loader))
            QueueDel(loader);

        ent.Comp.Charging = false;
        ent.Comp.ChunkLoader = null;
        ent.Comp.PendingMap = MapId.Nullspace;
        ent.Comp.PendingOrigin = default;
        ent.Comp.PendingDirection = default;
    }
}
