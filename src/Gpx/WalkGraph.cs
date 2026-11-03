namespace UnitSport.Gpx;

/// <summary>
/// The road network as a graph of its vertices (<see cref="RoadNetwork"/>'s walking graph), read only:
/// what a route that turns at junctions needs, and what a unit test can fake with a few points.
/// </summary>
public interface IWalkGraph
{
    /// <summary>A node's place in LV95 and the road surface's height there.</summary>
    (double E, double N, double H) Pos(int node);

    /// <summary>The nodes one step along the roads from <paramref name="node"/>.</summary>
    IReadOnlyList<(int To, double Cost)> Next(int node);

    /// <summary>The nodes within <paramref name="radius"/> m of a point.</summary>
    List<int> NodesNear(double e, double n, double radius);
}
