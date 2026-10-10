using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.TestRegion;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The rules that differ between junctions with and without lights are listed in one place (#711 phase 4,
/// <see cref="JunctionRules"/>): the test region's twins, the same T in town once with lights and once without, differ in
/// their paint only where a rule one of them has and the other has not draws it.
/// </summary>
public class JunctionRulesTests(SignalTestRegionFixture region) : IClassFixture<SignalTestRegionFixture>
{
    /// <summary>The paint within 45 m of a twin's centre, counted by type and colour.</summary>
    private Dictionary<(PaintType Type, uint Rgba), int> PaintAt(double e)
    {
        var id = TileId.FromLv95(e, SignalTestRegion.TwinRowN);
        double cx = e - id.MinE, cz = id.MaxN - SignalTestRegion.TwinRowN;
        return region.Tile(e, SignalTestRegion.TwinRowN).Paint
            .Where(p => p.Vertices.Length >= 3 && Enumerable.Range(0, p.Vertices.Length / 3)
                .Any(i => Math.Abs(p.Vertices[i * 3] - cx) < 45 && Math.Abs(p.Vertices[i * 3 + 2] - cz) < 45))
            .GroupBy(p => (p.Type, p.Rgba)).ToDictionary(g => g.Key, g => g.Count());
    }

    [Fact]
    public void The_same_arms_with_and_without_lights_differ_only_in_the_listed_markings()
    {
        var lit = PaintAt(SignalTestRegion.TwinLitE);
        var unlit = PaintAt(SignalTestRegion.TwinUnlitE);
        var differing = JunctionRules.Lights.Rules.Except(JunctionRules.NoLights.Rules)
            .Concat(JunctionRules.NoLights.Rules.Except(JunctionRules.Lights.Rules)).ToList();
        var listed = differing.SelectMany(JunctionRules.PaintOf).ToHashSet();
        var unlisted = lit.Keys.Union(unlit.Keys)
            .Where(key => lit.GetValueOrDefault(key) != unlit.GetValueOrDefault(key) && !listed.Contains(key))
            .Select(key => $"{key.Type} {key.Rgba:X8}: {lit.GetValueOrDefault(key)} with lights, {unlit.GetValueOrDefault(key)} without").ToList();
        Assert.True(unlisted.Count == 0, "no listed rule draws: " + string.Join("; ", unlisted));
        // what tells them apart is there: the lights' stop line and crosswalks, the yielding arm's teeth without them
        Assert.True(lit.GetValueOrDefault((PaintType.StopLine, PaintEmitterWhite)) > unlit.GetValueOrDefault((PaintType.StopLine, PaintEmitterWhite)));
        Assert.True(lit.GetValueOrDefault((PaintType.YellowSolid, PaintEmitterYellow)) > 0, "no crosswalk at the lights");
        Assert.Equal(0, lit.GetValueOrDefault((PaintType.SharkTooth, PaintEmitterWhite)));
        Assert.True(unlit.GetValueOrDefault((PaintType.SharkTooth, PaintEmitterWhite)) > 0, "no give-way teeth without lights");
        // and the layout they share is the same: arrows, hatches
        Assert.Equal(lit.GetValueOrDefault((PaintType.Arrow, PaintEmitterWhite)), unlit.GetValueOrDefault((PaintType.Arrow, PaintEmitterWhite)));
        Assert.Equal(lit.GetValueOrDefault((PaintType.Hatch, PaintEmitterWhite)), unlit.GetValueOrDefault((PaintType.Hatch, PaintEmitterWhite)));
    }

    [Fact]
    public void Every_crossing_meets_only_sloped_kerbs()
    {
        // #711, the user's rule: a walker on a zebra or a rider on a bike crossing never meets a vertical kerb
        var log = new List<string>();
        int code = UnitSport.Tools.RoadGen.Diagnostics.KerbCheck.Run(region.Dir, null, true, log.Add);
        Assert.True(code == 0, string.Join(" | ", log));
        Assert.Contains(log, l => l.Contains("zebras") && !l.Contains("zebras 0,"));   // it checked some
    }

    [Fact]
    public void Every_rule_belongs_to_one_kind()
    {
        Assert.Empty(JunctionRules.Lights.Rules.Intersect(JunctionRules.NoLights.Rules));
        Assert.Equal(Enum.GetValues<JunctionRule>().Length, JunctionRules.Lights.Rules.Count + JunctionRules.NoLights.Rules.Count);
    }

    private const uint PaintEmitterWhite = UnitSport.Tools.RoadGen.Meshing.PaintEmitter.White, PaintEmitterYellow = UnitSport.Tools.RoadGen.Meshing.PaintEmitter.Yellow;
}
