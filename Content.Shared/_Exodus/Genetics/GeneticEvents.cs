using Content.Shared.Actions;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Genetics;

// These physiological hooks allow independent providers to coexist without removing each other's components.
[ByRefEvent]
public record struct RespirationAttemptEvent(bool Cancelled = false);

[ByRefEvent]
public record struct PressureImmunityEvent(bool HighPressure, bool Immune = false);

[ByRefEvent]
public record struct TemperatureDamageAttemptEvent(bool Hot, bool Cancelled = false);

[ByRefEvent]
public readonly record struct GenomeChangedEvent;

/// <summary>Raised after genetic effects stop contributing, so server-owned abilities can release their resources.</summary>
[ByRefEvent]
public readonly record struct GeneticEffectsShutdownEvent;

public sealed partial class GeneticTelekinesisEvent : EntityTargetActionEvent;
public sealed partial class GeneticRemoteViewingEvent : InstantActionEvent;
public sealed partial class GeneticCloakEvent : InstantActionEvent;
public sealed partial class GeneticDevourEvent : EntityTargetActionEvent;
public sealed partial class GeneticEatTileEvent : WorldTargetActionEvent;
public sealed partial class GeneticMimicEvent : EntityTargetActionEvent;
public sealed partial class GeneticRestoreAppearanceEvent : InstantActionEvent;
public sealed partial class GeneticPryEvent : EntityTargetActionEvent;

[Serializable, NetSerializable]
public sealed partial class GeneticInjectionDoAfterEvent : SimpleDoAfterEvent
{
    [DataField] public int Revision;
}

[Serializable, NetSerializable]
public sealed partial class GeneticDevourDoAfterEvent : SimpleDoAfterEvent;

[Serializable, NetSerializable]
public sealed partial class GeneticEatTileDoAfterEvent : SimpleDoAfterEvent
{
    [DataField] public Vector2i Indices;
    [DataField] public int TileType;
}

[Serializable, NetSerializable]
public sealed partial class GeneticPryDoAfterEvent : SimpleDoAfterEvent;
