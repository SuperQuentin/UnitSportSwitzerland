using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Turns BD TOPO® building footprints into the same triangle soup swissBUILDINGS3D produces.
///
/// <para>
/// The two sources are not the same shape of data and pretending otherwise is where this would
/// go wrong. swissBUILDINGS3D ships LoD2 solids — real roof planes, already triangulated. BD TOPO
/// ships a <b>footprint plus heights</b>: ground, eave (<c>altitude_minimale_toit</c>) and ridge
/// (<c>altitude_maximale_toit</c>). So the roof has to be built rather than read, and the honest
/// thing is to build only what the attributes actually justify: a pitched roof where an eave and
/// a ridge genuinely differ, and a flat one where they do not.
/// </para>
///
/// <para>
/// Roughly half the buildings here carry both, so half get pitched roofs. The rest are flat, and
/// that is a real difference from the Swiss side rather than something to paper over — a hip
/// invented from a single height would be a guess drawn as if it were surveyed.
/// </para>
/// </summary>
public static class FranceBuildings
{
    /// <summary>
    /// How far below the sampled ground the walls start.
    ///
    /// <para>
    /// Same reason as the Swiss path: our heightfield is not the one the source was referenced
    /// to, so a building seated exactly on it shows daylight under a wall wherever the two
    /// disagree by a few centimetres. Sinking the base hides that without burying anything.
    /// </para>
    /// </summary>
    private const double FoundationDepth = 0.8;

    /// <summary>Eave and ridge must differ by this much before a roof is worth pitching.</summary>
    private const double MinPitch = 0.6;

    /// <summary>Below this a "building" is a shed roof, a canopy or a digitising artefact.</summary>
    private const double MinHeight = 1.5;

    public sealed class Stats
    {
        public int Built, Pitched, Flat, Skipped, NoHeight;
    }

    /// <summary>
    /// Builds every footprint that falls inside one of <paramref name="tiles"/>.
    ///
    /// <para>
    /// A building is assigned whole to the tile containing its centroid, never split. Splitting a
    /// solid across a tile boundary would need the walls capped at the seam; assigning it whole
    /// means it hangs a few metres over the edge, which nothing downstream minds because tiles
    /// are drawn together.
    /// </para>
    /// </summary>
    public static Dictionary<TileId, List<Building>> Build(IEnumerable<BdFeature> features,
        IReadOnlySet<TileId> tiles, Func<double, double, double?> heightAt, Stats stats)
    {
        var byTile = new Dictionary<TileId, List<Building>>();

        foreach (var feature in features)
        {
            if (feature.Text("etat_de_l_objet") == "En ruine") { stats.Skipped++; continue; }

            // The outer ring only. BD TOPO courtyards are rare and a hole in a footprint costs
            // an ear-clipper that handles them; without one, the courtyard is simply filled.
            var ring = feature.Rings[0];
            if (ring.Count < 4) { stats.Skipped++; continue; }

            var footprint = CleanRing(ring);
            if (footprint.Count < 3) { stats.Skipped++; continue; }

            double centroidE = 0, centroidN = 0;
            foreach (var (e, n) in footprint) { centroidE += e; centroidN += n; }
            centroidE /= footprint.Count;
            centroidN /= footprint.Count;

            var tile = TileId.FromLv95(centroidE, centroidN);
            if (!tiles.Contains(tile)) continue;

            double? ground = SampleGround(footprint, centroidE, centroidN, heightAt);
            if (ground == null) { stats.Skipped++; continue; }

            var heights = Heights(feature);
            if (heights == null) { stats.NoHeight++; continue; }
            var (wallHeight, ridgeHeight) = heights.Value;

            double baseZ = ground.Value - FoundationDepth;
            double eaveZ = ground.Value + wallHeight;
            double ridgeZ = ground.Value + ridgeHeight;

            bool pitched = ridgeZ - eaveZ >= MinPitch;
            if (pitched) stats.Pitched++; else stats.Flat++;

            var triangles = new List<float>();
            AppendWalls(triangles, footprint, baseZ, eaveZ, tile);
            if (pitched) AppendPitchedRoof(triangles, footprint, eaveZ, ridgeZ, tile);
            else AppendCap(triangles, footprint, eaveZ, tile);

            if (triangles.Count == 0) { stats.Skipped++; continue; }

            if (!byTile.TryGetValue(tile, out var list)) byTile[tile] = list = new List<Building>();
            list.Add(new Building
            {
                Kind = Classify(feature, wallHeight),
                Egid = 0,                       // no EGID in France; the RNB id is a string
                YearBuilt = 0,                  // BD TOPO carries no construction year
                Floors = (byte)Math.Clamp((int)Math.Round(wallHeight / 2.9), 0, 255),
                MinY = (float)baseZ,
                MaxY = (float)ridgeZ,
                Triangles = triangles.ToArray(),
            });
            stats.Built++;
        }

        return byTile;
    }

