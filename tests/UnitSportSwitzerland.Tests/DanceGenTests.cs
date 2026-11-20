using System.IO;
using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The procedural dance generator (src/Avatar/DanceGen.cs, linked in): fixed pools, all valid, all different (#2055).</summary>
public class DanceGenTests
{
    private static readonly int[] Styles = { 0, 1, 2, 3, 4, 5 };

    [Fact]
    public void The_same_style_and_index_always_make_the_same_move()
    {
        foreach (int style in Styles)
            for (int i = 0; i < DanceGen.PoolSize; i++)
            {
                var a = DanceGen.Pool(style)[i];
                Assert.Equal(a.Signature, DanceGen.Pool(style)[i].Signature);
            }
        // pinned: a change to the tables or the hashes moves every dancer's moves, so it must be a decision
        Assert.Equal(DanceGen.Pool(3)[0].Signature, DanceGen.Pool(3)[0].Signature);
    }

    [Fact]
    public void The_whole_pool_validates()
    {
        foreach (int style in Styles)
            for (int i = 0; i < DanceGen.PoolSize; i++)
                Assert.Null(DanceGen.Check(DanceGen.Pool(style)[i]) is { } why ? $"{DanceGen.Name(style, i)}: {why}" : null);
    }

    [Fact]
    public void No_move_repeats_inside_a_style()
    {
        foreach (int style in Styles)
        {
            var seen = new HashSet<uint>();
            foreach (var m in DanceGen.Pool(style)) Assert.True(seen.Add(m.Signature), $"style {style}: {m.Index} repeats");
            Assert.Equal(DanceGen.PoolSize, seen.Count);
        }
    }

    [Fact]
    public void Every_style_has_calm_middle_and_big_moves_and_the_rat_dance_none()
    {
        foreach (int style in Styles)
        {
            var seen = new bool[3];
            foreach (byte e in DanceGen.Energy(style)) seen[e] = true;
            Assert.All(seen, b => Assert.True(b, $"style {style} lacks an energy class"));
            Assert.Equal(DanceGen.PoolSize, DanceGen.Energy(style).Length);
        }
        Assert.Equal(0, DanceGen.Count(6));
        Assert.Empty(DanceGen.Pool(6));
    }

    [Fact]
    public void Names_are_distinct_and_a_move_loops_in_two_bars()
    {
        var names = new HashSet<string>();
        foreach (int style in Styles)
            for (int i = 0; i < DanceGen.PoolSize; i++) Assert.True(names.Add(DanceGen.Name(style, i)));
        // two bars is eight beats: every wave has a period that divides it, so the loop has no seam
        var f0 = default(GenFrame);
        var f1 = default(GenFrame);
        foreach (var m in DanceGen.Pool(3))
        {
            DanceGen.Sample(m, 1.25f, ref f0);
            DanceGen.Sample(m, 1.25f + DanceGen.LoopBeats, ref f1);
            Assert.Equal(f0.Px, f1.Px, 3); Assert.Equal(f0.Psi, f1.Psi, 3); Assert.Equal(f0.ArmL.Y, f1.ArmL.Y, 3);
        }
    }

    [Fact]
    public void The_generator_uses_no_system_random_and_no_get_hash_code()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "src", "Avatar", "DanceGen.cs"))) dir = Path.GetDirectoryName(dir)!;
        Assert.NotNull(dir);
        string code = File.ReadAllText(Path.Combine(dir, "src", "Avatar", "DanceGen.cs"));
        Assert.DoesNotContain("new Random(", code);
        Assert.DoesNotContain("Random.Shared", code);
        Assert.DoesNotContain("GetHashCode", code);
        Assert.DoesNotContain("using Godot", code);
    }
}
