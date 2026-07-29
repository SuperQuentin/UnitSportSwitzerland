namespace UnitSport.Tools.RoadGen.Import;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Loads the <c>.road</c> files the existing preprocessor already writes, so the generator can
/// be pointed at real swissTLM3D geometry rather than only at hand-built test cases.
///
/// <para>
/// Several tiles can be loaded at once and they are merged into one network in LV95 metres.
/// That matters: roads are clipped at tile boundaries, so a junction sitting on a boundary is
/// split across two files, and loading tiles one at a time would see two dead ends where there
/// is one crossroads.
/// </para>
/// </summary>
public static class RoadTileImporter
{
    public sealed record ImportStats(int Tiles, int Segments, int Skipped);

    /// <param name="dividedScale">
    /// Width multiplier for lines flagged <c>richtungsgetrennt</c>. swissTLM3D draws a
    /// direction-separated road as <i>two</i> centrelines, one per carriageway, but the
    /// existing pipeline gives each of them the full class width — so both halves of a
    /// motorway are drawn 11 m wide and then painted over each other. Left at 1.0 by default
    /// so this tool reports the effect rather than silently changing how the world looks.
    /// </param>
    public static (RoadNetwork Network, ImportStats Stats) Load(string chunkDir, IEnumerable<TileId> tiles,
        double dividedScale = 1.0)
    {
        var net = new RoadNetwork();
        int tileCount = 0, segments = 0, skipped = 0;

        foreach (var id in tiles)
        {
            string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
            if (!File.Exists(path)) continue;

            using var stream = File.OpenRead(path);
            var tile = RoadCodec.Decode(stream);
            tileCount++;

            foreach (var segment in tile.Segments)
            {
                if (segment.PointCount < 2) { skipped++; continue; }

                var points = new List<Vec2>(segment.PointCount);
                for (int i = 0; i < segment.PointCount; i++)
                {
                    // tile-local is X east, Z south from the NW corner; plan view wants LV95
                    double east = id.MinE + segment.Points[i * 3];
                    double north = id.MaxN - segment.Points[i * 3 + 2];
                    points.Add(new Vec2(east, north));
                }

                net.AddLink(Polyline.Dedupe(points), ProfileFor(segment, dividedScale), LayerFor(segment.Flags));
                segments++;
            }
        }

        return (net, new ImportStats(tileCount, segments, skipped));
    }

    /// <summary>
    /// Bridges and tunnels go on their own layer so the graph builder never welds them to what
    /// they pass over or under. Without this a motorway viaduct and the lane beneath it share
    /// endpoints wherever their plan-view geometry happens to touch, and the junction solver
    /// dutifully builds a crossroads in mid-air.
    /// </summary>
    private static int LayerFor(RoadFlags flags) =>
        (flags & RoadFlags.Bridge) != 0 ? 1 : (flags & RoadFlags.Tunnel) != 0 ? -1 : 0;

    private static RoadProfile ProfileFor(RoadSegment segment, double dividedScale)
    {
        var baseProfile = segment.Class switch
        {
            RoadClass.Motorway => RoadProfile.Motorway,
            RoadClass.Expressway => RoadProfile.Expressway,
            RoadClass.Ramp => RoadProfile.Ramp,
            RoadClass.Major => RoadProfile.Major,
            RoadClass.Road => RoadProfile.Road,
            RoadClass.Minor => RoadProfile.Minor,
            RoadClass.Lane or RoadClass.Link or RoadClass.Square => RoadProfile.Lane,
            RoadClass.Track => RoadProfile.Track,
            RoadClass.Path => RoadProfile.Path,
            RoadClass.Railway => RoadProfile.Railway,
            _ => RoadProfile.Lane,
        };

        // the surveyed width wins where TLM gives one, and an unpaved road loses its markings
        // regardless of how wide it is
        double width = segment.Width > 0.1 ? segment.Width : baseProfile.Width;
        if ((segment.Flags & RoadFlags.Divided) != 0) width *= dividedScale;
        bool paved = segment.Surface == RoadSurface.Paved;

        return baseProfile with
        {
            Width = width,
            Paved = paved,
            Markings = paved ? baseProfile.Markings : MarkingPlan.None,
        };
    }
}
