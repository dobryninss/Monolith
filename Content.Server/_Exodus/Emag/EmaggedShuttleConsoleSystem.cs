using Content.Server.Chat.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Server.Shuttles.Components;
using Content.Shared._Exodus.Emag;
using Content.Shared.Access.Components;
using Content.Shared.Chat;
using Content.Shared.DoAfter;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Paper;
using Content.Shared.PDA;
using Content.Shared.Radio;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Exodus.Emag;

/// <summary>
/// Handles emag/demag interactions on shuttle consoles.
/// Emagging any shuttle console kicks off a 20-second hack DoAfter; on completion the parent grid
/// has its lock permanently disabled (until demag). Demagging spawns a paper with the emagger's
/// name into the demagger's hands.
/// </summary>
public sealed class EmaggedShuttleConsoleSystem : EntitySystem
{
    private static readonly EntProtoId PaperProto = "Paper";

    /// <summary>
    /// Hack delay applied to every shuttle console emag attempt.
    /// </summary>
    private static readonly TimeSpan ShuttleConsoleEmagDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Radio channel the hack announcement is broadcast on (the local <c>:л</c> channel).
    /// </summary>
    private static readonly ProtoId<RadioChannelPrototype> HackAnnounceChannel = "Traffic";

    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly PaperSystem _paper = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly EmagSystem _emagSystem = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly RadioSystem _radio = default!;

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
        SubscribeLocalEvent<ShuttleConsoleComponent, MapInitEvent>(OnConsoleMapInit);
        SubscribeLocalEvent<EmagComponent, ShuttleConsoleEmagDoAfterEvent>(OnEmagDoAfter);
    }

    /// <summary>
    /// Newly-spawned shuttle consoles (purchase, SRD restore, mapper placement) inherit the
    /// emagged screen sprite if their grid is already emag-broken.
    /// </summary>
    private void OnConsoleMapInit(Entity<ShuttleConsoleComponent> ent, ref MapInitEvent args)
    {
        if (Transform(ent).GridUid is not { } gridUid)
            return;

        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock) || !gridLock.LockDisabled)
            return;

        _appearance.SetData(ent.Owner, ShuttleConsoleEmagVisuals.Emagged, true);
    }

    /// <summary>
    /// Mirrors the grid-level emag state onto every shuttle console on the grid via Appearance.
    /// </summary>
    private void SetGridConsoleVisuals(EntityUid gridUid, bool emagged)
    {
        var query = EntityQueryEnumerator<ShuttleConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var consoleUid, out _, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            _appearance.SetData(consoleUid, ShuttleConsoleEmagVisuals.Emagged, emagged);
        }
    }

    private void OnEmagged(Entity<ShuttleConsoleComponent> ent, ref GotEmaggedEvent args)
    {
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
            TryStartEmagDoAfter(args.UserUid, ent.Owner, gridUid);
            // Suppress EmaggedComponent on the no-Handled pass.
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

        SetGridConsoleVisuals(gridUid, true);

        args.Handled = true;
    }

    private void TryStartEmagDoAfter(EntityUid user, EntityUid console, EntityUid shipGrid)
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

        var message = Loc.GetString("shuttle-console-emag-in-progress", ("vessel", Name(shipGrid)));

        _chat.TrySendInGameICMessage(console,
            message,
            InGameICChatType.Speak,
            ChatTransmitRange.Normal,
            false);

        if (_proto.TryIndex(HackAnnounceChannel, out var channel))
            _radio.SendRadioMessage(console, message, channel, console);
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

        SetGridConsoleVisuals(gridUid, false);

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

        if (Deleted(target) || !HasComp<ShuttleConsoleComponent>(target))
            return;

        // Flag this console so the upcoming GotEmaggedEvent applies state instead of restarting
        // the DoAfter.
        _pendingApplications.Add(target);
        var result = _emagSystem.TryEmagEffect((emag.Owner, emag.Comp), args.User, target);
        // Clean up in case TryEmagEffect bailed before reaching OnEmagged (e.g. no charges).
        _pendingApplications.Remove(target);

        args.Handled = result;
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
