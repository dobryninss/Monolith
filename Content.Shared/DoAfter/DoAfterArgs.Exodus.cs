namespace Content.Shared.DoAfter;

// Exodus - capability-scoped range for delayed interactions.
public sealed partial class DoAfterArgs
{
    /// <summary>
    /// Optional provider key for an extended DistanceThreshold. The provider must approve
    /// ValidateDoAfterRangeEvent on the user for the entire duration of the interaction.
    /// </summary>
    [DataField]
    public string? RangeProvider;
}
