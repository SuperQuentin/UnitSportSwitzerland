using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// Turns BD TOPO® <c>troncon_de_route</c> into the same <c>.road</c> segments swissTLM3D produces.
///
/// <para>
/// The two road models line up better than the building ones do. Both are centrelines with a
/// class, a surface and a structure flag, so this is mostly a translation table — the work is in
/// choosing honest equivalents rather than in the geometry. <c>Route à 2 chaussées</c> really is
/// the same thing as a <c>richtungsgetrennt</c> road, and gets the same halved width for the same
/// reason: it is one carriageway of two, not the whole road.
/// </para>
///
/// <para>
/// Draping is the shared part. BD TOPO lines carry no usable Z, so every vertex takes its height
/// from our own heightfield, which is exactly what the Swiss extractor does for anything that is
/// not a bridge or a tunnel. That is why the seam works: both sides end up sitting on the same
/// surface, sampled the same way.
/// </para>
/// </summary>
public static class FranceRoads
{
    /// <summary>Metres above the terrain surface, matching <c>RoadExtractor.DrapeOffset</c>.</summary>
    private const double DrapeOffset = 0.35;

    /// <summary>Max spacing between draped vertices, matching the Swiss extractor.</summary>
    private const double MaxDrapeSpacing = 4.0;

    public sealed class Stats
    {
        public int Segments, Skipped, Bridges, Tunnels, Cycle;
        public readonly SortedDictionary<RoadClass, int> ByClass = new();
    }

    public static Dictionary<TileId, List<RoadSegment>> Build(IEnumerable<BdFeature> features,
        IReadOnlySet<TileId> tiles, Func<double, double, double?> heightAt, Stats stats)
    {
        var byTile = new Dictionary<TileId, List<RoadSegment>>();

        foreach (var feature in features)
        {
            var cls = Classify(feature);
            if (cls == null) { stats.Skipped++; continue; }

            var flags = Flags(feature);
            var surface = Surface(feature);
            float width = Width(feature, cls.Value, flags);
            var attributes = Attributes(feature, cls.Value, flags);

            foreach (var ring in feature.Rings)
            {
                if (ring.Count < 2) continue;

                var line = new GeoPackageReader.Polyline(
                    ring.Select(p => p.E).ToArray(),
                    ring.Select(p => p.N).ToArray(),
                    new double[ring.Count]);

                foreach (var piece in PolylineClipper.SplitByTile(line))
                {
                    if (!tiles.Contains(piece.Tile) || piece.Points.Count < 2) continue;

                    var dense = Densify(piece.Points);
                    var points = new float[dense.Count * 3];

                    for (int i = 0; i < dense.Count; i++)
                    {
                        var (e, n, _) = dense[i];
                        double? ground = heightAt(e, n);
                        // Nothing under it means the tile edge; the neighbouring drape covers
                        // the join, so a flat fallback here is never seen.
                        double y = (ground ?? 0) + DrapeOffset + ClassLift(cls.Value);

                        points[i * 3] = (float)(e - piece.Tile.MinE);
                        points[i * 3 + 1] = (float)y;
                        points[i * 3 + 2] = (float)(piece.Tile.MaxN - n);
                    }

                    if (!byTile.TryGetValue(piece.Tile, out var list))
                        byTile[piece.Tile] = list = new List<RoadSegment>();

                    list.Add(new RoadSegment
                    {
                        Class = cls.Value,
                        Surface = surface,
                        Flags = flags,
                        Width = width,
                        Points = points,
                        Attributes = attributes,
                    });

                    stats.Segments++;
                    stats.ByClass[cls.Value] = stats.ByClass.GetValueOrDefault(cls.Value) + 1;
                    if ((flags & RoadFlags.Bridge) != 0) stats.Bridges++;
                    if ((flags & RoadFlags.Tunnel) != 0) stats.Tunnels++;
                    if ((flags & RoadFlags.Cycle) != 0) stats.Cycle++;
                }
            }
        }

        return byTile;
    }

    /// <summary>
    /// BD TOPO <c>nature</c> to <see cref="RoadClass"/>.
    ///
    /// <para>
    /// <c>Rond-point</c> becomes a <see cref="RoadClass.Link"/> rather than a road of its own
    /// class: it is a short connecting arc, which is what Link means here, and it keeps
    /// roundabouts out of the width ordering where they would be drawn as major roads.
    /// </para>
    /// </summary>
    private static RoadClass? Classify(BdFeature feature)
    {
        if (feature.Text("etat_de_l_objet") == "En construction") return null;

        // A "fictif" tronçon is a routing connector with no physical road under it — the
        // French equivalent of the Faehre problem: drawing it lays tarmac where there is none.
        if (feature.Flag("fictif")) return null;

        return feature.Text("nature") switch
        {
            "Type autoroutier" => RoadClass.Motorway,
            "Bretelle" => RoadClass.Ramp,
            "Route à 2 chaussées" => RoadClass.Major,
            "Route à 1 chaussée" => RoadClass.Road,
            "Rond-point" => RoadClass.Link,
            "Route empierrée" => RoadClass.Track,
            "Chemin" => RoadClass.Track,
            "Sentier" => RoadClass.Path,
            "Escalier" => RoadClass.Path,
            "Piste cyclable" => RoadClass.Lane,
            _ => null,
        };
    }

