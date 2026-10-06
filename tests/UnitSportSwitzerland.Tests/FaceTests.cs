using UnitSport.Avatar.Face;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The procedural faces' data (#657, src/Avatar/Face, linked in): the genome's packing the shader decodes, the presets, the expressions.</summary>
public class FaceTests
{
    // the face band's second UV is two float32s: whole numbers are exact only below 2^24
    private const int Exact = 1 << 24;

    public static IEnumerable<object[]> Genomes()
    {
        for (int i = 0; i < FaceGenome.PresetCount; i++) yield return new object[] { FaceGenome.Preset(i).WithSeed(i * 37) };
        for (uint s = 0; s < 64; s++) yield return new object[] { FaceGenome.ForSeed(s * 2654435761u, (int)(s % 4)) };
    }

    [Theory]
    [MemberData(nameof(Genomes))]
    public void Code_round_trips_and_fits_a_float(FaceGenome g)
    {
        var (low, high) = g.Code;
        Assert.InRange(low, 0, Exact - 1);
        Assert.InRange(high, 0, Exact - 1);
        Assert.Equal((float)low, (double)low);
        Assert.Equal(g, FaceGenome.FromCode(low, high));
    }

    [Fact]
    public void Steps_out_of_range_are_clamped_not_spilled()
    {
        var g = FaceGenome.Preset(0) with { EyeSize = 9, Iris = -2, MouthWidth = 4 };
        var back = FaceGenome.FromCode(g.Code.Low, g.Code.High);
        Assert.Equal(3, back.EyeSize);
        Assert.Equal(0, back.Iris);
        Assert.Equal(3, back.MouthWidth);
        // and nothing else moved
        Assert.Equal(g.Eyes, back.Eyes);
        Assert.Equal(g.Lashes, back.Lashes);
        Assert.Equal(g.Brow, back.Brow);
    }

    [Fact]
    public void Presets_fit_the_four_replicated_bits_and_keep_the_old_names()
    {
        Assert.InRange(FaceGenome.PresetCount, 8, 16);
        // Appearance.Face is saved and replicated: the #394 faces keep their index
        string[] old = { "anime", "calm", "sharp", "cute", "freckles", "stern", "grin", "stubble" };
        for (int i = 0; i < old.Length; i++) Assert.Equal(old[i], FaceGenome.PresetName(i));
        Assert.Equal(FaceGenome.PresetName(1), FaceGenome.PresetName(1 + FaceGenome.PresetCount));
        var codes = Enumerable.Range(0, FaceGenome.PresetCount).Select(i => FaceGenome.Preset(i).Code).ToHashSet();
        Assert.Equal(FaceGenome.PresetCount, codes.Count);
    }

    [Fact]
    public void Figures_are_drawn_on_a_finer_grid_than_the_atlas()
    {
        for (int i = 0; i < FaceGenome.PresetCount; i++) Assert.Equal(32, FaceGenome.Preset(i).Pixels);
    }

    [Fact]
    public void Seeded_faces_are_the_same_everywhere_and_vary()
    {
        Assert.Equal(FaceGenome.ForSeed(1234), FaceGenome.ForSeed(1234));
        var many = Enumerable.Range(0, 200).Select(s => FaceGenome.ForSeed((uint)s) with { Seed = 0 }).ToHashSet();
        Assert.True(many.Count > 150, $"only {many.Count} distinct faces from 200 seeds");
        Assert.Equal(8, Enumerable.Range(0, 400).Select(s => FaceGenome.ForSeed((uint)s).Eyes).Distinct().Count());
    }

    [Fact]
    public void Expressions_stay_in_the_shaders_ranges()
    {
        foreach (var e in Enum.GetValues<FaceExpression>())
        {
            var s = FaceExpressions.Of(e);
            Assert.InRange(s.OpenL, 0f, 1f);
            Assert.InRange(s.OpenR, 0f, 1f);
            Assert.InRange(s.Jaw, 0f, 1f);
            Assert.InRange(s.Blush, 0f, 1f);
            Assert.InRange(s.Squint, 0f, 1f);
            foreach (float v in new[] { s.Smile, s.Wide, s.Brow, s.GazeX, s.GazeY }) Assert.InRange(v, -1f, 1f);
        }
        Assert.Equal(FaceEyes.Normal, FaceExpressions.Of(FaceExpression.Neutral).Special);
        Assert.Equal(FaceEyes.Cross, FaceExpressions.Of(FaceExpression.Out).Special);
    }

    [Fact]
    public void Lerp_ends_on_its_ends()
    {
        var a = FaceExpressions.Of(FaceExpression.Neutral);
        var b = FaceExpressions.Of(FaceExpression.Love);
        Assert.Equal(a, FaceState.Lerp(a, b, 0f));
        Assert.Equal(b, FaceState.Lerp(a, b, 1f));
        Assert.Equal(0.4f, FaceState.Lerp(a, b, 0.5f).Smile, 4);
    }

    [Fact]
    public void Every_emote_shows_something_and_none_shows_nothing()
    {
        Assert.Equal(FaceExpression.Neutral, FaceExpressions.ForEmote(-1));
        // the catalog has 30 emotes (HumanMeshBuilder.EmoteTable); more fall to the dance cycle
        for (int i = 0; i < 40; i++) Assert.NotEqual(FaceExpression.Neutral, FaceExpressions.ForEmote(i));
    }
}
