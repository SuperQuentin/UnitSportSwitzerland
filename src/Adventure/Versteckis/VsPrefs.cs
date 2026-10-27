namespace UnitSport.Adventure.Versteckis;

/// <summary>Versteckis's client settings (#1268), kept in <c>GameSettings</c>: the seeker's counting tick.</summary>
public static class VsPrefs
{
    public static bool Counting => Core.GameSettings.Current.VersteckisCounting;
}
