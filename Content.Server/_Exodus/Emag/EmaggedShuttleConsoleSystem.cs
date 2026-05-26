using Content.Server.Chat.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._Exodus.Emag;
using Content.Shared._Exodus.Shipyard.Utilization;
using Content.Shared.Access.Components;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Paper;
using Content.Shared.PDA;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Exodus.Emag;

/// <summary>
/// Handles emag/demag interactions on shuttle consoles.
/// Emagging a shuttle console permanently disables the grid lock on the parent ship —
/// no card or voucher can lock it again until a demag clears the state.
/// SRD-restored consoles inherit the emag state via <see cref="ShipGridLockComponent"/>,
/// which lives on the grid and survives individual console rebuilds.
/// Demagging spawns a paper into the demagger's hands with the emagger's name and job.
/// </summary>
public sealed class EmaggedShuttleConsoleSystem : EntitySystem
{
    private static readonly EntProtoId PaperProto = "Paper";

    /// <summary>
    /// Hack delay for emagging a shuttle console. The user must stand still next to it for the
    /// entire duration; the console announces the breach over local chat while the hack runs.
    /// </summary>
    private static readonly TimeSpan ShuttleConsoleEmagDelay = TimeSpan.FromSeconds(20);

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly PaperSystem _paper = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedChargesSystem _charges = default!;
    [Dependency] private readonly EmagSystem _emagSystem = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleConsoleComponent, GotEmaggedEvent>(OnEmagged);
        SubscribeLocalEvent<ShuttleConsoleComponent, GotUnEmaggedEvent>(OnUnemagged);
        SubscribeLocalEvent<NativeShuttleConsoleComponent, ComponentStartup>(OnNativeStartup);

