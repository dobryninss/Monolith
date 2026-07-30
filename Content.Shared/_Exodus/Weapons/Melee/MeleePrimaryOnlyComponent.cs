using Robust.Shared.GameStates;

namespace Content.Shared._Exodus.Weapons.Melee;

/// <summary>
/// Restricts a melee weapon to the primary attack input when the owner also has a secondary-input gun.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class MeleePrimaryOnlyComponent : Component;
