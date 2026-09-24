using System.Numerics;
using Content.Shared._Exodus.MedicalTracking; // Exodus medical tablet
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Map; // Exodus medical tablet

namespace Content.Client.Shuttles.UI;

public sealed partial class MapScreen
{
    // Exodus read-only bluespace map
    /// <summary>
    /// Configures the existing shuttle map as a read-only sector map.
    /// </summary>
    public void SetupReadOnlyMap(EntityUid entity)
    {
        SetConsole(entity);
        SetShuttle(entity);
        UpdateState(new ShuttleMapInterfaceState(FTLState.Available, default, [], []));
        MapRadar.FtlMode = false;
        MapRadar.ShowFTLRangeOnly = false;
        Startup();
        PingMap();
        MapRadar.MaxSize = new Vector2(float.PositiveInfinity);

        if (RightDisplayMap.Parent?.Parent is { } rightPanel)
            rightPanel.Visible = false;
    }

    // Exodus-begin medical tablet
    public event Action<NetEntity> MedicalContactSelected
    {
        add => MapRadar.MedicalContactSelected += value;
        remove => MapRadar.MedicalContactSelected -= value;
    }

    public void SetMedicalContacts(List<MedicalTrackingContact> contacts)
    {
        MapRadar.SetMedicalContacts(contacts);
    }

    public void FocusMedicalContact(MapCoordinates coordinates)
    {
        if (_mapManager.MapExists(coordinates.MapId))
            MapRadar.FocusMedicalPosition(coordinates, 64f);
    }

    public void SelectMedicalContact(NetEntity? body)
    {
        MapRadar.SelectMedicalContact(body);
    }

    public void FocusMedicalOperator()
    {
        if (_console is { } console && _entManager.EntityExists(console))
            FocusMedicalContact(_xformSystem.GetMapCoordinates(console));
    }

    public void ShowMedicalContacts()
    {
        MapRadar.ShowMedicalContacts();
    }
    // Exodus-end
}