    /// <summary>
    /// Wall height to the eave and total height to the ridge, in metres above ground.
    ///
    /// <para>
    /// Prefers the surveyed roof altitudes and falls back to <c>hauteur</c>, which BD TOPO
    /// defines as ground to the <i>top</i>. Using <c>hauteur</c> as a wall height would make
    /// every fallback building a storey too tall.
    /// </para>
    /// </summary>
    private static (double Wall, double Ridge)? Heights(BdFeature feature)
    {
        double? groundZ = feature.Number("altitude_minimale_sol");
        double? eaveZ = feature.Number("altitude_minimale_toit");
        double? ridgeZ = feature.Number("altitude_maximale_toit");
        double? hauteur = feature.Number("hauteur");

        if (groundZ != null && eaveZ != null && ridgeZ != null)
        {
            double wall = eaveZ.Value - groundZ.Value;
            double ridge = ridgeZ.Value - groundZ.Value;
            if (ridge >= MinHeight && wall > 0.5 && ridge >= wall)
                return (wall, ridge);
        }

        if (hauteur is >= MinHeight)
        {
            // no roof shape known: a plain box to the stated height
            return (hauteur.Value, hauteur.Value);
        }

        return null;
    }

    /// <summary>
    /// Maps BD TOPO's <c>nature</c> and <c>usage_1</c> onto the existing building kinds.
    ///
    /// <para>
    /// <c>nature</c> is checked first because it is the specific field — a church is an
    /// <c>Eglise</c> whatever its recorded usage. Residential falls back to a storey count for
    /// the house/apartment split, exactly as the Swiss path does when GWR has no class: there is
    /// no dwelling count in BD TOPO either.
    /// </para>
    /// </summary>
    private static BuildingKind Classify(BdFeature feature, double wallHeight)
    {
        if (feature.Text("etat_de_l_objet") == "En construction")
            return BuildingKind.UnderConstruction;

        switch (feature.Text("nature"))
        {
            case "Eglise" or "Chapelle": return BuildingKind.Sacral;
            case "Château" or "Arc de triomphe" or "Tour, donjon": return BuildingKind.Civic;
            case "Silo" or "Réservoir": return BuildingKind.Industrial;
            case "Serre": return BuildingKind.Agricultural;
        }

        return feature.Text("usage_1") switch
        {
            "Annexe" => BuildingKind.Annex,
            "Agricole" => BuildingKind.Agricultural,
            "Commercial et services" => BuildingKind.Commercial,
            "Industriel" => BuildingKind.Industrial,
            "Religieux" => BuildingKind.Sacral,
            "Sportif" or "Santé" or "Enseignement" or "Administratif" => BuildingKind.Civic,
            "Résidentiel" => wallHeight > 8.0 ? BuildingKind.Apartment : BuildingKind.House,
            _ => feature.Text("nature") == "Industriel, agricole ou commercial"
                ? BuildingKind.Industrial
                : BuildingKind.Other,
        };
    }

    /// <summary>
    /// Ground level under the footprint: the median of the corners plus the centroid.
    ///
    /// <para>
    /// The median rather than the minimum, for the same reason the Swiss path uses one — on a
    /// slope the lowest corner drags the whole building down into the hillside, and one bad
    /// sample at a cliff edge should not decide where a house sits.
    /// </para>
    /// </summary>
    private static double? SampleGround(List<(double E, double N)> ring,
        double centroidE, double centroidN, Func<double, double, double?> heightAt)
    {
        var samples = new List<double>();
        if (heightAt(centroidE, centroidN) is { } middle) samples.Add(middle);
        foreach (var (e, n) in ring)
            if (heightAt(e, n) is { } h) samples.Add(h);

        if (samples.Count == 0) return null;
        samples.Sort();
        return samples[samples.Count / 2];
    }

