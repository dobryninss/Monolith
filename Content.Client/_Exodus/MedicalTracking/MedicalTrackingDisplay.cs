using Content.Shared.Mobs;

namespace Content.Client._Exodus.MedicalTracking;

public static class MedicalTrackingDisplay
{
    public static string Status(MobState state) => Loc.GetString(state switch
    {
        MobState.Alive => "medical-tracking-alive",
        MobState.Critical => "medical-tracking-critical",
        _ => "medical-tracking-dead",
    });

    public static Color StatusColor(MobState state) => state switch
    {
        MobState.Alive => Color.LimeGreen,
        MobState.Critical => Color.Orange,
        _ => Color.Red,
    };

    public static int Priority(MobState state) => state switch
    {
        MobState.Critical => 0,
        MobState.Dead => 1,
        _ => 2,
    };
}
