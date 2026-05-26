using Content.Server.Chat.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared._Exodus.Emag;
using Content.Shared._Exodus.Shipyard.Utilization;
using Content.Shared.Access.Components;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Hands.EntitySystems;
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
    [Dependency] private readonly EmagSystem _emagSystem = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    /// <summary>
    /// Consoles whose <see cref="GotEmaggedEvent"/> is being raised as the completion of a hack
    /// DoAfter, not as a fresh interaction. Cleared by <see cref="OnEmagged"/> on the second pass.
    /// </summary>
    private readonly HashSet<EntityUid> _pendingApplications = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleConsoleComponent, GotEmaggedEvent>(OnEmagged);
        SubscribeLocalEvent<ShuttleConsoleComponent, GotUnEmaggedEvent>(OnUnemagged);
        SubscribeLocalEvent<NativeShuttleConsoleComponent, ComponentStartup>(OnNativeStartup);
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

        // If this isn't the DoAfter-completion pass, kick off the hack instead of applying state.
        // The emag tool gets no Handled/charge consumption; the DoAfter UI tells the player it's
        // working and the console announces the breach in local chat.
        if (!_pendingApplications.Remove(ent.Owner))
        {
            TryStartEmagDoAfter(args.UserUid, ent.Owner);
            // Suppress EmaggedComponent — it gets added properly on the completion pass.
            args.Repeatable = true;
            return;
        }

        gridLock.LockDisabled = true;
        gridLock.Locked = false;
        gridLock.EmaggedBy = args.UserUid;
        gridLock.EmaggerName = GetEmaggerName(args.UserUid);
        gridLock.EmaggerJob = GetEmaggerJob(args.UserUid);
        gridLock.EmaggedAt = _timing.CurTime;
        Dirty(gridUid, gridLock);

        args.Handled = true;
    }

    /// <summary>
    /// Attempts to start the 20-second hack DoAfter on the user. Looks up the emag tool from the
    /// user's hands; if none is found (e.g. the emag was just placed away), bails silently.
    /// </summary>
    private void TryStartEmagDoAfter(EntityUid user, EntityUid console)
    {
        if (!TryFindHeldEmag(user, out var emag))
            return;

        var ev = new ShuttleConsoleEmagDoAfterEvent();
        var doAfterArgs = new DoAfterArgs(EntityManager, user, ShuttleConsoleEmagDelay, ev, emag.Owner, target: console, used: emag.Owner)
        {
            BreakOnMove = true,
            BreakOnDamage = true,
            NeedHand = true,
            DuplicateCondition = DuplicateConditions.SameTool | DuplicateConditions.SameTarget,
        };

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
            return;

        _chat.TrySendInGameICMessage(console,
            Loc.GetString("shuttle-console-emag-in-progress"),
            InGameICChatType.Speak,
            ChatTransmitRange.Normal,
            false);
    }

    private bool TryFindHeldEmag(EntityUid user, out Entity<EmagComponent> emag)
    {
        foreach (var hand in _hands.EnumerateHands(user))
        {
            if (hand.HeldEntity is not { } held)
                continue;

            if (TryComp<EmagComponent>(held, out var emagComp) && !emagComp.Demag)
            {
                emag = (held, emagComp);
                return true;
            }
        }

        emag = default;
        return false;
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

    private void OnEmagDoAfter(Entity<EmagComponent> emag, ref ShuttleConsoleEmagDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        if (args.Target is not { } target)
            return;

        // Sanity: the target must still be a valid, non-emagged native shuttle console.
        if (Deleted(target) || !HasComp<ShuttleConsoleComponent>(target) || !HasComp<NativeShuttleConsoleComponent>(target))
            return;

        // Flag this console so the upcoming GotEmaggedEvent applies state instead of restarting
        // the DoAfter.
        _pendingApplications.Add(target);
        var result = _emagSystem.TryEmagEffect((emag.Owner, emag.Comp), args.User, target);
        // Clean up in case TryEmagEffect bailed before reaching OnEmagged (e.g. no charges).
        _pendingApplications.Remove(target);

        args.Handled = result;
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
