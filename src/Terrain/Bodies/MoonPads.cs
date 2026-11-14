using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>A landing pad or a runway's centre line on the Moon (#1943): what the shuttle needs to come down on it.</summary>
/// <param name="Id">Stable: <c>artemis-pad2</c>, <c>runway</c>.</param>
/// <param name="Colony">The colony it belongs to.</param>
/// <param name="Centre">The pad's centre, or the runway's first end (a unit normal, body-fixed).</param>
/// <param name="End">The runway's other end, or null for a pad.</param>
/// <param name="Heading">Degrees from north: a pad's road out from the colony, the runway's direction from its first end.</param>
/// <param name="RadiusM">A pad's radius, or the runway's half width, metres.</param>
/// <param name="Occupied">A lander stands on it: the shuttle should take another.</param>
public readonly record struct MoonPad(string Id, string Colony, EcefPos Centre, EcefPos? End, double Heading, double RadiusM, bool Occupied)
{
    public bool IsRunway => End != null;
}

/// <summary>
/// Every place on the Moon a craft may land (#1943, docs/plans/moon-surface.md section 7): each staffed colony's pads (from
/// its plan, in its frame turned to selenographic normals) and Artemis Base's runway (an outpost). The ground is level
/// there on every peer (the colony's plateau, the runway's strip stamp), so a pad's height is
/// <c>BodyHeights.At(Centre)</c>. Pure: the planets session's shuttle landing logic (C3a) reads it; nothing here flies.
/// </summary>
public static class MoonPads
{
    private static List<MoonPad>? _all;

    public static IReadOnlyList<MoonPad> All => _all ??= Make();

    private static List<MoonPad> Make()
    {
        var list = new List<MoonPad>();
        foreach (var c in MoonColonies.All)
        {
            var plan = ColonyPlan.For(c);
            var probe = new MoonSite(c.Id, c.Name, MoonSiteKind.Colony, c.Lat, c.Lon, []);
            foreach (var p in plan.Pads)
            {
                bool occupied = false;
                foreach (var i in plan.Industry)
                    if (i.Kind == IndustryKind.Lander && Math.Abs(i.X - p.X) < 1 && Math.Abs(i.Z - p.Z) < 1) occupied = true;
                // the frame's z is south: north = -z
                list.Add(new MoonPad($"{c.Id}-pad{p.Number}", c.Id, MoonSites.Offset(probe, p.X, -p.Z), null, c.Heading, p.Radius, occupied));
            }
        }
        foreach (var o in MoonOutposts.All)
            if (o.Kind == OutpostKind.Runway)
                list.Add(new MoonPad(o.Id, o.Colony, o.Normal, o.End, o.Heading, o.RadiusM, false));
        return list;
    }

    public static MoonPad? Find(string id)
    {
        foreach (var p in All)
            if (p.Id == id) return p;
        return null;
    }

    /// <summary>The nearest free pad or runway to a unit normal, and how far it is (metres on the sphere), or null.</summary>
    public static (MoonPad Pad, double DistanceM)? NearestFree(EcefPos n)
    {
        (MoonPad, double)? best = null;
        foreach (var p in All)
        {
            if (p.Occupied) continue;
            double d = Math.Acos(Math.Clamp(n.X * p.Centre.X + n.Y * p.Centre.Y + n.Z * p.Centre.Z, -1, 1)) * Ellipsoid.Moon.A;
            if (best == null || d < best.Value.Item2) best = (p, d);
        }
        return best;
    }
}
