using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The dance move picker (src/Avatar/DancePick.cs, linked in): sections, crowd moves and break sets (#728).</summary>
public class DancePickTests
{
    private const int HipHop = 3, Rock = 1;
    // a style table of six moves: two calm, two middle, two big
    private static readonly byte[] Energy = { 0, 0, 1, 1, 2, 2 };

    private static readonly DanceSlotMusic Peak = new(2, 4, true);
    private static readonly DanceSlotMusic Calm = new(0, 1, true);
    private static readonly DanceSlotMusic Groove = new(1, 2, true);

    private static (DanceSlotKind Kind, int Index) At(int slot, int style, uint seed, bool crowd, DanceSlotMusic m) =>
        DancePick.Pick(slot, style, seed, crowd, m, m, m, Energy);

    [Fact]
    public void Calm_parts_never_pick_a_big_move_and_peaks_never_a_calm_one()
    {
        for (int slot = 0; slot < 400; slot++)
        {
            var calm = At(slot, Rock, 7u, false, Calm);
            Assert.Equal(DanceSlotKind.Table, calm.Kind);
            Assert.True(Energy[calm.Index] <= 1, $"slot {slot}: {calm.Index}");
            var peak = At(slot, Rock, 7u, false, Peak);
            Assert.True(Energy[peak.Index] >= 1, $"slot {slot}: {peak.Index}");
        }
    }

    [Fact]
    public void A_groove_uses_the_whole_table()
    {
        var seen = new HashSet<int>();
        for (int slot = 0; slot < 400; slot++) seen.Add(At(slot, Rock, 3u, false, Groove).Index);
        Assert.Equal(Energy.Length, seen.Count);
    }

    [Fact]
    public void Every_break_set_is_a_down_slot_then_a_power_slot()
    {
        int sets = 0;
        for (uint seed = 0; seed < 50; seed++)
        {
            var prevKind = At(-1, HipHop, seed, false, Peak).Kind;
            for (int slot = 0; slot < 200; slot++)
            {
                var kind = At(slot, HipHop, seed, false, Peak).Kind;
                bool power = kind is DanceSlotKind.BreakWindmill or DanceSlotKind.BreakHeadspin;
                Assert.Equal(prevKind == DanceSlotKind.BreakDown, power);
                if (kind == DanceSlotKind.BreakDown) sets++;
                prevKind = kind;
            }
        }
        // rare but there: about one eligible slot in BreakOdds starts one
        Assert.InRange(sets, 50 * 200 / 12, 50 * 200 / 4);
    }

    [Fact]
    public void No_break_in_a_calm_part_a_section_too_short_or_a_style_that_does_not_break()
    {
        var shortPeak = new DanceSlotMusic(2, 4, false);
        for (uint seed = 0; seed < 30; seed++)
            for (int slot = 0; slot < 200; slot++)
            {
                Assert.True(At(slot, HipHop, seed, false, Calm).Kind is DanceSlotKind.Table);
                Assert.True(At(slot, HipHop, seed, false, shortPeak).Kind is DanceSlotKind.Table);
                Assert.True(At(slot, Rock, seed, false, Peak).Kind is DanceSlotKind.Table);
            }
    }

    [Fact]
    public void Crowd_moves_are_the_same_for_every_dancer_and_never_cut_a_break_set()
    {
        for (int slot = 0; slot < 300; slot++)
        {
            var a = At(slot, HipHop, 11u, true, Peak);
            var b = At(slot, HipHop, 999u, true, Peak);
            bool crowdA = a.Kind is DanceSlotKind.CrowdJump or DanceSlotKind.CrowdPogo;
            bool crowdB = b.Kind is DanceSlotKind.CrowdJump or DanceSlotKind.CrowdPogo;
            // a crowd slot is everyone's, unless one of them is in the second half of a set
            if (crowdA && b.Kind is not (DanceSlotKind.BreakWindmill or DanceSlotKind.BreakHeadspin)) Assert.Equal(a.Kind, b.Kind);
            if (crowdB && a.Kind is not (DanceSlotKind.BreakWindmill or DanceSlotKind.BreakHeadspin)) Assert.Equal(a.Kind, b.Kind);
        }
    }

    [Fact]
    public void A_new_section_is_a_new_pick()
    {
        int differ = 0;
        for (int slot = 0; slot < 100; slot++)
            if (At(slot, Rock, 5u, false, new DanceSlotMusic(1, 0, true)).Index != At(slot, Rock, 5u, false, new DanceSlotMusic(1, 1, true)).Index)
                differ++;
        Assert.True(differ > 50, $"{differ} of 100 slots changed");
    }
    // a generated pool of nine moves: three of each energy class
    private static readonly byte[] GenEnergy = { 0, 0, 0, 1, 1, 1, 2, 2, 2 };

    private static (DanceSlotKind Kind, int Index) Gen(int slot, uint seed, DanceSlotMusic m, bool crowd = false) =>
        DancePick.Pick(slot, Rock, seed, crowd, m, m, m, Energy, GenEnergy);

    [Fact]
    public void A_phrase_is_A_A_B_A_over_four_slots()
    {
        for (uint seed = 1; seed < 40; seed++)
            for (int phrase = 0; phrase < 25; phrase++)
            {
                int s0 = phrase * 4;
                var a = Gen(s0, seed, Groove);
                Assert.Equal(a, Gen(s0 + 1, seed, Groove));
                Assert.Equal(a, Gen(s0 + 3, seed, Groove));
            }
        // and B is a move of its own often enough to hear the form
        int differs = 0;
        for (int phrase = 0; phrase < 200; phrase++)
            if (Gen(phrase * 4 + 2, 5u, Groove) != Gen(phrase * 4, 5u, Groove)) differs++;
        Assert.InRange(differs, 100, 200);
    }

    [Fact]
    public void About_a_quarter_of_the_slots_are_generated_within_the_sections_limits()
    {
        int gen = 0, total = 0;
        for (uint seed = 1; seed < 30; seed++)
            for (int slot = 0; slot < 400; slot++)
                foreach (var m in new[] { Calm, Groove, Peak })
                {
                    var (kind, index) = Gen(slot, seed, m);
                    total++;
                    if (kind != DanceSlotKind.Generated) continue;
                    gen++;
                    Assert.InRange(index, 0, GenEnergy.Length - 1);
                    if (m.SectionKind == 0) Assert.True(GenEnergy[index] <= 1);
                    if (m.SectionKind == 2) Assert.True(GenEnergy[index] >= 1);
                }
        Assert.InRange(gen / (double)total, 0.20, 0.30);
    }

    [Fact]
    public void No_generated_move_without_a_pool_and_crowd_and_break_slots_stay_first()
    {
        for (int slot = 0; slot < 300; slot++)
            Assert.NotEqual(DanceSlotKind.Generated, At(slot, Rock, 9u, false, Groove).Kind);
        for (int slot = 0; slot < 300; slot++)
        {
            var a = DancePick.Pick(slot, HipHop, 11u, true, Peak, Peak, Peak, Energy, GenEnergy);
            var b = DancePick.Pick(slot, HipHop, 999u, true, Peak, Peak, Peak, Energy, GenEnergy);
            if (a.Kind is DanceSlotKind.CrowdJump or DanceSlotKind.CrowdPogo && b.Kind is DanceSlotKind.CrowdJump or DanceSlotKind.CrowdPogo)
                Assert.Equal(a.Kind, b.Kind);
        }
        for (uint seed = 0; seed < 30; seed++)
        {
            var prevKind = DancePick.Pick(-1, HipHop, seed, false, Peak, Peak, Peak, Energy, GenEnergy).Kind;
            for (int slot = 0; slot < 200; slot++)
            {
                var kind = DancePick.Pick(slot, HipHop, seed, false, Peak, Peak, Peak, Energy, GenEnergy).Kind;
                bool power = kind is DanceSlotKind.BreakWindmill or DanceSlotKind.BreakHeadspin;
                Assert.Equal(prevKind == DanceSlotKind.BreakDown, power);
                prevKind = kind;
            }
        }
    }
}
