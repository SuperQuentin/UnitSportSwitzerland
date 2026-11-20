using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>
/// <c>--placedsavecheck [N]</c> (#1995, headless, on a copy of a save in a scratch user folder): once
/// <see cref="PlacedObjects"/> has loaded <c>user://placed/offline.json</c>, every object is compared with the file: its
/// place (the saved <c>at</c>, its body) and its turn (the saved <c>turn</c>, the local east-up-south) must come back
/// exactly. The log also gives how far the record before #1995 (LV95 E/N/Altitude, its turn in the grid's axes) was
/// from the file: information only (far outside Switzerland the grid's axes drift). The file is
/// never written. <c>N</c>: the object count the file holds.
/// </summary>
public static class PlacedSaveCheck
{
    public static bool Requested => CmdArgs.Has("--placedsavecheck");

    /// <summary>The report, from <see cref="PlacedObjects"/>' load of <paramref name="path"/>; quits the process after it.</summary>
    public static void Report(PlacedObjects placed, string path, int loaded)
    {
        double worstM = 0, worstDeg = 0, oldM = 0, oldDeg = 0;
        string worstWhat = "none", oldWhat = "none";
        int matched = 0, inFile = 0;
        var file = JsonNode.Parse(System.IO.File.ReadAllText(path))?["Objects"]?.AsArray();
        foreach (var e in file ?? new JsonArray())
        {
            inFile++;
            if (e?["Id"]?.GetValue<long>() is not { } id || !placed.All.TryGetValue(id, out var o)) continue;
            matched++;
            var at = e["at"]!;
            var body = at["b"] is { } b ? (BodyId)b.GetValue<int>() : BodyId.Earth;
            var saved = GlobalPos.On(body, new UnitSport.Terrain.Format.EcefPos(at["x"]!.GetValue<double>(), at["y"]!.GetValue<double>(), at["z"]!.GetValue<double>()));
            var t = e["turn"]!.AsArray();
            var savedTurn = new Quaternion(t[0]!.GetValue<float>(), t[1]!.GetValue<float>(), t[2]!.GetValue<float>(), t[3]!.GetValue<float>());
            double m = saved.Body == o.At.Body ? saved.DistanceTo(o.At) : double.PositiveInfinity;
            double deg = Degrees(o.EnuTurn, savedTurn);
            if (m > worstM || deg > worstDeg) worstWhat = $"{o.Kind} #{o.Id}";
            // (the same object either way round: the worst of both kept)
            worstM = System.Math.Max(worstM, m);
            worstDeg = System.Math.Max(worstDeg, deg);
            // the record before #1995: its place through LV95, its grid turn read at that place
            if (o.At.Body != BodyId.Earth) continue;
            var (le, ln, _) = o.At.Lv95;
            var old = GlobalPos.FromLv95(le, ln, o.At.Alt);
            // (it read the saved turn into the grid's axes at the file's place, and turned it back out at the round-tripped one)
            var oldRot = Lv95Grid.Axes(saved).FromEnu(savedTurn);
            double om = old.DistanceTo(saved), od = Degrees(Lv95Grid.Axes(old).ToEnu(oldRot), savedTurn);
            if (om > oldM || od > oldDeg) oldWhat = $"{o.Kind} #{o.Id}";
            oldM = System.Math.Max(oldM, om);
            oldDeg = System.Math.Max(oldDeg, od);
        }
        int? want = CmdArgs.Int("--placedsavecheck");
        bool ok = matched == inFile && matched == loaded && worstM < 0.001 && worstDeg < 0.001 && (want == null || want == loaded);
        GD.Print(System.FormattableString.Invariant(
            $"[placedsavecheck] before #1995 the LV95 record was up to {oldM:F4} m and {oldDeg:F4}° off the file ({oldWhat})"));
        GD.Print(System.FormattableString.Invariant(
            $"[placedsavecheck] RESULT: {(ok ? "ok" : "FAILED")} — {loaded} loaded, {matched} of {inFile} in the file{(want is { } w ? $" (expected {w})" : "")}, worst {worstM:F6} m and {worstDeg:F6}° off the file ({worstWhat})"));
        placed.GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// The angle between two turns, degrees, in doubles from their difference (2·|q1 ∓ q2| for small angles): Godot's
    /// <c>AngleTo</c> takes an arccos near 1 in floats, ~0.03° of noise between two equal turns.
    /// </summary>
    private static double Degrees(Quaternion a, Quaternion b)
    {
        double la = System.Math.Sqrt((double)a.X * a.X + (double)a.Y * a.Y + (double)a.Z * a.Z + (double)a.W * a.W);
        double lb = System.Math.Sqrt((double)b.X * b.X + (double)b.Y * b.Y + (double)b.Z * b.Z + (double)b.W * b.W);
        double Diff(double s) =>
            System.Math.Sqrt(Sq(a.X / la - s * b.X / lb) + Sq(a.Y / la - s * b.Y / lb) + Sq(a.Z / la - s * b.Z / lb) + Sq(a.W / la - s * b.W / lb));
        double d = System.Math.Min(Diff(1), Diff(-1));
        return 4 * System.Math.Asin(System.Math.Min(1, d / 2)) * 180 / System.Math.PI;
    }

    private static double Sq(double v) => v * v;
}
