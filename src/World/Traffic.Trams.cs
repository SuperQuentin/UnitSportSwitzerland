using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// Trams (#1057): articulated trams on the street tracks, stopping at tram stops, obeying the tram bar signals, given way to by
/// turning cars. Seams of the shared base (#1060, <c>Traffic.Modes.cs</c>): this file is the mode's own. A tram is a
/// <see cref="Vehicle"/> like a train (box sections at fixed offsets, <see cref="AddTram"/>); the defaults below run one along the
/// tram graph like a train, and there is no tram graph yet (density 0 by default).
/// </summary>
public partial class Traffic
{
    /// <summary>Whether trams have landed: their density row shows in the settings (Settings → World, the solo screen).</summary>
    public static bool TramsReady => false;

    /// <summary>A tram section: 2.4 m wide, 3.4 m high, blue (its length per section, <see cref="AddTram"/>); 11 m/s; spawned 300-1500 m out, dropped past 2000 m, drawn to 1200 m.</summary>
    private static readonly MoverLook TramLook = new(new Vector3(2.4f, 3.4f, 9f), new Color(0.2f, 0.45f, 0.9f),
        Cruise: 11f, Spread: 0f, Near: 300f, SpawnMin: 300f, SpawnMax: 1500f, Despawn: 2000f, Drawn: 1200f);

    /// <summary>
    /// The tram graph (on a worker thread): <c>Tramway</c> track segments (left out of the rails, <see cref="IsRail"/>) and the roads
    /// carrying the overlay's tram flag, joined through junctions along the embedded rails, in <c>tiles.Origin</c>'s frame. E.g.
    /// <c>LaneGraph.Build(tiles.Wide, tiles.Origin, s =&gt; s.Class == RoadClass.Railway &amp;&amp; (s.Flags &amp; RoadFlags.Tramway) != 0,
    /// MoverKind.Tram)</c>; null builds none (no trams spawn).
    /// </summary>
    private static LaneGraph? BuildTramGraph(ModeTiles tiles) => null;

    /// <summary>One more tram somewhere on the tram graph (<see cref="_tramLines"/>, not null here), out of sight of the view.</summary>
    private void SpawnTram(Vector3 focus)
    {
        if (_tramLines!.RandomSpot(_rng, focus, TramLook.SpawnMin, TramLook.SpawnMax, _ => 1f) is not var (edge, arc)) return;
        AddTram(edge, _rng.Next(2) == 0, arc, [9f, 7f, 9f], 0.6f, TramLook.Cruise);
    }

    /// <summary>One tick of a tram (line speed, curves, stops, its signals): by default a train's run on the tram graph.</summary>
    private void StepTram(Vehicle tram, float dt)
    {
        var (_, dir) = tram.Route.At(0);
        var (_, aheadDir) = tram.Route.At(-20f);
        float turn = Mathf.Acos(Mathf.Clamp(dir.Dot(aheadDir), -1f, 1f));
        float target = tram.Cruise;
        if (turn > 0.05f) target = Mathf.Min(target, Mathf.Sqrt(1.0f * 20f / turn));
        tram.Speed = Mathf.MoveToward(tram.Speed, target, 1.0f * dt);
        var before = tram.Head;
        if (!tram.Route.Advance(tram.Speed * dt, _nextTramLeg ??= NextTramLeg)) tram.Stuck += 99f;
        tram.Route.Trim(tram.Offsets[^1] + 20f);
        tram.Place(_ => tram.Offset);
        tram.Vel = dt > 0f ? MathX.Flat(tram.Head - before) / dt : Vector3.Zero;
        for (int k = 0; k < PathSteps; k++) tram.Path[k] = tram.Route.At(-tram.Speed * 0.5f * (k + 1)).Pos;
    }

    /// <summary>A tram's next leg (the trains' rule on the tram graph: only lines that carry straight on).</summary>
    private (LaneEdge, bool)? NextTramLeg((LaneEdge Edge, bool Forward) leg) => _tramLines is { } g ? NextTrack(g, leg) : null;
    private Func<(LaneEdge Edge, bool Forward), (LaneEdge, bool)?>? _nextTramLeg;

    /// <summary>Whether a tram is in a car lane now (street running): the cars behind it follow it (<see cref="FollowCrossers"/>).</summary>
    private static bool TramInLane(Vehicle tram) => false;
}
