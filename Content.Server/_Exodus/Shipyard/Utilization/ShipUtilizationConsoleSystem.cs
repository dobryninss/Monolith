using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Chat.Systems;
using Content.Server.Mind;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared._Exodus.Shipyard.Utilization;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Chat;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Radio;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Exodus.Shipyard.Utilization;

/// <summary>
/// Drives the ship utilization console.
/// Lists emagged ships docked to Camelot, runs a 5-minute timer with periodic organic checks,
/// pauses on detected life, and on completion deletes the ship grid while spawning a cash payout.
/// </summary>
public sealed class ShipUtilizationConsoleSystem : EntitySystem
{
    /// <summary>
    /// Multiplier applied to the appraisal of a ship to compute the utilization payout.
    /// Owner-driven shipyard sales pay 0.85 × appraisal; utilization pays 25 percentage points
    /// less (absolute).
    /// </summary>
    private const float UtilizationSaleRate = 0.60f;

    private const string CashProtoId = "SpaceCash";

    /// <summary>
    /// How long a utilization process runs before completing.
    /// </summary>
    private static readonly TimeSpan UtilizationDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cadence at which we re-push the UI state for an active console.
    /// </summary>
    private static readonly TimeSpan UiTickInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often we scan the ship for organic life while utilization is running.
    /// </summary>
    private static readonly TimeSpan OrganicCheckInterval = TimeSpan.FromSeconds(10);

    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly DockingSystem _docking = default!;
    [Dependency] private readonly MindSystem _mind = default!;
    [Dependency] private readonly PricingSystem _pricing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShipUtilizationConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ShipUtilizationConsoleComponent, ShipUtilizationStartMessage>(OnStart);
        SubscribeLocalEvent<ShipUtilizationConsoleComponent, ShipUtilizationCancelMessage>(OnCancel);

