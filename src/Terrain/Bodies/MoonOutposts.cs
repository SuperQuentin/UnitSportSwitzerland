using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>What an industrial outpost away from its colony is (#1942, docs/plans/moon-surface.md section 4).</summary>
public enum OutpostKind : byte
{
    /// <summary>The south-polar ice mine on Shackleton's permanently dark floor: a headframe, flood lights, bins, a hut.</summary>
    IceMine,
    /// <summary>The far-side radio telescope: a field of dishes on a level crater floor, radio-quiet behind the Moon.</summary>
    RadioTelescope,
    /// <summary>The mass driver: a 2 km rail on pylons along a levelled strip, a bucket flung east along it.</summary>
    MassDriver,
}

/// <summary>
/// An industrial outpost (#1942): where it is (LOLA's frame), what it is, which colony runs it (its colours), its heading
/// (degrees from north), the ground it needs (a level disc of <see cref="RadiusM"/>, or a level strip <see cref="LengthM"/>
/// long along the heading and twice <see cref="RadiusM"/> wide), and the seed of its layout.
/// </summary>
public sealed record MoonOutpost(string Id, string Name, OutpostKind Kind, string Colony, double Lat, double Lon,
    double Heading, double RadiusM, double LengthM, int Seed)
{
    public EcefPos Normal => CubeSphere.NormalAt(Lat, Lon);

    /// <summary>A stand-in site at the outpost's point (for <see cref="MoonSites.Offset"/>).</summary>
    public MoonSite Probe => new(Id, Name, MoonSiteKind.Industry, Lat, Lon, []);

    /// <summary>The strip's far end (a unit normal), or null for a disc.</summary>
    public EcefPos? End => LengthM > 0
        ? MoonSites.Offset(Probe, Math.Sin(Heading * Math.PI / 180) * LengthM, Math.Cos(Heading * Math.PI / 180) * LengthM)
        : null;

    /// <summary>The ground it stands on: a level disc, or a level strip (linear between the data's heights at its ends).</summary>
    public SurfaceStamp Stamp => new(Normal, End, RadiusM, LengthM > 0 ? 40 : 80, StampMode.Level);

    /// <summary>How far the farthest piece is from the outpost's point, metres.</summary>
    public double Extent => Math.Max(RadiusM, LengthM);
}

/// <summary>
/// The industrial outposts (#1942): few, each by the colony that runs it. Positions from <c>--moonsurvey</c> (the
/// flattest disc near the place the plan names), committed.
/// </summary>
public static class MoonOutposts
{
    public static readonly MoonOutpost[] All =
    [
        // Shackleton's floor, 20 km from Artemis Base and 4.2 km under it, never in the sun: --moonsurvey
        // -89.67,129.78,2.5,120 (0.38 m RMS round a plane tilted 0.58°, -2,757 m)
        new("icemine", "Shackleton ice mine", OutpostKind.IceMine, "artemis", -89.6370, 128.6348, 0, 120, 0, 4001),
        // a level patch of Von Karman's floor 4.7 km south-west of ILRS: --moonsurvey -44.92,176.66,2.5,250
        // (0.04 m RMS, 0.01°, -5,896 m)
        new("radiotelescope", "Far-side radio telescope", OutpostKind.RadioTelescope, "ilrs", -44.9398, 176.5482, 0, 250, 0, 4002),
        // 910 m east of Moon Village, flinging east along the equator (the Moon's turn helps a little): a 2 km strip
        new("massdriver", "Moon Village mass driver", OutpostKind.MassDriver, "moonvillage", 0.7198, 23.5570, 90, 14, 2000, 4003),
    ];

    public static MoonOutpost? Find(string id)
    {
        foreach (var o in All)
            if (o.Id == id) return o;
        return null;
    }

    /// <summary>
    /// An outpost's pieces in its frame (x east, z south, metres from its point): pure from its record, the same on every
    /// peer. The ice mine: the headframe over the shaft, a hut, ore bins, a parked digger, four flood lights round it.
    /// The radio telescope: 19 dishes in hexagonal rings 42 m apart, a control hut. The mass driver: the rail, a loading
    /// hut and bins at its start.
    /// </summary>
    public static List<ColonyIndustry> Plan(MoonOutpost o)
    {
        var list = new List<ColonyIndustry>();
        double h = o.Heading * Math.PI / 180;
        var (ax, az) = ColonyPlan.Along(h);
        var (sx, sz) = ColonyPlan.Along(h + Math.PI / 2);
        switch (o.Kind)
        {
            case OutpostKind.IceMine:
                list.Add(new ColonyIndustry(IndustryKind.Headframe, 0, 0, h, 22));
                list.Add(new ColonyIndustry(IndustryKind.Hut, sx * 18 + ax * 4, sz * 18 + az * 4, h, 10));
                list.Add(new ColonyIndustry(IndustryKind.OreBins, -sx * 16, -sz * 16, h + Math.PI / 2, 4));
                list.Add(new ColonyIndustry(IndustryKind.Digger, ax * 55, az * 55, h, 0));
                list.Add(new ColonyIndustry(IndustryKind.SpoilHeap, -ax * 45 + sx * 30, -az * 45 + sz * 30, h, 9));
                for (int i = 0; i < 4; i++)
                {
                    var (lx, lz) = ColonyPlan.Along(h + Math.PI / 4 + i * Math.PI / 2);
                    list.Add(new ColonyIndustry(IndustryKind.FloodLight, lx * 34, lz * 34, h + Math.PI / 4 + i * Math.PI / 2 + Math.PI, 12));
                }
                break;
            case OutpostKind.RadioTelescope:
                list.Add(new ColonyIndustry(IndustryKind.Dish, 0, 0, h, 16));
                for (int ring = 1; ring <= 2; ring++)
                    for (int i = 0; i < 6 * ring; i++)
                    {
                        // a hexagonal ring: the corners and the points between them
                        int side = i / ring, step = i % ring;
                        var (c0x, c0z) = ColonyPlan.Along(h + side * Math.PI / 3);
                        var (c1x, c1z) = ColonyPlan.Along(h + (side + 1) * Math.PI / 3);
                        double t = step / (double)ring;
                        double x = (c0x + (c1x - c0x) * t) * 42 * ring, z = (c0z + (c1z - c0z) * t) * 42 * ring;
                        list.Add(new ColonyIndustry(IndustryKind.Dish, x, z, h, 12));
                    }
                list.Add(new ColonyIndustry(IndustryKind.Hut, -sx * 150, -sz * 150, h, 12));
                break;
            case OutpostKind.MassDriver:
                list.Add(new ColonyIndustry(IndustryKind.DriverTrack, 0, 0, h, o.LengthM));
                list.Add(new ColonyIndustry(IndustryKind.Hut, sx * 14 - ax * 10, sz * 14 - az * 10, h, 10));
                list.Add(new ColonyIndustry(IndustryKind.OreBins, -sx * 9 - ax * 6, -sz * 9 - az * 6, h, 4));
                break;
        }
        return list;
    }
}
