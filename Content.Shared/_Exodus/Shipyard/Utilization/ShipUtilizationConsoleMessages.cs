using Robust.Shared.Serialization;

namespace Content.Shared._Exodus.Shipyard.Utilization;

[Serializable, NetSerializable]
public sealed class ShipUtilizationStartMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity Ship;

    public ShipUtilizationStartMessage(NetEntity ship)
    {
        Ship = ship;
    }
}

[Serializable, NetSerializable]
public sealed class ShipUtilizationCancelMessage : BoundUserInterfaceMessage
{
}
