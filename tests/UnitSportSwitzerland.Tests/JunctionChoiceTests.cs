using UnitSport.Gpx;
using UnitSport.Trainer;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Junctions on a training's way and the roads out of them (#901, src/Trainer/JunctionChoice.cs, linked in).</summary>
public class JunctionChoiceTests
{
    /// <summary>A graph of straight roads with a vertex every 5 m; ends that meet are one node.</summary>
    private sealed class Grid : IWalkGraph
    {
        private readonly List<(double E, double N)> _pos = new();
        private readonly List<List<(int, double)>> _next = new();

        public Grid Road(double e0, double n0, double e1, double n1)
        {
            double len = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
            int steps = (int)Math.Round(len / 5);
            int prev = -1;
            for (int i = 0; i <= steps; i++)
            {
                int id = Node(e0 + (e1 - e0) * i / steps, n0 + (n1 - n0) * i / steps);
                if (prev >= 0) { _next[prev].Add((id, len / steps)); _next[id].Add((prev, len / steps)); }
                prev = id;
            }
            return this;
        }

        public int Node(double e, double n)
        {
            int at = _pos.FindIndex(p => Math.Abs(p.E - e) < 0.01 && Math.Abs(p.N - n) < 0.01);
            if (at >= 0) return at;
            _pos.Add((e, n));
            _next.Add(new());
            return _pos.Count - 1;
        }

        public (double E, double N, double H) Pos(int node) => (_pos[node].E, _pos[node].N, 500);
        public IReadOnlyList<(int To, double Cost)> Next(int node) => _next[node];
        public List<int> NodesNear(double e, double n, double radius) =>
            Enumerable.Range(0, _pos.Count).Where(i => (_pos[i].E - e) * (_pos[i].E - e) + (_pos[i].N - n) * (_pos[i].N - n) <= radius * radius).ToList();
    }

    /// <summary>A road east from 0 to 1000; at 300 a road north (a T), at 600 roads north and south (a crossroads).</summary>
    private static Grid Town() => new Grid()
        .Road(0, 0, 300, 0).Road(300, 0, 600, 0).Road(600, 0, 1000, 0)
        .Road(300, 0, 300, 400)
        .Road(600, 0, 600, 300).Road(600, 0, 600, -300);

    /// <summary>A course along the main road, as a snapped GPX is: points every 8 m, between the graph's vertices.</summary>
    private static List<WayPoint> Course(double from, double to)
    {
        var way = new List<WayPoint>();
        for (double e = from; e <= to; e += 8) way.Add(new WayPoint(e + 1.3, 0.4, 500, -1, way.Count));
        return way;
    }

    [Fact]
    public void Finds_the_next_junction_and_its_roads()
    {
        var g = Town();
        var way = Course(100, 900);
        var arc = JunctionChoice.Arcs(way);
        var j = JunctionChoice.Next(g, way, arc, 0, 0, 400);
        Assert.NotNull(j);
        Assert.Equal(g.Node(300, 0), j!.Node);
        Assert.InRange(j.Arc, 195, 205);
        // north is a left turn, east is straight on and taken; the road it came by is not a branch
        Assert.Equal(2, j.Branches.Count);
        Assert.InRange(j.Branches[0].Bearing, -90 - 3, -90 + 3);
        Assert.False(j.Branches[0].Taken);
        Assert.InRange(j.Branches[1].Bearing, 0 - 3, 0 + 3);
        Assert.True(j.Branches[1].Taken);
    }

    [Fact]
    public void A_junction_behind_the_rider_is_passed()
    {
        var g = Town();
        var way = Course(100, 900);
        var arc = JunctionChoice.Arcs(way);
        var j = JunctionChoice.Next(g, way, arc, 26, arc[26], 400);
        Assert.Equal(g.Node(600, 0), j!.Node);
        Assert.Equal(3, j.Branches.Count);
        Assert.Null(JunctionChoice.Next(g, way, arc, 26, arc[26], 50));
    }

    [Fact]
    public void Left_and_right_step_from_the_road_taken()
    {
        var g = Town();
        var way = Course(100, 900);
        var arc = JunctionChoice.Arcs(way);
        var t = JunctionChoice.Next(g, way, arc, 0, 0, 400)!;
        Assert.Equal(0, JunctionChoice.Pick(t, -1));
        Assert.Equal(-1, JunctionChoice.Pick(t, +1));   // no road right at the T
        var x = JunctionChoice.Next(g, way, arc, 26, arc[26], 400)!;
        int left = JunctionChoice.Pick(x, -1), right = JunctionChoice.Pick(x, +1);
        Assert.InRange(x.Branches[left].Bearing, -90 - 3, -90 + 3);
        Assert.InRange(x.Branches[right].Bearing, 90 - 3, 90 + 3);
    }

    [Fact]
    public void Follow_goes_straight_and_stops_at_a_dead_end()
    {
        var g = Town();
        // north at the T: 400 m to the road's end, asked for more
        var path = JunctionChoice.Follow(g, g.Node(300, 0), g.Node(300, 5), 1000);
        Assert.Equal(g.Node(300, 400), path[^1]);
        // east from the T across the crossroads: straight on, not north or south
        path = JunctionChoice.Follow(g, g.Node(295, 0), g.Node(300, 0), 450);
        Assert.Contains(g.Node(700, 0), path);
        Assert.DoesNotContain(g.Node(600, 5), path);
    }

    [Fact]
    public void Route_never_turns_straight_back_and_ends_where_the_goal_says()
    {
        var g = Town();
        // from the T's north road, 100 m up and facing south: back to the main road and east to 800
        int start = g.Node(300, 100);
        var route = JunctionChoice.RouteTo(g, g.Node(300, 105), start, (_, n) => n == g.Node(800, 0), 5000);
        Assert.NotNull(route);
        Assert.Equal(start, route![0]);
        Assert.Equal(g.Node(800, 0), route[^1]);
        Assert.Contains(g.Node(300, 0), route);
        // facing north instead, the only way is the dead end: no route without turning round
        Assert.Null(JunctionChoice.RouteTo(g, g.Node(300, 95), start, (_, n) => n == g.Node(800, 0), 5000));
    }

    [Fact]
    public void Bearing_is_negative_to_the_left()
    {
        Assert.Equal(-90, JunctionChoice.Bearing((1, 0), (0, 1)), 6);
        Assert.Equal(90, JunctionChoice.Bearing((1, 0), (0, -1)), 6);
        Assert.Equal(0, JunctionChoice.Bearing((0, 1), (0, 5)), 6);
    }
}