    private static RoadFlags Flags(BdFeature feature)
    {
        var flags = RoadFlags.None;

        // position_par_rapport_au_sol: 0 ground, positive above, negative below
        double position = feature.Number("position_par_rapport_au_sol") ?? 0;
        if (position > 0) flags |= RoadFlags.Bridge;
        else if (position < 0) flags |= RoadFlags.Tunnel;

        if (feature.Text("sens_de_circulation") is "Sens direct" or "Sens inverse")
        {
            // One-way is how BD TOPO records the two halves of a dual carriageway, and it is
            // also how it records an ordinary one-way street. Only the former should be halved
            // in width, so the Divided flag is set from `nature`, not from this.
        }

        if (feature.Text("nature") == "Route à 2 chaussées") flags |= RoadFlags.Divided;

        // Cycle route: a marked facility alongside, a named cycle itinerary, or a voie verte.
        // NOTE: all three of these come back null from the WFS for Haute-Savoie, so no road here
        // is ever flagged Cycle. The test is kept because it is correct where the fields are
        // populated, but the honest position is that French cycle routing is not imported —
        // the Veloland equivalent would have to come from a separate source.
        if (feature.Text("amenagement_cyclable_gauche") is { Length: > 0 } and not "Aucun"
            || feature.Text("amenagement_cyclable_droit") is { Length: > 0 } and not "Aucun"
            || feature.Text("cpx_toponyme_itineraire_cyclable") is { Length: > 0 }
            || feature.Text("cpx_toponyme_voie_verte") is { Length: > 0 })
            flags |= RoadFlags.Cycle;

        if (feature.Text("nature") is "Sentier" or "Chemin") flags |= RoadFlags.Hiking;
        if (feature.Text("nature") == "Escalier") flags |= RoadFlags.Stairs;
        if (feature.Flag("prive")) flags |= RoadFlags.Restricted;

        return flags;
    }

    private static RoadSurface Surface(BdFeature feature) => feature.Text("nature") switch
    {
        "Route empierrée" or "Chemin" or "Sentier" => RoadSurface.Natural,
        "Type autoroutier" or "Bretelle" or "Route à 2 chaussées"
            or "Route à 1 chaussée" or "Rond-point" or "Piste cyclable" => RoadSurface.Paved,
        _ => RoadSurface.Unknown,
    };

    /// <summary>
    /// Carriageway width, preferring the surveyed <c>largeur_de_chaussee</c>.
    ///
    /// <para>
    /// This is better than the Swiss side has: TLM3D gives a width <i>class</i> and we infer
    /// metres from it, while BD TOPO measures the carriageway. It is used directly where
    /// present, and only falls back to the class default when it is missing or absurd.
    /// </para>
    /// </summary>
    private static float Width(BdFeature feature, RoadClass cls, RoadFlags flags)
    {
        double? measured = feature.Number("largeur_de_chaussee");
        if (measured is > 1.0 and < 40.0) return (float)measured.Value;
        return RoadFormat.WidthFor(cls, flags);
    }

    /// <summary>
    /// v3 attributes straight from BD TOPO (#117), which records what TLM does not:
    /// <c>sens_de_circulation</c> (one-way in or against the drawing direction),
    /// <c>nombre_de_voies</c> (lanes, both directions together), <c>largeur_de_chaussee</c>
    /// and <c>importance</c> (1 national .. 6 local). The network stage keeps values a source sets.
    /// </summary>
    private static RoadAttributes Attributes(BdFeature feature, RoadClass cls, RoadFlags flags)
    {
        sbyte oneWay = feature.Text("sens_de_circulation") switch
        {
            "Sens direct" => 1,
            "Sens inverse" => -1,
            _ => 0,
        };
        int lanes = (int)Math.Clamp(feature.Number("nombre_de_voies") ?? 0, 0, 12);
        int fwd = oneWay > 0 ? lanes : oneWay < 0 ? 0 : lanes / 2;
        int bwd = oneWay < 0 ? lanes : oneWay > 0 ? 0 : lanes - lanes / 2;
        double? measured = feature.Number("largeur_de_chaussee");
        int importance = (int)(feature.Number("importance") ?? 0) switch
        {
            1 or 2 => 3,
            3 => 2,
            4 => 1,
            _ => 0,
        };
        return new RoadAttributes(
            Flags: feature.Text("nature") == "Rond-point" ? RoadAttrFlags.Roundabout : RoadAttrFlags.None,
            OneWay: oneWay,
            Layer: RoadFormat.LayerFor(null, flags),
            LanesForward: (byte)fwd, LanesBackward: (byte)bwd,
            Priority: (byte)(importance << 4 | (RoadFormat.PriorityFor(cls, null) & 0x0F)),
            WidthCm: measured is > 1.0 and < 40.0 ? (ushort)Math.Round(measured.Value * 100) : (ushort)0);
    }

    /// <summary>Same per-class depth bias the Swiss extractor uses, so junctions do not z-fight.</summary>
    private static double ClassLift(RoadClass cls) => (12 - (int)cls) * 0.012;

    private static List<(double E, double N, double Z)> Densify(
        List<(double E, double N, double Z)> pts)
    {
        var result = new List<(double, double, double)>(pts.Count * 2);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            result.Add(a);

            double length = Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));
            int steps = Math.Min((int)Math.Ceiling(length / MaxDrapeSpacing), 512);
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                result.Add((a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t, 0));
            }
        }
        result.Add(pts[^1]);
        return result;
    }
}
