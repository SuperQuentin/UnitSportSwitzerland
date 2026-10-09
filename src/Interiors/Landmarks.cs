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
/// of flats. Worse, any radius at all destroys agreement between peers: a point near a tile edge is
/// offered to two tiles, <see cref="BuildingTypes.Detect"/> sees one tile at a time and cannot
/// compare across them, so two tiles could each claim a store and the two halves of the region
/// would disagree about where the IKEA is. That is not hypothetical — St. Gallen's point is 0.4 m
/// from a tile edge, and its store is in the tile next door.
/// </para>
///
/// <para>
/// <b>Strict containment is what makes the answer unique.</b> Two buildings are disjoint solids, so
/// exactly one footprint in exactly one tile can hold a given point: the match cannot depend on how
/// the tiles are cut, in what order anything is read, or which tiles happen to be loaded. All nine
/// points were checked to fall inside their store's footprint, which is the standard each row has
/// to meet. The price is that a row drifting a few metres off a wall stops matching — reported at
/// once by <c>--ikeacheck</c>, and one line to correct, which is much the better failure.
/// </para>
///
/// <para>
/// The shape band below was measured off those same nine solids. Rules, the sources behind each
/// coordinate, and how to re-check them: <c>docs/notes/terrain/ikea.md</c>.
/// </para>
/// </summary>
public static class Landmarks
{
    /// <summary>
    /// How far outside a tile a point may lie for that tile to bother looking at it. Only a
    /// pre-filter, and generous: a store is up to 300 m across, its record belongs to the tile
    /// holding its centre, and its footprint can therefore cover a point well inside a neighbour.
    /// Being generous here costs one box test per tile and changes no answer — strict containment
    /// in <see cref="Match"/> is what decides.
    /// </summary>
    public const float Reach = 400f;

    // The store signature, measured off the nine real solids: a big LOW box. The height band is
    // what keeps a shopping centre's tower or an 8-storey block from ever being a store, and the
    // area floor keeps out the garden centre, the trolley shelter and the substation.
    public const float MinArea = 6000f, MaxArea = 45000f;
    public const float MinHeight = 9f, MaxHeight = 30f;

    /// <summary>
    /// The ten IKEA Einrichtungshäuser. The plan-and-order points (Zürich Pelikanstrasse, Bern
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
        // the tenth, and the first in a mountain canton: Zone Commerciale des Babioux. The address
        // point published for it sits ~200 m off the store, out in the retail zone, so this is the
        // one that lands on the building — 16,576 m2, 187 x 89 m, which is the 23,000 m2 of sales
        // floor over two storeys
        Ikea1("Riddes", 46.165237, 7.210981),
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
    /// <see cref="Reach"/> of its edge. More than one tile may be offered the same landmark — that
    /// is the point of it — and at most one of them can then hold the building that contains it.
    /// </summary>
    public static IEnumerable<Landmark> Near(TileId id)
    {
        foreach (var l in All)
            if (l.E >= id.MinE - Reach && l.E <= id.MinE + ChunkFormat.TileSizeM + Reach
                && l.N >= id.MinN - Reach && l.N <= id.MaxN + Reach)
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
    /// <b>contains</b> the noted point. Since buildings are disjoint solids, at most one can, so
    /// there is nothing to arbitrate and no way for two tiles to disagree; the larger-then-lower
    /// index tiebreak only exists so that a pathological tile (overlapping solids in the source
    /// data) still gives one fixed answer rather than depending on anything.
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
        float bestArea = 0;
        foreach (var l in Near(tile.Id))
        {
            var at = LocalPoint(tile.Id, l);
            int n = Math.Min(boxes.Length, tile.Buildings.Count);
            for (int i = 0; i < n; i++)
            {
                if (boxes[i] is not { } box) continue;
                if (!IsStoreShaped(box, tile.Buildings[i])) continue;
                if (box.DistanceTo(at) > 0f) continue;      // the point must be on the building
                if (box.Area <= bestArea) continue;
                best = (i, l);
                bestArea = box.Area;
            }
        }
        return best;
    }
}
