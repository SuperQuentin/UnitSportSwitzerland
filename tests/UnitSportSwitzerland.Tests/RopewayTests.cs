using System;
using System.Linq;
using UnitSport.World.Ropeways;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Ski lifts (#912): the per-tile pieces joined into one line bottom first, the carriers looping up
/// and down evenly spaced from the clock alone, and the next one to board.
/// </summary>
public class RopewayTests
{
    // a chairlift climbing north 600 m over 1.2 km, cut by tile seams at N 1000 and N 2000;
    // the middle piece was digitised top first
    private static RopewayJoin.Piece P(RopewayKind k, params double[] p) => new(k, p);
    private static readonly RopewayJoin.Piece[] Pieces =
    [
        P(RopewayKind.Chair, 500, 800, 1500, 500, 1000, 1600),
        P(RopewayKind.Chair, 500, 2000, 2100, 500, 1500, 1850, 500, 1000, 1600),
        P(RopewayKind.Chair, 500, 2000, 2100, 500.2, 2000.3, 2100, 500, 2000.1, 2100),  // a scrap at the seam
        P(RopewayKind.TBar, 3000, 3000, 1800, 3000, 3400, 1950),
    ];

    [Fact]
    public void Pieces_across_tile_seams_join_into_one_line_bottom_first()
    {
        var lines = RopewayJoin.Join(Pieces);
        var chair = lines.Single(l => l.Kind == RopewayKind.Chair);
        Assert.Equal(1500f, chair.Alt[0]);
        Assert.Equal(2100f, chair.Alt[^1]);
        Assert.InRange(chair.Length, 1199f, 1202f);   // the seam scrap may ride along: a metre at most
        Assert.Equal(500L * 10_000_000L + 800L, chair.Key);
        var tbar = lines.Single(l => l.Kind == RopewayKind.TBar);
        Assert.Equal(400f, tbar.Length, 1);
    }

    [Fact]
    public void A_line_drawn_top_first_still_starts_at_the_bottom_station()
    {
        var line = new RopewayLine(RopewayKind.Gondola, [0, 0], [1000, 0], [2500, 1200]);
        Assert.Equal(1200f, line.Alt[0]);
        Assert.Equal(0, line.N[0]);
        var (_, n, alt, de, dn) = line.At(250);
        Assert.Equal(250, n, 3);
        Assert.Equal(1525f, alt, 1);
        Assert.Equal(0f, de, 4);
        Assert.Equal(1f, dn, 4);
    }

    [Fact]
    public void Carriers_loop_evenly_spaced_and_every_peer_puts_them_in_the_same_place()
    {
        var line = RopewayJoin.Join(Pieces).Single(l => l.Kind == RopewayKind.Chair);
        int n = RopewayMotion.Count(line);
        Assert.InRange(n, 100, 120);   // 2.4 km of loop, a chair every ~22 m
        var at = Enumerable.Range(0, n).Select(i => RopewayMotion.Loop(line, i, 1234.5)).OrderBy(s => s).ToArray();
        for (int i = 1; i < n; i++) Assert.Equal(2 * line.Length / n, at[i] - at[i - 1], 2);
        Assert.Equal(RopewayMotion.Loop(line, 7, 99.25), RopewayMotion.Loop(line, 7, 99.25));
        // 4.5 m/s
        float a = RopewayMotion.Loop(line, 3, 10), b = RopewayMotion.Loop(line, 3, 12);
        Assert.Equal(9f, (b - a + 2 * line.Length) % (2 * line.Length), 2);
    }

    [Fact]
    public void Up_the_line_then_back_down_it_beside_the_way_up()
    {
        var line = new RopewayLine(RopewayKind.Chair, [0, 0], [0, 1000], [1000, 1400]);
        var up = RopewayMotion.Place(line, 100);
        Assert.True(up.Up);
        Assert.Equal(100, up.N, 3);
        Assert.Equal(1f, up.DirN, 4);
        var down = RopewayMotion.Place(line, 1900);   // 100 m from the bottom, on the way down
        Assert.False(down.Up);
        Assert.Equal(100, down.N, 3);
        Assert.Equal(-1f, down.DirN, 4);
        Assert.True(Math.Abs(down.E - up.E) > 2, "the down side runs beside the up side");
    }

    [Fact]
    public void The_next_carrier_comes_within_one_spacing()
    {
        var line = new RopewayLine(RopewayKind.TBar, [0, 0], [0, 600], [1000, 1150]);
        var (speed, spacing, _, _) = RopewayMotion.Of(RopewayKind.TBar);
        for (double t = 0; t < 30; t += 0.7)
        {
            var (carrier, wait) = RopewayMotion.Next(line, t);
            Assert.InRange(wait, 0, spacing / speed + 0.01);
            // and that carrier is at the bottom when the wait is over
            float s = RopewayMotion.Loop(line, carrier, t + wait);
            Assert.True(s < 0.05f || s > 2 * line.Length - 0.05f, $"carrier {carrier} at {s}");
        }
    }
}
