using Content.Shared.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Access.Components;
using Content.Shared.UserInterface;

namespace Content.Client.Shuttles.Systems;

/// <summary>
/// Client implementation of the shuttle console lock system.
/// </summary>
public sealed class ShuttleConsoleLockSystem : SharedShuttleConsoleLockSystem
{
    /// <summary>
    /// Client implementation of TryUnlock. The actual unlock happens server-side.
    /// </summary>
    public override bool TryUnlock(EntityUid console, EntityUid idCard, ShuttleConsoleLockComponent? lockComp = null, IdCardComponent? idComp = null, EntityUid? user = null)
    {
        // Prediction only
        return false;
    }

    /// <summary>
    /// Client extension of OnUIOpenAttempt. Prevents UI flashing open on client.
    /// </summary>
    protected override void OnUIOpenAttempt(EntityUid uid,
        ShuttleConsoleLockComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        base.OnUIOpenAttempt(uid, component, args);

        // Exodus emag-rework: only show the lock popup when the console is actually locked.
        // Other systems (e.g. no-power) also cancel the open attempt, and we don't want to claim
        // it's a lock issue then — especially for emagged consoles, which aren't locked at all.
        if (Timing.IsFirstTimePredicted && args.Cancelled && GetEffectiveLockState(uid, component))
            Popup.PopupEntity(Loc.GetString("shuttle-console-locked"), uid, args.User);
    }
}
