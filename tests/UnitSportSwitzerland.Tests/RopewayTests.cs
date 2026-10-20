using System;
using System.Linq;
using UnitSport.Terrain.Format;
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
        float a = RopewayMotion.Loop(line, 3, 10), b = RopewayMotion.Loop(line, 3, 12);
        Assert.Equal(2 * line.Speed, (b - a + 2 * line.Length) % (2 * line.Length), 2);
    }

    [Fact]
    public void Up_the_line_then_back_down_it_beside_the_way_up()
    {
        var line = new RopewayLine(RopewayKind.Chair, [0, 0], [0, 1000], [1000, 1400]);
        var up = RopewayMotion.Place(line, 0, 100 / line.Speed);
        Assert.True(up.Up);
        Assert.Equal(100, up.N, 2);
        Assert.Equal(1f, up.DirN, 4);
        var down = RopewayMotion.Place(line, 0, 1900 / line.Speed);   // 100 m from the bottom, on the way down
        Assert.False(down.Up);
        Assert.Equal(100, down.N, 2);
        Assert.Equal(-1f, down.DirN, 4);
        Assert.True(Math.Abs(down.E - up.E) > 2, "the down side runs beside the up side");
    }

    [Fact]
    public void The_next_carrier_comes_within_one_spacing()
    {
        var line = new RopewayLine(RopewayKind.TBar, [0, 0], [0, 600], [1000, 1150]);
        for (double t = 0; t < 30; t += 0.7)
        {
            var (carrier, wait) = RopewayMotion.Next(line, t);
            Assert.InRange(wait, 0, line.Spacing / line.Speed + 0.01);
            var (s, _, _) = RopewayMotion.Carrier(line, carrier, t + wait + 0.01);
            Assert.True(s < 0.2f, $"carrier {carrier} at {s}");
        }
    }

    [Fact]
    public void A_gondola_drawn_as_its_two_cables_is_one_lift_with_its_return_side()
    {
        var up = new RopewayJoin.Piece(RopewayKind.Gondola, [0, 0, 1000, 0, 1500, 1600]);
        var down = new RopewayJoin.Piece(RopewayKind.Gondola, [14, 0, 1000, 14, 1500, 1600]);
        var other = new RopewayJoin.Piece(RopewayKind.Gondola, [500, 0, 1000, 500, 900, 1400]);
        var lines = RopewayJoin.Join([up, down, other]);
        Assert.Equal(2, lines.Count);
        Assert.Equal(14f, lines.Single(l => l.E[0] < 100).Side, 1);
    }

    [Fact]
    public void Two_lifts_side_by_side_that_osm_names_apart_stay_two()
    {
        var info = new RopewayInfoIndex
        {
            Ropeways =
            [
                new RopewayInfo(0, 0, 0, 1000, "gondola", 6, 4, null, "Médran 2"),
                new RopewayInfo(14, 0, 14, 1000, "gondola", 6, 10, null, "Médran 4"),
            ],
        };
        var lines = RopewayJoin.Join(
        [
            new RopewayJoin.Piece(RopewayKind.Gondola, [0, 0, 1000, 0, 1000, 1500]),
            new RopewayJoin.Piece(RopewayKind.Gondola, [14, 0, 1000, 14, 1000, 1500]),
        ], info: info);
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Name == "Médran 2");
        Assert.Contains(lines, l => l.Name == "Médran 4");
    }

    [Fact]
    public void Osm_gives_the_type_the_speed_the_seats_and_the_name()
    {
        var info = new RopewayInfoIndex
        {
            Ropeways =
            [
                new RopewayInfo(0, 1500, 0, 0, "cable_car", 5, 80, null, "Médran"),
                new RopewayInfo(1000, 0, 1000, 1200, "chair_lift", 0, 2, false, "Les Moulins"),
            ],
        };
        var lines = RopewayJoin.Join(
        [
            new RopewayJoin.Piece(RopewayKind.Gondola, [0, 0, 1000, 0, 1500, 1600]),
            new RopewayJoin.Piece(RopewayKind.Chair, [1000, 0, 1000, 1000, 1200, 1300]),
        ], info: info);
        var car = lines.Single(l => l.Name == "Médran");
        Assert.Equal(RopewayKind.CableCar, car.Kind);
        Assert.Equal(1500f / 300f, car.Speed, 3);        // 1.5 km in 5 minutes
        Assert.Equal(80, car.Seats);
        var chair = lines.Single(l => l.Name == "Les Moulins");
        Assert.Equal(2.3f, chair.Speed);                  // fixed grip
        Assert.Equal(2, chair.Seats);
    }

    [Fact]
    public void A_cable_cars_two_cabins_pass_each_other_and_wait_at_the_stations()
    {
        var info = new RopewayInfoIndex { Ropeways = [new RopewayInfo(0, 0, 0, 800, "cable_car", 2, 60, null, "")] };
        var line = RopewayJoin.Join([new RopewayJoin.Piece(RopewayKind.Gondola, [0, 0, 1000, 0, 800, 1500])], info: info).Single();
        Assert.True(line.Shuttle);
        Assert.Equal(2, RopewayMotion.Count(line));
        double travel = line.Length / line.Speed;
        for (double t = 0; t < 600; t += 3.3)
        {
            var a = RopewayMotion.Carrier(line, 0, t);
            var b = RopewayMotion.Carrier(line, 1, t);
            Assert.False(a.OnSide);
            Assert.True(b.OnSide);
            Assert.Equal(line.Length, a.S + b.S, 1);      // where one is, the other is mirrored
        }
        var (cabin, wait) = RopewayMotion.Next(line, 17);
        Assert.InRange(wait, 0, 2 * (travel + RopewayMotion.Dwell));
        var leaving = RopewayMotion.Carrier(line, cabin, 17 + wait + 0.5);
        Assert.True(leaving.Up && leaving.S < 10);
    }
}