    /// <summary>Drops the closing duplicate and any coincident points an ear clipper would choke on.</summary>
    private static List<(double E, double N)> CleanRing(List<(double E, double N)> ring)
    {
        var cleaned = new List<(double E, double N)>(ring.Count);
        foreach (var p in ring)
        {
            if (cleaned.Count > 0)
            {
                var q = cleaned[^1];
                if (Math.Abs(p.E - q.E) < 1e-6 && Math.Abs(p.N - q.N) < 1e-6) continue;
            }
            cleaned.Add(p);
        }

        if (cleaned.Count > 1)
        {
            var first = cleaned[0];
            var last = cleaned[^1];
            if (Math.Abs(first.E - last.E) < 1e-6 && Math.Abs(first.N - last.N) < 1e-6)
                cleaned.RemoveAt(cleaned.Count - 1);
        }

        // counter-clockwise in LV95, so the tile-local flip below yields outward normals
        if (SignedArea(cleaned) < 0) cleaned.Reverse();

        // Drop vertices collinear with their neighbours. BD TOPO footprints carry plenty of
        // them, and a collinear corner is neither convex nor reflex — the ear clipper can never
        // remove it, so with enough of them it runs out of ears and gives up mid-polygon.
        for (int pass = 0; pass < 2 && cleaned.Count > 3; pass++)
        {
            var kept = new List<(double E, double N)>(cleaned.Count);
            for (int i = 0; i < cleaned.Count; i++)
            {
                var previous = cleaned[(i - 1 + cleaned.Count) % cleaned.Count];
                var here = cleaned[i];
                var next = cleaned[(i + 1) % cleaned.Count];

                double cross = (here.E - previous.E) * (next.N - here.N)
                             - (here.N - previous.N) * (next.E - here.E);
                if (Math.Abs(cross) > 1e-4) kept.Add(here);   // ~0.1 mm² of doubled area
            }
            if (kept.Count < 3 || kept.Count == cleaned.Count) break;
            cleaned = kept;
        }

        return cleaned;
    }

