using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Teleportation;

public sealed partial class DirectionalTeleportActionEvent : InstantActionEvent;

[Serializable, NetSerializable]
public sealed partial class DirectionalTeleportDoAfterEvent : SimpleDoAfterEvent;
