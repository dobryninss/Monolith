using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffect;
using Content.Shared.Temperature;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Shared._Exodus.Genetics;

public sealed class SharedGeneticEffectsSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _movement = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GeneticEffectsComponent, RefreshMovementSpeedModifiersEvent>(OnMovement);
        SubscribeLocalEvent<GeneticEffectsComponent, ComponentStartup>(OnChanged);
        SubscribeLocalEvent<GeneticEffectsComponent, AfterAutoHandleStateEvent>(OnChanged);
        SubscribeLocalEvent<GeneticEffectsComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<GeneticEffectsComponent, BeforeStatusEffectAddedEvent>(OnStatus);
        SubscribeLocalEvent<GeneticEffectsComponent, DamageModifyEvent>(OnDamage);
        SubscribeLocalEvent<GeneticEffectsComponent, GetMeleeDamageEvent>(OnMelee);
        SubscribeLocalEvent<GeneticEffectsComponent, BeforeStaminaDamageEvent>(OnStamina);
        SubscribeLocalEvent<GeneticEffectsComponent, RespirationAttemptEvent>(OnRespiration);
        SubscribeLocalEvent<GeneticEffectsComponent, PressureImmunityEvent>(OnPressure);
        SubscribeLocalEvent<GeneticEffectsComponent, TemperatureDamageAttemptEvent>(OnTemperatureDamage);
        SubscribeLocalEvent<GeneticEffectsComponent, ModifyChangedTemperatureEvent>(OnTemperatureChange);
    }

    private void OnChanged<T>(Entity<GeneticEffectsComponent> ent, ref T args)
    {
        _movement.RefreshMovementSpeedModifiers(ent);
    }

    private void OnShutdown(Entity<GeneticEffectsComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Reverting = true;
        _movement.RefreshMovementSpeedModifiers(ent);
        var ev = new GeneticEffectsShutdownEvent();
        RaiseLocalEvent(ent, ref ev);
    }

    private void OnMovement(Entity<GeneticEffectsComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (!ent.Comp.Reverting)
            args.ModifySpeed(ent.Comp.Modifiers.MovementMultiplier, ent.Comp.Modifiers.MovementMultiplier);
    }

    private void OnStatus(Entity<GeneticEffectsComponent> ent, ref BeforeStatusEffectAddedEvent args)
    {
        if (!ent.Comp.Reverting && ent.Comp.Modifiers.BlockedStatuses.Contains(args.Key))
            args.Cancelled = true;
    }

    private void OnDamage(Entity<GeneticEffectsComponent> ent, ref DamageModifyEvent args)
    {
        if (ent.Comp.Reverting || ent.Comp.Modifiers.DamageMultiplier == 1f)
            return;

        // Healing is not amplified by a vulnerability.
        var damage = new DamageSpecifier(args.Damage);
        foreach (var (type, amount) in args.Damage.DamageDict)
        {
            if (amount > 0)
                damage.DamageDict[type] = amount * ent.Comp.Modifiers.DamageMultiplier;
        }
        args.Damage = damage;
    }

    private void OnMelee(Entity<GeneticEffectsComponent> ent, ref GetMeleeDamageEvent args)
    {
        if (!ent.Comp.Reverting && args.Weapon == ent.Owner)
            args.Damage *= ent.Comp.Modifiers.MeleeMultiplier;
    }

    private void OnStamina(Entity<GeneticEffectsComponent> ent, ref BeforeStaminaDamageEvent args)
    {
        if (!ent.Comp.Reverting && args.Value > 0)
            args.Value *= ent.Comp.Modifiers.StaminaMultiplier;
    }

    private void OnRespiration(Entity<GeneticEffectsComponent> ent, ref RespirationAttemptEvent args)
    {
        args.Cancelled |= !ent.Comp.Reverting && ent.Comp.Modifiers.NoBreathing;
    }

    private void OnPressure(Entity<GeneticEffectsComponent> ent, ref PressureImmunityEvent args)
    {
        args.Immune |= !ent.Comp.Reverting && (args.HighPressure
            ? ent.Comp.Modifiers.HighPressureImmunity
            : ent.Comp.Modifiers.LowPressureImmunity);
    }

    private void OnTemperatureDamage(Entity<GeneticEffectsComponent> ent, ref TemperatureDamageAttemptEvent args)
    {
        args.Cancelled |= !ent.Comp.Reverting && (args.Hot
            ? ent.Comp.Modifiers.HeatImmunity
            : ent.Comp.Modifiers.ColdImmunity);
    }

    private void OnTemperatureChange(Entity<GeneticEffectsComponent> ent, ref ModifyChangedTemperatureEvent args)
    {
        if (!ent.Comp.Reverting && (args.TemperatureDelta < 0 && ent.Comp.Modifiers.ColdImmunity ||
                                   args.TemperatureDelta > 0 && ent.Comp.Modifiers.HeatImmunity))
            args.TemperatureDelta = 0;
    }
}
