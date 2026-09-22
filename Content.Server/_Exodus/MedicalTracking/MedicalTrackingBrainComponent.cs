namespace Content.Server._Exodus.MedicalTracking;

[RegisterComponent]
public sealed partial class MedicalTrackingBrainComponent : Component
{
    /// <summary>Implant that registered this brain; used to revoke tracking on deliberate extraction.</summary>
    [DataField]
    public EntityUid? Implant;

    /// <summary>Remains true after body destruction, until the brain is destroyed or coverage is revoked.</summary>
    [DataField]
    public bool Registered;

    /// <summary>Identity presented by the client at registration, retained after extraction of the brain.</summary>
    [DataField]
    public string ClientName = string.Empty;
}
