using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The traffic-lights test region (#386, <c>RoadGen --test-region</c>): built from scratch into a
/// temp dir by the real network stage, every designed approach gets its lane set, every plan is
/// valid, and the stem of the T turns only left and right (~2 s).
/// </summary>
public class SignalTestRegionTests
{
    [Fact]
    public void Region_builds_with_the_designed_lanes_and_valid_plans()
    {
        string dir = Path.Combine(Path.GetTempPath(), "unitsport-386-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new List<string>();
            int code = SignalTestRegion.Run(dir, null, log.Add);
            Assert.True(code == 0, string.Join("\n", log.Where(l => l.StartsWith("J", StringComparison.Ordinal) || l.Contains("RESULT"))));

            int signals = 0;
            foreach (string path in Directory.EnumerateFiles(dir, "roads_*.road"))
            {
                RoadTile tile;
                using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
                foreach (var s in tile.Signals)
                {
                    signals++;
                    Assert.Empty(s.Plan.Validate());
                }
                foreach (var a in tile.Approaches)
                    Assert.All(a.Lanes, l => Assert.NotEqual(SignalMoves.None, l.Moves));
            }
            Assert.Equal(SignalTestRegion.Junctions.Count, signals);

            // the T's stem (J4, from the south): no straight on, so no lane goes straight
            var tee = SignalTestRegion.Junctions.Single(j => j.Name.StartsWith("J4", StringComparison.Ordinal));
            var id = TileId.FromLv95(tee.E, tee.N);
            RoadTile teeTile;
            using (var fs = File.OpenRead(Path.Combine(dir, RoadFormat.FileName(id)))) teeTile = RoadCodec.Decode(fs);
            var stem = teeTile.Approaches.Single(a => a.Signal >= 0 && Math.Sin(a.Heading) < -0.95);
            Assert.All(stem.Lanes, l => Assert.Equal(SignalMoves.None, l.Moves & SignalMoves.Through));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (Directory.Exists(dir + "_temp")) Directory.Delete(dir + "_temp", recursive: true);
        }
    }
}
