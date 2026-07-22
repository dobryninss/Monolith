using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._Mono.FireControl;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._Exodus.FireControl;

/// <summary>
/// Bridges an entity's own gun to the existing ship fire-control BUI without requiring a grid,
/// gunnery server, anchoring, or power network.
/// </summary>
public sealed class IntrinsicFireControlSystem : EntitySystem
{
    [Dependency] private GunSystem _gun = default!;
    [Dependency] private ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<IntrinsicFireControlComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<IntrinsicFireControlComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<IntrinsicFireControlComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<IntrinsicFireControlComponent, FireControlConsoleRefreshServerMessage>(OnRefresh);
        SubscribeLocalEvent<IntrinsicFireControlComponent, FireControlConsoleFireMessage>(OnFire);
        SubscribeLocalEvent<IntrinsicFireControlWeaponComponent, ComponentShutdown>(OnWeaponShutdown);
    }

    private void OnMapInit(Entity<IntrinsicFireControlComponent> ent, ref MapInitEvent args)
    {
        foreach (var definition in ent.Comp.Weapons)
        {
            var weapon = Spawn(definition.Prototype, new EntityCoordinates(ent.Owner, definition.Offset));
            if (!HasComp<GunComponent>(weapon))
            {
                Log.Error($"Intrinsic fire-control weapon prototype {definition.Prototype} has no Gun component.");
                QueueDel(weapon);
                continue;
            }

            var weaponComponent = EnsureComp<IntrinsicFireControlWeaponComponent>(weapon);
            weaponComponent.Owner = ent.Owner;
            ent.Comp.SpawnedWeapons.Add(new IntrinsicFireControlSpawnedWeapon(
                weapon,
                definition.MaxRange,
                definition.WeaponName));
        }
    }

    private void OnShutdown(Entity<IntrinsicFireControlComponent> ent, ref ComponentShutdown args)
    {
        foreach (var weapon in ent.Comp.SpawnedWeapons)
        {
            if (!Deleted(weapon.Entity))
                QueueDel(weapon.Entity);
        }

        ent.Comp.SpawnedWeapons.Clear();
    }

    private void OnWeaponShutdown(Entity<IntrinsicFireControlWeaponComponent> ent, ref ComponentShutdown args)
    {
        if (TerminatingOrDeleted(ent.Comp.Owner)
            || !TryComp<IntrinsicFireControlComponent>(ent.Comp.Owner, out var fireControl))
        {
            return;
        }

        for (var i = fireControl.SpawnedWeapons.Count - 1; i >= 0; i--)
        {
            if (fireControl.SpawnedWeapons[i].Entity != ent.Owner)
                continue;

            fireControl.SpawnedWeapons.RemoveAt(i);
            break;
        }

        UpdateUi((ent.Comp.Owner, fireControl));
    }

    private void OnUiOpened(Entity<IntrinsicFireControlComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, FireControlConsoleUiKey.Key))
            return;

        UpdateUi(ent);
    }

    private void OnRefresh(Entity<IntrinsicFireControlComponent> ent, ref FireControlConsoleRefreshServerMessage args)
    {
        if (!Equals(args.UiKey, FireControlConsoleUiKey.Key) || args.Actor != ent.Owner)
            return;

        UpdateUi(ent);
    }

    private void OnFire(Entity<IntrinsicFireControlComponent> ent, ref FireControlConsoleFireMessage args)
    {
        if (!Equals(args.UiKey, FireControlConsoleUiKey.Key)
            || args.Actor != ent.Owner
            || args.Selected.Count == 0
            || args.Selected.Count > ent.Comp.SpawnedWeapons.Count + 1)
        {
            return;
        }

        var targetCoordinates = GetCoordinates(args.Coordinates);
        if (!targetCoordinates.IsValid(EntityManager))
            return;

        var target = _transform.ToMapCoordinates(targetCoordinates);

        for (var i = 0; i < args.Selected.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (args.Selected[i] == args.Selected[j])
                    return;
            }

            if (!TryGetEntity(args.Selected[i], out var weaponUid)
                || weaponUid is not { } weapon
                || !TryResolveWeapon(ent, weapon, out _, out _))
            {
                return;
            }
        }

        foreach (var selected in args.Selected)
        {
            if (!TryGetEntity(selected, out var weaponUid)
                || weaponUid is not { } weapon
                || !TryResolveWeapon(ent, weapon, out var gun, out var maxRange)
                || !IsInRange(weapon, target, maxRange))
            {
                continue;
            }

            _gun.AttemptShoot(ent.Owner, weapon, gun, targetCoordinates);
        }

        UpdateUi(ent);
    }

    private void UpdateUi(Entity<IntrinsicFireControlComponent> ent)
    {
        if (!_ui.HasUi(ent.Owner, FireControlConsoleUiKey.Key))
            return;

        var navState = _shuttleConsole.GetNavState(
            ent.Owner,
            _shuttleConsole.GetAllDocks(),
            _shuttleConsole.GetAllGrapLinks(),
            new EntityCoordinates(ent.Owner, Vector2.Zero),
            Angle.Zero);

        var controllables = new List<FireControllableEntry>(ent.Comp.SpawnedWeapons.Count + 1);
        if (TryComp<GunComponent>(ent.Owner, out var ownerGun))
            AddWeaponEntry(controllables, ent.Owner, ownerGun, ent.Comp.WeaponName);

        foreach (var weapon in ent.Comp.SpawnedWeapons)
        {
            if (Deleted(weapon.Entity) || !TryComp<GunComponent>(weapon.Entity, out var gun))
                continue;

            AddWeaponEntry(controllables, weapon.Entity, gun, weapon.WeaponName);
        }

        var state = new FireControlConsoleBoundInterfaceState(
            true,
            controllables.ToArray(),
            navState,
            null);

        _ui.SetUiState(ent.Owner, FireControlConsoleUiKey.Key, state);
    }

    private void AddWeaponEntry(
        List<FireControllableEntry> controllables,
        EntityUid weapon,
        GunComponent gun,
        LocId? weaponName)
    {
        var name = weaponName is { } locId
            ? Loc.GetString(locId)
            : Name(weapon);
        var controllable = new FireControllableEntry(
            GetNetEntity(weapon),
            GetNetCoordinates(Transform(weapon).Coordinates),
            name)
        {
            NextFire = gun.NextFire,
        };

        controllables.Add(controllable);
    }

    private bool TryResolveWeapon(
        Entity<IntrinsicFireControlComponent> owner,
        EntityUid weapon,
        out GunComponent gun,
        out float maxRange)
    {
        maxRange = 0f;
        gun = default!;

        if (Deleted(weapon) || !TryComp<GunComponent>(weapon, out var weaponGun))
            return false;

        gun = weaponGun;

        if (weapon == owner.Owner)
        {
            maxRange = owner.Comp.MaxRange;
            return true;
        }

        foreach (var spawned in owner.Comp.SpawnedWeapons)
        {
            if (spawned.Entity != weapon)
                continue;

            maxRange = spawned.MaxRange;
            return true;
        }

        return false;
    }

    private bool IsInRange(EntityUid weapon, MapCoordinates target, float maxRange)
    {
        if (!float.IsFinite(maxRange) || maxRange <= 0f)
            return false;

        var source = _transform.GetMapCoordinates(weapon);
        var distanceSquared = (target.Position - source.Position).LengthSquared();

        return float.IsFinite(distanceSquared)
            && source.MapId == target.MapId
            && distanceSquared >= 0.01f
            && distanceSquared <= maxRange * maxRange;
    }
}
