namespace UnitSport.Interiors;

/// <summary>
/// The combination of a locked flat's front door (#557): three numbers on the lock dial, a pure
/// function of the building and the door, so the server and the client cracking it agree without
/// sending it. As with lockers, a modded client could derive it; what the server guarantees is one
/// shared unlocked state. Plain C#, unit-tested.
/// </summary>
public static class InnerDoorLock
{
    public const int Tumblers = 3;

    public static int[] Combination(string building, int door)
    {
        var combo = new int[Tumblers];
        for (int i = 0; i < Tumblers; i++)
            combo[i] = (int)(Core.Fnv.Unit($"{building}|flatlock|{door}|{i}") * 100) % 100;
        return combo;
    }

    /// <summary>Whether numbers a dial settled on are the combination, each within a number either side.</summary>
    public static bool Opens(string building, int door, int[] tried)
    {
        var combo = Combination(building, door);
        if (tried.Length != combo.Length) return false;
        for (int i = 0; i < combo.Length; i++)
        {
            int d = Math.Abs(tried[i] - combo[i]);
            if (Math.Min(d, 100 - d) > 1) return false;
        }
        return true;
    }
}