    private static double SignedArea(List<(double E, double N)> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += a.E * b.N - b.E * a.N;
        }
        return sum * 0.5;
    }

    // ---- geometry ------------------------------------------------------------------------
    // Tile-local frame: X east, Y altitude, Z south from the tile's NW corner. The Z flip is
    // what turns a counter-clockwise LV95 ring into a clockwise one, so wall normals face out.

    private static void AppendWalls(List<float> tris, List<(double E, double N)> ring,
        double baseZ, double topZ, TileId tile)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            Quad(tris, tile, a, baseZ, a, topZ, b, topZ, b, baseZ);
        }
    }

    /// <summary>
    /// Fills a ring at one height — the flat top of a building, or the flat part of a hip.
    ///
    /// <para>
    /// Ear clipping, not a fan. A fan from vertex 0 is only valid for a <b>convex</b> polygon,
    /// and on a concave one it emits triangles that lie outside the shape and are wound the other
    /// way — which is a roof you can see through into the building. Measured 2,913 downward-facing
    /// roof triangles across 759 of 1,239 buildings, because an ordinary house with a porch or a
    /// garage is already concave.
    /// </para>
    ///
    /// <para>
    /// Self-contained rather than shared with RoadGen's clipper, which is tied to that tool's own
    /// <c>Vec2</c>; hoisting it would drag the geometry library into the preprocessor for forty
    /// lines. If a third caller ever wants one, that is the moment to move it.
    /// </para>
    /// </summary>
    private static void AppendCap(List<float> tris, List<(double E, double N)> ring,
        double z, TileId tile)
    {
        if (ring.Count < 3) return;

        var remaining = new List<int>(ring.Count);
        for (int i = 0; i < ring.Count; i++) remaining.Add(i);

        // bounded: a ring that stops yielding ears is degenerate, and the fan below closes it
        int guard = ring.Count * ring.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int k = 0; k < remaining.Count; k++)
            {
                int previous = remaining[(k - 1 + remaining.Count) % remaining.Count];
                int current = remaining[k];
                int next = remaining[(k + 1) % remaining.Count];

                if (!IsEar(ring, remaining, previous, current, next)) continue;

                Triangle(tris, tile, ring[previous], z, ring[current], z, ring[next], z);
                remaining.RemoveAt(k);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }

        for (int k = 1; k + 1 < remaining.Count; k++)
            Triangle(tris, tile, ring[remaining[0]], z, ring[remaining[k]], z, ring[remaining[k + 1]], z);
    }

    /// <summary>Convex corner, with no other remaining vertex inside the candidate triangle.</summary>
    private static bool IsEar(List<(double E, double N)> ring, List<int> remaining,
        int previous, int current, int next)
    {
        var a = ring[previous];
        var b = ring[current];
        var c = ring[next];

        // counter-clockwise ring, so a convex corner turns left
        double cross = (b.E - a.E) * (c.N - b.N) - (b.N - a.N) * (c.E - b.E);
        if (cross <= 0) return false;

        foreach (int i in remaining)
        {
            if (i == previous || i == current || i == next) continue;
            if (InTriangle(ring[i], a, b, c)) return false;
        }
        return true;
    }

    private static bool InTriangle((double E, double N) p,
        (double E, double N) a, (double E, double N) b, (double E, double N) c)
    {
        // A vertex sitting exactly on the candidate ear's edge must NOT count as inside, or an
        // ear is never found, the clipper stalls, and the fan fallback emits the very triangles
        // this exists to avoid. The tolerance is in square metres of doubled area.
        const double OnEdge = -1e-7;
        double d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
        bool negative = d1 < OnEdge || d2 < OnEdge || d3 < OnEdge;
        bool positive = d1 > -OnEdge || d2 > -OnEdge || d3 > -OnEdge;
        return !(negative && positive);

        static double Side((double E, double N) p, (double E, double N) a, (double E, double N) b) =>
            (p.E - b.E) * (a.N - b.N) - (a.E - b.E) * (p.N - b.N);
    }

    /// <summary>
    /// A hipped roof: the footprint stepped inward and lifted to the ridge height.
    ///
    /// <para>
    /// The obvious construction — pick a ridge line along the building's long axis and lift every
    /// eave vertex to its perpendicular projection on it — was here first and is <b>wrong for any
    /// concave footprint</b>. On an L-shaped house two adjacent vertices project to opposite ends
    /// of the ridge, so the quad between them is a bowtie stretched across the interior, and it
    /// renders as a vertical sail sticking up through the roof. Measured 236 of 541 buildings
    /// with vertical or downward-facing roof faces, because real buildings are mostly not
    /// rectangles.
    /// </para>
    ///
    /// <para>
    /// Insetting instead is well defined for any simple polygon: offset every edge inward by the
    /// same distance along its own normal, and each roof face is the strip between an original
    /// edge and its offset — always planar, always sloping the right way. A narrow building's
    /// inset collapses to nearly a line, which is a ridge; a wide one keeps a flat top, which is
    /// what a hip on a large footprint really looks like.
    /// </para>
    ///
    /// <para>
    /// The offset can still fold where a concave notch is narrower than twice the inset, so it is
    /// checked rather than trusted: every inset vertex must land inside the original footprint.
    /// If one does not the inset is halved and retried, and a footprint that never passes gets a
    /// flat roof instead of a broken one.
    /// </para>
    /// </summary>
    private static void AppendPitchedRoof(List<float> tris, List<(double E, double N)> ring,
        double eaveZ, double ridgeZ, TileId tile)
    {
        // The pitch sets the inset: with a rise of (ridge - eave), a ~35 degree roof runs back
        // about 1.4 times that horizontally. Capped so a tall narrow turret cannot inset past
        // its own walls before the fold check even runs.
        double rise = ridgeZ - eaveZ;
        double inset = Math.Min(rise * 1.43, 6.0);

        for (int attempt = 0; attempt < 7 && inset > 0.10; attempt++, inset *= 0.6)
        {
            var capped = Inset(ring, inset);
            if (capped == null) continue;

            // Orientation first, and this is the test that matters. Where the inset exceeds the
            // building's half-width the offset edges cross and the ring turns inside out — but
            // every one of its vertices is still *inside* the original footprint, so a
            // containment check passes it. Measured on a 6.2 x 1.4 m outbuilding: a 0.89 m inset
            // flipped the ring clockwise, which inverted the cap so the roof faced down and
            // bowtied two of the slope quads.
            if (SignedArea(capped) <= 1e-3) continue;

            bool folded = false;
            foreach (var p in capped)
                if (!Contains(ring, p)) { folded = true; break; }
            if (folded) continue;

            // Build it aside and check it before committing. Chasing the individual ways an
            // offset polygon can misbehave — reflex corners, notches narrower than the inset,
            // near-collinear runs — was three rounds of fixes that each halved the damage and
            // none of which reached zero. A roof is only correct if every face points upward, so
            // testing that directly is both simpler and complete: this cannot emit a broken roof.
            var scratch = new List<float>();
            for (int i = 0; i < ring.Count; i++)
            {
                int j = (i + 1) % ring.Count;
                Quad(scratch, tile, ring[i], eaveZ, ring[j], eaveZ, capped[j], ridgeZ, capped[i], ridgeZ);
            }
            AppendCap(scratch, capped, ridgeZ, tile);

            if (!FacesUp(scratch)) continue;

            tris.AddRange(scratch);
            return;
        }

        // Nothing safe to pitch, so leave it flat — and check that too, because the cap of a
        // concave footprint is itself only as good as the ear clipper.
        var flat = new List<float>();
        AppendCap(flat, ring, eaveZ, tile);
        if (FacesUp(flat)) tris.AddRange(flat);
    }

    /// <summary>
    /// True when every triangle in a roof faces upward.
    ///
    /// <para>
    /// The one property that matters. A downward face is a roof you see through into the
    /// building; a vertical one is a sail sticking out of it. Both are obvious on screen and
    /// neither is worth trying to salvage.
    /// </para>
    /// </summary>
    private static bool FacesUp(List<float> tris)
    {
        for (int t = 0; t + 8 < tris.Count; t += 9)
        {
            double ux = tris[t + 3] - tris[t], uy = tris[t + 4] - tris[t + 1], uz = tris[t + 5] - tris[t + 2];
            double wx = tris[t + 6] - tris[t], wy = tris[t + 7] - tris[t + 1], wz = tris[t + 8] - tris[t + 2];

            double nx = uy * wz - uz * wy;
            double ny = uz * wx - ux * wz;
            double nz = ux * wy - uy * wx;

            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length < 1e-9) continue;              // a sliver contributes nothing either way
            // 0.25 is about a 75 degree pitch. Anything steeper is not a roof, it is a
            // wall standing above the eave — which is what a heavily shrunk inset produces, and
            // it looked like a broken roof even though the winding was correct.
            if (ny / length < 0.25) return false;
        }
        return true;
    }

    /// <summary>
    /// Offsets every edge inward by <paramref name="distance"/>, corners meeting on the angle
    /// bisector. Null where a corner is too sharp to offset without the join running away.
    /// </summary>
    private static List<(double E, double N)>? Inset(List<(double E, double N)> ring, double distance)
    {
        int n = ring.Count;
        var result = new List<(double E, double N)>(n);

        for (int i = 0; i < n; i++)
        {
            var previous = ring[(i - 1 + n) % n];
            var here = ring[i];
            var next = ring[(i + 1) % n];

            var a = InwardNormal(previous, here);
            var b = InwardNormal(here, next);
            if (a == null || b == null) return null;

            double bx = a.Value.X + b.Value.X, by = a.Value.Y + b.Value.Y;
            double length = Math.Sqrt(bx * bx + by * by);
            if (length < 1e-6) return null;          // a 180 degree spike
            bx /= length; by /= length;

            // how far along the bisector reaches both offset lines
            // Signed, deliberately. At a reflex corner the two inward normals sum to a vector
            // pointing outward and this cosine goes negative, which makes the step negative and
            // walks the vertex the other way along the bisector — exactly where the two offset
            // lines actually meet. Rejecting negatives (the first version) threw away every
            // concave footprint, which is most houses with a porch or a garage.
            double cos = bx * a.Value.X + by * a.Value.Y;
            if (Math.Abs(cos) < 0.2) return null;    // needle corner: the join shoots off
            double step = distance / cos;

            result.Add((here.E + bx * step, here.N + by * step));
        }

        return result;

        // the ring is counter-clockwise, so the interior lies to the left of each edge
        static (double X, double Y)? InwardNormal((double E, double N) from, (double E, double N) to)
        {
            double dx = to.E - from.E, dy = to.N - from.N;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) return null;
            return (-dy / length, dx / length);
        }
    }

    /// <summary>Even-odd point-in-polygon, used to reject an inset that folded outside.</summary>
    private static bool Contains(List<(double E, double N)> ring, (double E, double N) p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if (a.N > p.N != b.N > p.N
                && p.E < (b.E - a.E) * (p.N - a.N) / (b.N - a.N) + a.E)
                inside = !inside;
        }
        return inside;
    }

    private static void Quad(List<float> tris, TileId tile,
        (double E, double N) a, double az, (double E, double N) b, double bz,
        (double E, double N) c, double cz, (double E, double N) d, double dz)
    {
        Triangle(tris, tile, a, az, b, bz, c, cz);
        Triangle(tris, tile, a, az, c, cz, d, dz);
    }

    private static void Triangle(List<float> tris, TileId tile,
        (double E, double N) a, double az, (double E, double N) b, double bz,
        (double E, double N) c, double cz)
    {
        Vertex(tris, tile, a, az);
        Vertex(tris, tile, b, bz);
        Vertex(tris, tile, c, cz);
    }

    private static void Vertex(List<float> tris, TileId tile, (double E, double N) p, double z)
    {
        tris.Add((float)(p.E - tile.MinE));
        tris.Add((float)z);
        tris.Add((float)(tile.MaxN - p.N));
    }
}
