using Robust.Shared.GameStates;

namespace Content.Shared._Exodus.Genetics;

/// <summary>Only expressed effects are replicated; the genome and round cipher stay on the server.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class GeneticEffectsComponent : Component
{
    [DataField, AutoNetworkedField] public GeneticModifiers Modifiers = new();
    public bool Reverting;
}
