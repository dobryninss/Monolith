using Content.Server.Administration.Logs;
using Content.Server.Destructible;
using Content.Shared._Exodus.Genetics;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Item;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Prying.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Exodus.Genetics;

[RegisterComponent]
public sealed partial class GeneticallyConsumedComponent : Component;

public sealed partial class GeneticAbilitiesSystem
{
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly DestructibleSystem _destructible = default!;
    [Dependency] private readonly HungerSystem _hunger = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly TileSystem _tiles = default!;
    [Dependency] private readonly SharedMapSystem _maps = default!;
    [Dependency] private readonly IAdminLogManager _admin = default!;

    private void InitializeDevouring()
    {
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticDevourEvent>(OnDevour);
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticDevourDoAfterEvent>(OnDevoured);
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticEatTileEvent>(OnEatTile);
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticEatTileDoAfterEvent>(OnTileEaten);
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticPryEvent>(OnPry);
        SubscribeLocalEvent<GeneticEffectsComponent, GeneticPryDoAfterEvent>(OnPried);
    }

    private bool CanEat(EntityUid user, EntityUid target)
    {
        return user != target && CanUse(user, GeneticAbility.Devour) && !TerminatingOrDeleted(target) &&
               !HasComp<GeneticallyConsumedComponent>(target) && !HasComp<MapGridComponent>(target) && !HasComp<MapComponent>(target) &&
               Transform(target).MapUid != null && _interaction.IsAccessible(user, target) &&
               _interaction.InRangeUnobstructed(user, target);
    }

    private void OnDevour(Entity<GeneticEffectsComponent> ent, ref GeneticDevourEvent args)
    {
        if (args.Handled || !CanEat(ent, args.Target))
            return;
        var state = EnsureComp<GeneticAbilityStateComponent>(ent);
        var time = HasComp<MobStateComponent>(args.Target) ? state.MobDevourTime
            : HasComp<ItemComponent>(args.Target) ? state.DevourTime : state.StructureDevourTime;
        Reveal(ent);
        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, ent, time,
            new GeneticDevourDoAfterEvent(), ent, args.Target)
        {
            BreakOnMove = true, BreakOnDamage = true, DistanceThreshold = 1.5f,
        });
    }

    private void OnDevoured(Entity<GeneticEffectsComponent> ent, ref GeneticDevourDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } target || !CanEat(ent, target))
            return;
        args.Handled = true;
        AddComp<GeneticallyConsumedComponent>(target); // Completion of a second eater in the same tick cannot pay out twice.
        Reveal(ent);
        _admin.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(ent):user} genetically consumed {ToPrettyString(target):target}");

        // Consume the shell/body, leaving contained equipment and organs in the world.
        // Destruction events still run; digestion intentionally does not produce construction salvage.
        if (TryComp<ContainerManagerComponent>(target, out var manager))
        {
            var containers = new List<BaseContainer>();
            foreach (var container in _containers.GetAllContainers(target, manager))
                containers.Add(container);
            foreach (var container in containers)
                _containers.EmptyContainer(container, true, Transform(target).Coordinates);
        }
        if (!TerminatingOrDeleted(target))
            _destructible.DestroyEntity(target, ent);
        var state = EnsureComp<GeneticAbilityStateComponent>(ent);
        _hunger.ModifyHunger(ent, state.DevourNutrition);
    }

    private void OnEatTile(Entity<GeneticEffectsComponent> ent, ref GeneticEatTileEvent args)
    {
        if (args.Handled || !CanUse(ent, GeneticAbility.Devour) ||
            !_interaction.InRangeUnobstructed(ent, args.Target) || !_turf.TryGetTileRef(args.Target, out var tile) || tile.Value.Tile.IsEmpty)
            return;
        var state = EnsureComp<GeneticAbilityStateComponent>(ent);
        if (state.EatingGrid != null)
            return;
        var ev = new GeneticEatTileDoAfterEvent { Indices = tile.Value.GridIndices, TileType = tile.Value.Tile.TypeId };
        state.EatingGrid = tile.Value.GridUid;
        Reveal(ent);
        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, ent, state.StructureDevourTime, ev, ent)
        {
            BreakOnMove = true, BreakOnDamage = true,
        });
        if (!args.Handled)
            state.EatingGrid = null;
    }

    private void OnTileEaten(Entity<GeneticEffectsComponent> ent, ref GeneticEatTileDoAfterEvent args)
    {
        if (!TryComp<GeneticAbilityStateComponent>(ent, out var state))
            return;
        var grid = state.EatingGrid;
        state.EatingGrid = null;
        if (args.Handled || args.Cancelled || grid == null || !CanUse(ent, GeneticAbility.Devour) ||
            !TryComp<MapGridComponent>(grid, out var mapGrid))
            return;
        var coordinates = _maps.GridTileToLocal(grid.Value, mapGrid, args.Indices);
        if (!_interaction.InRangeUnobstructed(ent, coordinates))
            return;
        var tile = _maps.GetTileRef(grid.Value, mapGrid, args.Indices);
        if (tile.Tile.TypeId != args.TileType || !_tiles.TryConsumeTile(tile))
            return;
        args.Handled = true;
        Reveal(ent);
        _hunger.ModifyHunger(ent, state.DevourNutrition);
        _admin.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(ent):user} genetically consumed tile {args.Indices} on {ToPrettyString(grid.Value)}");
    }

    private bool CanPry(EntityUid user, EntityUid target)
    {
        if (!CanUse(user, GeneticAbility.ForcePry) || TerminatingOrDeleted(target) ||
            !_interaction.IsAccessible(user, target) || !_interaction.InRangeUnobstructed(user, target) ||
            !HasComp<Content.Shared.Doors.Components.DoorComponent>(target))
            return false;
        var attempt = new BeforePryEvent(user, true, true, true);
        RaiseLocalEvent(target, ref attempt);
        return !attempt.Cancelled;
    }

    private void OnPry(Entity<GeneticEffectsComponent> ent, ref GeneticPryEvent args)
    {
        if (args.Handled || !CanPry(ent, args.Target))
            return;
        Reveal(ent);
        var state = EnsureComp<GeneticAbilityStateComponent>(ent);
        args.Handled = _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, ent, state.PryTime,
            new GeneticPryDoAfterEvent(), ent, args.Target)
        {
            BreakOnMove = true, BreakOnDamage = true, DistanceThreshold = 1.5f,
        });
    }

    private void OnPried(Entity<GeneticEffectsComponent> ent, ref GeneticPryDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled || args.Target is not { } target || !CanPry(ent, target))
            return;
        args.Handled = true;
        Reveal(ent);
        var ev = new PriedEvent(ent);
        RaiseLocalEvent(target, ref ev);
        _admin.Add(LogType.Action, LogImpact.High, $"{ToPrettyString(ent):user} genetically pried {ToPrettyString(target):target}");
    }
}