        SubscribeLocalEvent<UndockEvent>(OnAnyUndock);
        SubscribeLocalEvent<ShuttleConsoleComponent, EntityTerminatingEvent>(OnShuttleConsoleTerminating);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ShipUtilizationConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.ActiveShip is not { } shipUid || comp.ActiveEndsAt is not { } endsAt)
                continue;

            // The ship lost its emag flag (demagged) — abort.
            if (!TryComp<ShipGridLockComponent>(shipUid, out var gridLock) || !gridLock.LockDisabled)
            {
                AbortUtilization((uid, comp), "ship-utilization-announce-cancel-demag");
                continue;
            }

            // Freeze the deadline while paused so the displayed countdown doesn't tick.
            if (comp.Paused)
            {
                comp.ActiveEndsAt = endsAt + TimeSpan.FromSeconds(frameTime);
                endsAt = comp.ActiveEndsAt.Value;
            }

            if (now >= comp.NextOrganicCheck)
            {
                comp.NextOrganicCheck = now + OrganicCheckInterval;
                HandleOrganicCheck((uid, comp), shipUid);
            }

            if (!comp.Paused && now >= endsAt)
            {
                if (FoundOrganics(shipUid) != null)
                {
                    BeginPause((uid, comp));
                    continue;
                }

                CompleteUtilization((uid, comp));
                continue;
            }

            if (now >= comp.NextUiUpdate)
            {
                comp.NextUiUpdate = now + UiTickInterval;
                RefreshState((uid, comp));
            }
        }
    }

    private void OnUiOpened(Entity<ShipUtilizationConsoleComponent> ent, ref BoundUIOpenedEvent args)
    {
        RefreshState(ent);
    }

    private void OnStart(Entity<ShipUtilizationConsoleComponent> ent, ref ShipUtilizationStartMessage args)
    {
        if (ent.Comp.ActiveShip != null)
            return;

        var requested = args.Ship;
        if (!TryGetEntity(requested, out var shipUid))
            return;

        var ships = GetEligibleShips(ent);
        var found = ships.FirstOrDefault(s => s.Ship == requested);
        if (found.Ship == default || found.LockedByOtherConsole)
            return;

        var now = _timing.CurTime;
        ent.Comp.ActiveShip = shipUid;
        ent.Comp.ActiveStartedAt = now;
        ent.Comp.ActiveEndsAt = now + UtilizationDuration;
        ent.Comp.ActivePayout = CalculatePayout(shipUid.Value);
        ent.Comp.ActiveShipName = Name(shipUid.Value);
        ent.Comp.NextUiUpdate = now + UiTickInterval;
        ent.Comp.NextOrganicCheck = now + OrganicCheckInterval;
        ent.Comp.Paused = false;

        Announce(ent, "ship-utilization-announce-start",
            ("vessel", ent.Comp.ActiveShipName!),
            ("station", GetConsoleStationName(ent)));

        _audio.PlayPvs(ent.Comp.ConfirmSound, ent.Owner);
        RefreshState(ent);
    }

    private void OnCancel(Entity<ShipUtilizationConsoleComponent> ent, ref ShipUtilizationCancelMessage args)
    {
        if (ent.Comp.ActiveShip == null)
            return;

        AbortUtilization(ent, "ship-utilization-announce-cancel-operator");
    }

    private void OnAnyUndock(UndockEvent args)
    {
        CancelIfShipMatches(args.GridAUid, "ship-utilization-announce-cancel-undock");
        CancelIfShipMatches(args.GridBUid, "ship-utilization-announce-cancel-undock");
    }

    /// <summary>
    /// If a native shuttle console of the ship being utilized is destroyed, abort the process.
    /// Non-native consoles (foreign or freshly built) don't count.
    /// </summary>
    private void OnShuttleConsoleTerminating(Entity<ShuttleConsoleComponent> ent, ref EntityTerminatingEvent args)
    {
        if (!HasComp<NativeShuttleConsoleComponent>(ent))
            return;

        if (Transform(ent).GridUid is not { } gridUid)
            return;

        CancelIfShipMatches(gridUid, "ship-utilization-announce-cancel-console");
    }

    private void CancelIfShipMatches(EntityUid shipUid, string locKey)
    {
        var query = EntityQueryEnumerator<ShipUtilizationConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.ActiveShip != shipUid)
                continue;

            AbortUtilization((uid, comp), locKey);
        }
    }

    private void AbortUtilization(Entity<ShipUtilizationConsoleComponent> ent, string locKey)
    {
        var name = ent.Comp.ActiveShipName ?? string.Empty;
        Announce(ent, locKey, ("vessel", name));
        _audio.PlayPvs(ent.Comp.ErrorSound, ent.Owner);
        ClearActive(ent.Comp);
        RefreshState(ent);
    }

    private void HandleOrganicCheck(Entity<ShipUtilizationConsoleComponent> ent, EntityUid shipUid)
    {
        var hasOrganic = FoundOrganics(shipUid) is { };

        if (hasOrganic && !ent.Comp.Paused)
            BeginPause(ent);
        else if (!hasOrganic && ent.Comp.Paused)
            EndPause(ent);
    }

    private void BeginPause(Entity<ShipUtilizationConsoleComponent> ent)
    {
        ent.Comp.Paused = true;

        Announce(ent, "ship-utilization-announce-pause",
            ("vessel", ent.Comp.ActiveShipName ?? string.Empty));
        RefreshState(ent);
    }

    private void EndPause(Entity<ShipUtilizationConsoleComponent> ent)
    {
        ent.Comp.Paused = false;
        Announce(ent, "ship-utilization-announce-resume",
            ("vessel", ent.Comp.ActiveShipName ?? string.Empty));
        RefreshState(ent);
    }

    private void CompleteUtilization(Entity<ShipUtilizationConsoleComponent> ent)
    {
        if (ent.Comp.ActiveShip is not { } shipUid)
            return;

        var payout = ent.Comp.ActivePayout;
        var name = ent.Comp.ActiveShipName ?? string.Empty;

        Announce(ent, "ship-utilization-announce-finish", ("vessel", name));
        _audio.PlayPvs(ent.Comp.ConfirmSound, ent.Owner);

        SpawnPayout(ent.Owner, payout);
        StripDeeds(shipUid);

        if (_station.GetOwningStation(shipUid) is { Valid: true } station)
            _station.DeleteStation(station);

        QueueDel(shipUid);

        ClearActive(ent.Comp);
        RefreshState(ent);
    }

    private void SpawnPayout(EntityUid console, int amount)
    {
        if (amount <= 0)
            return;

        var coords = Transform(console).Coordinates;
        var cash = Spawn(CashProtoId, coords);
        _stack.SetCount(cash, amount);
    }

    /// <summary>
    /// Removes the deed from the ship grid itself and from every ID card that points to this ship.
    /// </summary>
    private void StripDeeds(EntityUid shipUid)
    {
        var query = EntityQueryEnumerator<ShuttleDeedComponent>();
        while (query.MoveNext(out var uid, out var deed))
        {
            if (deed.ShuttleUid == shipUid)
                RemComp<ShuttleDeedComponent>(uid);
        }
    }

    private static void ClearActive(ShipUtilizationConsoleComponent comp)
    {
        comp.ActiveShip = null;
        comp.ActiveStartedAt = null;
        comp.ActiveEndsAt = null;
        comp.ActivePayout = 0;
        comp.ActiveShipName = null;
        comp.Paused = false;
    }

    private void RefreshState(Entity<ShipUtilizationConsoleComponent> ent)
    {
        var ships = GetEligibleShips(ent);

        var isActive = ent.Comp.ActiveShip != null;
        var remaining = 0;
        var total = (int)UtilizationDuration.TotalSeconds;

        if (isActive && ent.Comp.ActiveEndsAt is { } endsAt)
        {
            var diff = endsAt - _timing.CurTime;
            remaining = diff.TotalSeconds > 0 ? (int)Math.Ceiling(diff.TotalSeconds) : 0;
        }

        var state = new ShipUtilizationConsoleInterfaceState(
            ships,
            isActive: isActive,
            activeShip: ent.Comp.ActiveShip is { } activeUid ? GetNetEntity(activeUid) : null,
            activeShipName: ent.Comp.ActiveShipName,
            activeSecondsRemaining: remaining,
            activeTotalSeconds: total,
            activePayout: ent.Comp.ActivePayout);

        _ui.SetUiState(ent.Owner, ShipUtilizationConsoleUiKey.Key, state);
    }

    /// <summary>
    /// Computes the utilization payout — <see cref="UtilizationSaleRate"/> of the grid appraisal.
    /// </summary>
    public int CalculatePayout(EntityUid shipGrid)
    {
        var appraisal = _pricing.AppraiseGrid(shipGrid, null);
        return (int)(appraisal * UtilizationSaleRate);
    }

    /// <summary>
    /// Walks the docks on the console's own grid (Camelot) to find docked ships whose grid lock has
    /// been emag-broken and which still own at least one native shuttle console.
    /// </summary>
    private List<UtilizationShipEntry> GetEligibleShips(Entity<ShipUtilizationConsoleComponent> ent)
    {
        var result = new List<UtilizationShipEntry>();

        if (Transform(ent.Owner).GridUid is not { } consoleGrid)
            return result;

        var seen = new HashSet<EntityUid>();
        var consoleDocks = _docking.GetDocks(consoleGrid);

        foreach (var dock in consoleDocks)
        {
            if (dock.Comp.DockedWith is not { } otherDock)
                continue;

            if (Transform(otherDock).GridUid is not { } shipGrid || shipGrid == consoleGrid)
                continue;

            if (!seen.Add(shipGrid))
                continue;

            if (!TryComp<ShipGridLockComponent>(shipGrid, out var gridLock) || !gridLock.LockDisabled)
                continue;

            if (!HasNativeShuttleConsole(shipGrid, gridLock.ShuttleId))
                continue;

            result.Add(new UtilizationShipEntry(
                Ship: GetNetEntity(shipGrid),
                Name: Name(shipGrid),
                Payout: CalculatePayout(shipGrid),
                LockedByOtherConsole: IsShipActiveOnAnyConsole(shipGrid, ent.Owner)));
        }

        return result;
    }

    private bool HasNativeShuttleConsole(EntityUid shipGrid, string? expectedShuttleId)
    {
        if (string.IsNullOrEmpty(expectedShuttleId))
            return false;

        var query = EntityQueryEnumerator<ShuttleConsoleComponent, ShuttleConsoleLockComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var lockComp, out var xform))
        {
            if (xform.GridUid != shipGrid)
                continue;

            if (lockComp.ShuttleId == expectedShuttleId)
                return true;
        }

        return false;
    }

    private bool IsShipActiveOnAnyConsole(EntityUid shipGrid, EntityUid exceptConsole)
    {
        var query = EntityQueryEnumerator<ShipUtilizationConsoleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (uid == exceptConsole)
                continue;

            if (comp.ActiveShip == shipGrid)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Local copy of ShipyardSystem.FoundOrganics — recursively searches for any entity carrying
    /// a Mind (player or formerly-played) under the ship grid. Ghosts are ignored.
    /// </summary>
    private EntityUid? FoundOrganics(EntityUid root)
    {
        var xform = Transform(root);
        var enumerator = xform.ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            if (HasComp<GhostComponent>(child))
                continue;

            if (_mind.TryGetMind(child, out _, out _))
                return child;

            if (FoundOrganics(child) is { } nested)
                return nested;
        }
        return null;
    }

    private string GetConsoleStationName(Entity<ShipUtilizationConsoleComponent> ent)
    {
        if (_station.GetOwningStation(ent.Owner) is { Valid: true } station)
            return Name(station);

        return Transform(ent.Owner).GridUid is { } grid ? Name(grid) : string.Empty;
    }

    private void Announce(Entity<ShipUtilizationConsoleComponent> ent, string locKey, params (string, object)[] args)
    {
        var message = Loc.GetString(locKey, args);

        _chat.TrySendInGameICMessage(ent.Owner, message, InGameICChatType.Speak, ChatTransmitRange.Normal, false);

        if (_proto.TryIndex<RadioChannelPrototype>(ent.Comp.AnnouncementChannel, out var channel))
            _radio.SendRadioMessage(ent.Owner, message, channel, ent.Owner);
    }
}