        // Run before EmagSystem.OnAfterInteract so we can intercept shuttle-console targets and
        // start a DoAfter instead of an instant emag. EmagSystem now respects args.Handled.
        SubscribeLocalEvent<EmagComponent, AfterInteractEvent>(OnEmagAfterInteract,
            before: new[] { typeof(EmagSystem) });
        SubscribeLocalEvent<EmagComponent, ShuttleConsoleEmagDoAfterEvent>(OnEmagDoAfter);
    }

    private void OnEmagged(Entity<ShuttleConsoleComponent> ent, ref GotEmaggedEvent args)
    {
        // Only consoles that were on the ship at purchase time (or SRD-restored copies of them)
        // can mark the ship as emagged. Foreign or freshly-built consoles silently no-op so the
        // emag tool keeps its charge.
        if (!HasComp<NativeShuttleConsoleComponent>(ent))
            return;

        if (Transform(ent).GridUid is not { } gridUid)
            return;

        var gridLock = EnsureComp<ShipGridLockComponent>(gridUid);

        if (gridLock.LockDisabled)
            return;

        gridLock.LockDisabled = true;
        gridLock.Locked = false;
        gridLock.EmaggedBy = args.UserUid;
        gridLock.EmaggerName = GetEmaggerName(args.UserUid);
        gridLock.EmaggerJob = GetEmaggerJob(args.UserUid);
        gridLock.EmaggedAt = _timing.CurTime;
        Dirty(gridUid, gridLock);

        args.Handled = true;
    }

    private void OnUnemagged(Entity<ShuttleConsoleComponent> ent, ref GotUnEmaggedEvent args)
    {
        if (Transform(ent).GridUid is not { } gridUid)
            return;

        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock) || !gridLock.LockDisabled)
            return;

        var emaggerName = gridLock.EmaggerName ?? Loc.GetString("emag-paper-unknown-name");

        gridLock.LockDisabled = false;
        gridLock.EmaggedBy = null;
        gridLock.EmaggerName = null;
        gridLock.EmaggerJob = null;
        gridLock.EmaggedAt = null;
        Dirty(gridUid, gridLock);

        SpawnDemagPaper(args.UserUid, emaggerName);

        args.Handled = true;
    }

    private void SpawnDemagPaper(EntityUid user, string emaggerName)
    {
        var paper = Spawn(PaperProto, _transform.GetMapCoordinates(user));

        _meta.SetEntityName(paper, Loc.GetString("emag-paper-title"));

        if (TryComp<PaperComponent>(paper, out var paperComp))
        {
            var body = Loc.GetString("emag-paper-body", ("name", emaggerName));
            _paper.SetContent((paper, paperComp), body);
        }

        _hands.PickupOrDrop(user, paper, checkActionBlocker: false);
    }

    /// <summary>
    /// Intercept emag interactions against shuttle consoles to start a 20-second hack DoAfter
    /// instead of applying the emag instantly. Non-shuttle-console targets fall through to the
    /// regular <see cref="EmagSystem"/> flow.
    /// </summary>
    private void OnEmagAfterInteract(Entity<EmagComponent> emag, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target)
            return;

        // Demag tools still operate instantly — only emag tools get the hack delay.
        if (emag.Comp.Demag)
            return;

        if (!HasComp<ShuttleConsoleComponent>(target))
            return;

        // Foreign / freshly-built consoles can't be emagged at all — let EmagSystem run so the
        // tool's "no effect" feedback (charge check, popup) still fires.
        if (!HasComp<NativeShuttleConsoleComponent>(target))
            return;

        // Already emag-broken at the grid level — bail so charges aren't wasted on a re-hack.
        if (Transform(target).GridUid is { } gridUid
            && TryComp<ShipGridLockComponent>(gridUid, out var gridLock)
            && gridLock.LockDisabled)
        {
            args.Handled = true;
            return;
        }

        // If the emag has no charges left, let EmagSystem show its "no charges" popup naturally.
        if (TryComp<LimitedChargesComponent>(emag, out var charges) && _charges.IsEmpty(emag, charges))
            return;

        var ev = new ShuttleConsoleEmagDoAfterEvent();
        var doAfterArgs = new DoAfterArgs(EntityManager, args.User, ShuttleConsoleEmagDelay, ev, emag.Owner, target: target, used: emag.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
            DuplicateCondition = DuplicateConditions.SameTool | DuplicateConditions.SameTarget,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
            return;

        _chat.TrySendInGameICMessage(target,
            Loc.GetString("shuttle-console-emag-in-progress"),
            InGameICChatType.Speak,
            ChatTransmitRange.Normal,
            false);

        args.Handled = true;
    }

    private void OnEmagDoAfter(Entity<EmagComponent> emag, ref ShuttleConsoleEmagDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (args.Target is not { } target)
            return;

        // Sanity: the target must still be a valid, non-emagged native shuttle console.
        if (Deleted(target) || !HasComp<ShuttleConsoleComponent>(target) || !HasComp<NativeShuttleConsoleComponent>(target))
            return;

        _emagSystem.TryEmagEffect((emag.Owner, emag.Comp), args.User, target);
        args.Handled = true;
    }

    /// <summary>
    /// When a native shuttle console comes online on a grid whose lock has been emag-broken,
    /// mark this console with <see cref="EmaggedComponent"/> so future demag attempts find it.
    /// Triggered on both map-load init and SRD restoration: SRD attaches
    /// <see cref="NativeShuttleConsoleComponent"/> after the entity is spawned, which fires this
    /// hook even though MapInit has already run by then.
    /// </summary>
    private void OnNativeStartup(Entity<NativeShuttleConsoleComponent> ent, ref ComponentStartup args)
    {
        if (Transform(ent).GridUid is not { } gridUid)
            return;

        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock) || !gridLock.LockDisabled)
            return;

        var emagged = EnsureComp<EmaggedComponent>(ent.Owner);
        emagged.EmagType |= EmagType.Interaction;
        Dirty(ent.Owner, emagged);
    }

    private string GetEmaggerName(EntityUid user)
    {
        if (TryGetHeldIdCard(user, out var idCard) && !string.IsNullOrWhiteSpace(idCard.Comp.FullName))
            return idCard.Comp.FullName!;

        return Name(user);
    }

    private string GetEmaggerJob(EntityUid user)
    {
        if (TryGetHeldIdCard(user, out var idCard))
        {
            var job = idCard.Comp.LocalizedJobTitle;
            if (!string.IsNullOrWhiteSpace(job))
                return job!;
        }

        return Loc.GetString("emag-paper-unknown-job");
    }

    private bool TryGetHeldIdCard(EntityUid user, out Entity<IdCardComponent> idCard)
    {
        foreach (var hand in _hands.EnumerateHands(user))
        {
            if (hand.HeldEntity is not { } held)
                continue;

            if (TryComp<IdCardComponent>(held, out var idComp))
            {
                idCard = (held, idComp);
                return true;
            }

            if (TryComp<PdaComponent>(held, out var pda)
                && pda.ContainedId is { } pdaId
                && TryComp<IdCardComponent>(pdaId, out var pdaIdComp))
            {
                idCard = (pdaId, pdaIdComp);
                return true;
            }
        }

        idCard = default;
        return false;
    }
}
