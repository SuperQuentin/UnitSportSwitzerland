using System;
using System.Linq;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The water pass's window maths in metres (#1507, G2 B3 PR 6): a count of cells is the LV95 window's metres as they
/// are and a leaf lattice's rounded from its cell size, distances scale with it, and the bank refine keeps the lowest
/// wet surface round a vertex.
/// </summary>
public class WaterWindowTests
{
    [Fact]
    public void A_length_in_cells_is_the_metres_on_the_lv95_grid_and_rounded_on_a_leaf_lattice()
    {
        Assert.Equal(40, WaterWindow.Cells(WaterWindow.EdgeSmoothM, 1));
        Assert.Equal(600, WaterWindow.Cells(WaterWindow.Halo, 1));
        Assert.Equal(34, WaterWindow.Cells(WaterWindow.EdgeSmoothM, 1.194));
        Assert.Equal(503, WaterWindow.Cells(WaterWindow.Halo, 1.194));
        Assert.Equal(1, WaterWindow.Cells(0.2, 1.194));
    }

    [Fact]
    public void Distances_are_in_metres_whatever_the_cell()
    {
        const int w = 21;
        var site = new bool[w * w];
        site[10 * w + 10] = true;
        var one = WaterWindow.Distances(site, w, 1);
        var leaf = WaterWindow.Distances(site, w, 1.25);
        Assert.Equal(5, one[10 * w + 15], 5);
        Assert.Equal(5 * 1.25, leaf[10 * w + 15], 5);
        Assert.Equal(Math.Sqrt(18) * 1.25, leaf[13 * w + 13], 4);
    }

    [Fact]
    public void A_wet_cell_standing_above_the_water_round_it_is_bank()
    {
        const int w = 9;
        var state = Enumerable.Repeat((byte)1, w * w).ToArray();
        var q = Enumerable.Repeat(1000, w * w).ToArray();
        q[4 * w + 4] = 1060;   // 0.6 m up in centimetres
        q[4 * w + 5] = 1040;   // 0.4 m: still water
        WaterWindow.RefineWet(state, q, w, WaterWindow.Cells(WaterWindow.BankProbeM, 1.2), 50);
        Assert.Equal(0, state[4 * w + 4]);
        Assert.Equal(1, state[4 * w + 5]);
        Assert.Equal(w * w - 1, state.Count(s => s == 1));
    }
}
