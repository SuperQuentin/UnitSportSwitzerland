using UnitSport.Avatar;
using UnitSport.Items;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Forklift pallets (#583 phase 2): the load byte that is a moved pallet's whole identity, the ids
/// the server keeps them under, and the fork rule — when the tines are under one, when raising
/// them lifts it, when lowering them sets it down. The rule is pure arithmetic against
/// <see cref="ForkliftLayout"/>, so it is pinned here and only exercised in the Godot checks.
/// </summary>
public class PalletTests
{
    /// <summary>The hull's front face, authored: where a pallet driven at stops.</summary>
    private const float HullFace = ForkliftLayout.MastZ + 0.1f;

    [Fact]
    public void A_load_byte_keeps_the_goods_and_the_deck()
    {
        for (int i = 0; i < 1000; i++)
        {
            float roll = i / 1000f;
            foreach (float depth in new[] { Pallets.NarrowDepth, Pallets.SquareDepth })
            {
                byte load = Pallets.LoadOf(roll, depth);
                Assert.Equal(depth, Pallets.Depth(load));
                Assert.True(System.Math.Abs(Pallets.Roll(load) - roll) <= 0.5f / 128f + 1e-6f, $"roll {roll} -> {Pallets.Roll(load)}");
            }
        }
        // the extremes stay in range rather than wrapping into the square bit
        Assert.Equal(127, Pallets.LoadOf(0.99999f, Pallets.NarrowDepth));
        Assert.Equal(0, Pallets.LoadOf(-1f, Pallets.NarrowDepth));
    }

    [Fact]
    public void A_pallet_of_drums_stays_a_pallet_of_drums()
    {
        // away from the thresholds the quantised roll draws the same goods as the hall did
        foreach (var (roll, goods) in new[] { (0.1f, PalletGoods.Cartons), (0.6f, PalletGoods.Drums), (0.8f, PalletGoods.Sacks), (0.95f, PalletGoods.Wrapped) })
        {
            Assert.Equal(goods, Pallets.GoodsOf(roll));
            Assert.Equal(goods, Pallets.Goods(Pallets.LoadOf(roll, Pallets.NarrowDepth)));
            Assert.Equal(goods, Pallets.Goods(Pallets.LoadOf(roll, Pallets.SquareDepth)));
        }
    }

    [Fact]
    public void What_the_forks_carry_round_trips_every_load_either_way_round()
    {
        Assert.Null(Pallets.LoadCarried(0));
        Assert.False(Pallets.CarriedAcross(0));
        for (int load = 0; load <= 255; load++)
            foreach (bool across in new[] { false, true })
            {
                int carried = Pallets.Carried((byte)load, across);
                Assert.InRange(carried, 1, 512);
                Assert.Equal((byte)load, Pallets.LoadCarried(carried));
                Assert.Equal(across, Pallets.CarriedAcross(carried));
                // ten bits: what the parked flags keep above the ten bits of fork height
                Assert.True(carried < 1 << 10);
            }
        Assert.Null(Pallets.LoadCarried(513));
        Assert.Null(Pallets.LoadCarried(-1));
    }

    [Fact]
    public void Ids_read_back()
    {
        Assert.True(Pallets.TryParse(Pallets.HallId("2585_1114_52", 37), out var hall));
        Assert.Equal(new PalletRef(PalletSource.Hall, "2585_1114_52", 37, 0), hall);
        Assert.True(Pallets.TryParse(Pallets.YardId("2585_1114_52", 3), out var yard));
        Assert.Equal(new PalletRef(PalletSource.Yard, "2585_1114_52", 3, 0), yard);
        Assert.True(Pallets.TryParse(Pallets.LooseId(12), out var loose));
        Assert.Equal(new PalletRef(PalletSource.Loose, "", -1, 12), loose);

        foreach (var bad in new[] { null, "", "#", "#0", "#-3", "#1a", "2585_1114_52", ":f3", "2585_1114_52:f", "2585_1114_52:q3", "2585_1114_52:f-1", "2585_1114_52:f3x" })
            Assert.False(Pallets.TryParse(bad, out _), bad ?? "null");
    }

    [Fact]
    public void A_pallet_driven_at_square_is_on_the_forks()
    {
        foreach (float depth in new[] { Pallets.NarrowDepth, Pallets.SquareDepth, Pallets.Length })
            Assert.True(Pallets.Forked(0f, HullFace + depth * 0.5f, 1f, ForkliftLayout.MinLift, 0.5f), $"{depth} m deep");
        // a little off centre, a little skewed: a driver is not a robot
        Assert.True(Pallets.Forked(0.25f, HullFace + 0.6f, 0.9f, ForkliftLayout.MinLift, 1f));
        // and going either way along its runners
        Assert.True(Pallets.Forked(0f, HullFace + 0.6f, 1f, 0.2f, 0f));
        // four-way: square across its runners too, the way a pallet against a wall is reached
        Assert.True(Pallets.Forked(0f, HullFace + 0.5f, 0f, ForkliftLayout.MinLift, 0f));
        Assert.True(Pallets.Forked(0f, HullFace + 0.5f, 0.45f, ForkliftLayout.MinLift, 0f));
        Assert.Equal(false, Pallets.Across(0.95f));
        Assert.Equal(true, Pallets.Across(0.1f));
    }

    [Fact]
    public void A_pallet_beside_above_beyond_or_at_a_corner_is_not()
    {
        float z = HullFace + 0.6f;
        Assert.False(Pallets.Forked(ForkliftLayout.ForkHalfSpan + 0.05f, z, 1f, ForkliftLayout.MinLift, 0f), "beside");
        Assert.False(Pallets.Forked(0f, z, 1f, ForkliftLayout.ForkEntry + 0.01f, 0f), "forks over the deck");
        Assert.False(Pallets.Forked(0f, ForkliftLayout.MastZ + ForkliftLayout.TineLength + 0.2f, 1f, ForkliftLayout.MinLift, 0f), "beyond the tips");
        Assert.False(Pallets.Forked(0f, ForkliftLayout.MastZ - 0.2f, 1f, ForkliftLayout.MinLift, 0f), "behind the mast");
        // 45°: the tines meet a corner block, between its two open sides
        Assert.False(Pallets.Forked(0f, z, 0.707f, ForkliftLayout.MinLift, 0f), "at a corner");
        Assert.Null(Pallets.Across(0.707f));
        Assert.False(Pallets.Forked(0f, z, 1f, ForkliftLayout.MinLift, Pallets.MaxForkSpeed + 0.1f), "driving past");
    }

    [Fact]
    public void Lift_and_set_down_do_not_chatter()
    {
        // the forks start under the deck, lift it above the set-down height, and only go in low
        Assert.True(ForkliftLayout.MinLift < ForkliftLayout.SetDown);
        Assert.True(ForkliftLayout.SetDown < Pallets.Seat, "a pallet just set down would be picked straight back up");
        Assert.True(Pallets.Seat < ForkliftLayout.ForkEntry, "forks low enough to go in must be able to lift");
        Assert.False(Pallets.Lifts(ForkliftLayout.MinLift));
        Assert.True(Pallets.Lifts(Pallets.Seat));
        Assert.True(Pallets.SetsDown(ForkliftLayout.MinLift));
        Assert.False(Pallets.SetsDown(Pallets.Seat));
    }

    [Fact]
    public void A_pallet_set_down_can_be_forked_again_where_it_landed()
    {
        // it lands where it rode, so backing off and driving straight back in picks it up
        var at = ForkliftLayout.LoadCentre;
        Assert.True(Pallets.Forked(at.X, at.Z, 1f, ForkliftLayout.MinLift, 0f));
        // and clear of the hull: the machine is not standing in the pallet it just put down
        Assert.True(at.Z - Pallets.Length * 0.5f >= HullFace);
    }
}
