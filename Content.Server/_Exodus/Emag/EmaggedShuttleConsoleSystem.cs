using Content.Server.Hands.Systems;
using Content.Server.Shuttles.Components;
using Content.Shared.Access.Components;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.PDA;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Timing;

namespace Content.Server._Exodus.Emag;

/// <summary>
/// Handles emag/demag interactions on shuttle consoles.
/// Emagging a shuttle console permanently disables the grid lock on the parent ship —
/// no card or voucher can lock it again until a demag clears the state.
/// SRD-restored consoles inherit the emag state via <see cref="ShipGridLockComponent"/>,
/// which lives on the grid and survives individual console rebuilds.
/// </summary>
public sealed class EmaggedShuttleConsoleSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly HandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleConsoleComponent, GotEmaggedEvent>(OnEmagged);
        SubscribeLocalEvent<ShuttleConsoleComponent, GotUnEmaggedEvent>(OnUnemagged);
        SubscribeLocalEvent<ShuttleConsoleComponent, MapInitEvent>(OnConsoleMapInit);
    }

    private void OnEmagged(Entity<ShuttleConsoleComponent> ent, ref GotEmaggedEvent args)
    {
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

        gridLock.LockDisabled = false;
        gridLock.EmaggedBy = null;
        gridLock.EmaggerName = null;
        gridLock.EmaggerJob = null;
        gridLock.EmaggedAt = null;
        Dirty(gridUid, gridLock);

        args.Handled = true;
    }

    /// <summary>
    /// When a shuttle console initializes (purchase or SRD rebuild) on a grid whose lock has been
    /// emag-broken, mark this console with <see cref="EmaggedComponent"/> so that future demag
    /// attempts find a valid target.
    /// </summary>
    private void OnConsoleMapInit(Entity<ShuttleConsoleComponent> ent, ref MapInitEvent args)
    {
        if (Transform(ent).GridUid is not { } gridUid)
            return;

        if (!TryComp<ShipGridLockComponent>(gridUid, out var gridLock) || !gridLock.LockDisabled)
            return;

        var emagged = EnsureComp<EmaggedComponent>(ent);
        emagged.EmagType |= EmagType.Interaction;
        Dirty(ent, emagged);
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
