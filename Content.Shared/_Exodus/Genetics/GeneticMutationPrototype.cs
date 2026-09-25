using Content.Shared.Damage;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Genetics;

/// <summary>A structural gene with a separate activation minimum for each hexadecimal digit.</summary>
[Prototype]
public sealed partial class GeneticMutationPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public LocId Description;
    [DataField] public int Instability = 10;
    /// <summary>Three hexadecimal minima, packed as a number. Default: D/A/C.</summary>
    [DataField] public int ActivationThreshold = 0xDAC;
    /// <summary>Private sensation sent to the carrier when this mutation becomes active.</summary>
    [DataField(required: true)] public LocId ActivationMessage;
    [DataField] public GeneticModifiers Modifiers = new();
    [DataField] public List<EntProtoId> Actions = new();
    [DataField] public DamageSpecifier PeriodicDamage = new();
}

/// <summary>Independent, source-owned contributions. These never replace organs or disease components.</summary>
[DataDefinition, Serializable, NetSerializable]
public sealed partial class GeneticModifiers
{
    [DataField] public bool NoBreathing;
    [DataField] public bool LowPressureImmunity;
    [DataField] public bool HighPressureImmunity;
    [DataField] public bool ColdImmunity;
    [DataField] public bool HeatImmunity;
    [DataField] public float MovementMultiplier = 1f;
    [DataField] public float MeleeMultiplier = 1f;
    [DataField] public float StaminaMultiplier = 1f;
    [DataField] public float DamageMultiplier = 1f;
    [DataField] public HashSet<string> BlockedStatuses = new();
    [DataField] public GeneticAbility Abilities;
}

[Flags, Serializable, NetSerializable]
public enum GeneticAbility : ushort
{
    None = 0,
    Telekinesis = 1,
    RemoteViewing = 2,
    Cloak = 4,
    Devour = 8,
    Mimic = 16,
    PsyResist = 32,
    ForcePry = 64,
}
