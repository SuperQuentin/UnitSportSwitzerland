using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// One place in Switzerland we recognise by name rather than by shape: a brand whose building is
/// the same building everywhere, standing somewhere the data cannot tell us about.
/// <see cref="E"/>/<see cref="N"/> are LV95, derived from <see cref="Lat"/>/<see cref="Lon"/> once.
/// </summary>
public readonly record struct Landmark(string Name, BuildingType Type, double Lat, double Lon, double E, double N);

/// <summary>
/// Buildings recognised by <b>where they are</b> rather than by what they look like.
///
/// <para>
/// swissBUILDINGS3D draws the IKEA at Dietlikon as what it is — one huge flat-roofed solid — but it
/// has no idea it is an IKEA, exactly as it has no idea which building is a bank. Geometry alone
/// cannot tell that box from any other distribution shed, and no hash will ever put one at
/// Dietlikon and nowhere else. So the nine stores are named here, by position.
/// </para>
///
/// <para>
/// This stays a <b>pure function of the tile</b>, which is what matters for the network:
/// <see cref="BuildingTile"/> carries its own <see cref="TileId"/>, and a tile id gives LV95, so the
/// server and every client answer "is this the IKEA?" from the same bytes and agree without one
/// being sent — the property <see cref="BuildingTypes"/> relies on for churches and
/// <c>BuildingFootprint.IsBank</c> for banks.
/// </para>
///
/// <para>
/// <b>Why the point must land inside the footprint.</b> The first cut of this took "the largest big
/// solid within 400 m", and checked against real tiles it was wrong almost everywhere: at
/// Spreitenbach it took the 73 m Tivoli tower, at Vernier a 249 000 m² complex, at Aubonne a block
/// of flats. Worse, a radius that reaches into neighbouring tiles lets up to four tiles each claim
/// the same store, and <see cref="BuildingTypes.Detect"/> sees one tile at a time so it cannot
/// compare across them. Containment fixes both: exactly one building in exactly one tile can hold a
/// point, so the answer is unique however the tiles are cut, and no mall or tower can be picked up
/// by being merely nearby. <see cref="Tolerance"/> only forgives a point that fell just off a wall.
/// </para>
///
/// <para>
/// Each row's coordinate was checked against the real solid it has to find, and the shape band
/// below was measured from those nine buildings. <c>--ikeacheck</c> prints every row and what it
/// matched, so a row the data disagrees with is one line to correct.
/// Rules and how to re-check them: <c>docs/notes/terrain/ikea.md</c>.
/// </para>
/// </summary>
public static class Landmarks
{
    /// <summary>
    /// How far outside a footprint the noted point may still fall. Small on purpose: this forgives
    /// a point taken from the car park edge or a door, not a point in the wrong retail park.
    /// </summary>
    public const float Tolerance = 45f;

    // The store signature, measured off the nine real solids: a big LOW box. The height band is
    // what keeps a shopping centre's tower or an 8-storey block from ever being a store, and the
    // area floor keeps out the garden centre, the trolley shelter and the substation.
    public const float MinArea = 6000f, MaxArea = 45000f;
    public const float MinHeight = 9f, MaxHeight = 30f;

    /// <summary>
    /// The nine IKEA Einrichtungshäuser. The plan-and-order points (Zürich Pelikanstrasse, Bern
    /// Gerechtigkeitsgasse, Winterthur Stadthausstrasse) are <b>not</b> here: they are counters in
    /// an ordinary town building, not blue boxes, and the shape band would reject them anyway.
    /// </summary>
    public static readonly Landmark[] Ikea =
    {
        Ikea1("Dietlikon", 47.413790, 8.623156),
        Ikea1("Spreitenbach", 47.421719, 8.375199),
        Ikea1("Lyssach", 47.074107, 7.569477),
        Ikea1("Rothenburg", 47.097496, 8.244069),
        Ikea1("Aubonne", 46.476563, 6.396967),
        Ikea1("Vernier", 46.219444, 6.094660),
        Ikea1("Pratteln", 47.527800, 7.689300),
        Ikea1("St. Gallen", 47.408400, 9.306900),
        Ikea1("Grancia", 45.972991, 8.926465),
    };

    /// <summary>Every landmark, whatever its type. One brand today; a list when there is a second.</summary>
    public static readonly Landmark[] All = Ikea;

    private static Landmark Ikea1(string name, double lat, double lon)
    {
        var (e, n) = SwissProjection.ToLv95(lat, lon);
        return new Landmark(name, BuildingType.Ikea, lat, lon, e, n);
    }

    /// <summary>
    /// The landmarks whose point could fall on a building of this tile: inside it, or within
    /// <see cref="Tolerance"/> of its edge, since a building belongs to the tile holding its centre
    /// and a 200 m store beside a tile edge reaches well over it.
    /// </summary>
    public static IEnumerable<Landmark> Near(TileId id)
    {
        foreach (var l in All)
            if (l.E >= id.MinE - Tolerance && l.E <= id.MinE + ChunkFormat.TileSizeM + Tolerance
                && l.N >= id.MinN - Tolerance && l.N <= id.MaxN + Tolerance)
                yield return l;
    }

    /// <summary>
    /// The point of <paramref name="l"/> in <paramref name="id"/>'s frame: X east, Z south from the
    /// NW corner, like every tile file. May fall just outside the tile, which is the point of it.
    /// </summary>
    public static Vector2 LocalPoint(TileId id, Landmark l) =>
        new((float)(l.E - id.MinE), (float)(id.MaxN - l.N));

    /// <summary>Whether a solid is the right shape and size to be a landmark store at all.</summary>
    public static bool IsStoreShaped(PlanBox box, Building b)
    {
        float h = b.MaxY - b.MinY;
        return box.Area >= MinArea && box.Area <= MaxArea && h >= MinHeight && h <= MaxHeight;
    }

    /// <summary>
    /// Which of a tile's buildings is a landmark, if any: the store-shaped solid whose footprint
    /// holds the noted point (within <see cref="Tolerance"/>). Ties — a point in the gap between two
    /// qualifying solids — go to the nearer, then the larger, then the lower index, so the answer
    /// never depends on anything but the bytes.
    ///
    /// <para>
    /// <paramref name="boxes"/> comes in rather than being recomputed, so this can run inside
    /// <see cref="BuildingTypes.Detect"/>, which has already built them, without recursing into
    /// <see cref="BuildingTypes.For"/>.
    /// </para>
    /// </summary>
    public static (int Index, Landmark Store)? Match(BuildingTile tile, PlanBox?[] boxes)
    {
        (int Index, Landmark Store)? best = null;
        float bestDist = float.MaxValue, bestArea = 0;
        foreach (var l in Near(tile.Id))
        {
            var at = LocalPoint(tile.Id, l);
            int n = Math.Min(boxes.Length, tile.Buildings.Count);
            for (int i = 0; i < n; i++)
            {
                if (boxes[i] is not { } box) continue;
                if (!IsStoreShaped(box, tile.Buildings[i])) continue;
                float dist = box.DistanceTo(at);
                if (dist > Tolerance) continue;
                if (dist > bestDist - 0.01f && (dist > bestDist + 0.01f || box.Area <= bestArea)) continue;
                best = (i, l);
                bestDist = dist;
                bestArea = box.Area;
            }
        }
        return best;
    }
}
