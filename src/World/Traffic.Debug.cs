using Godot;

namespace UnitSport.World;

/// <summary>
/// The traffic's debug drawing (#971, debug menu): each car's way ahead (a generated car's way to its destination in that
/// destination's colour, any other's planned legs in grey) and the traffic endpoints (a post, green a generator, red a
/// destination, yellow both, with an arrow along its heading). Lines through everything, redrawn five times a second.
/// </summary>
public partial class Traffic
{
    /// <summary>Draws every car's way ahead.</summary>
    public bool ShowPaths { get; set; }

    /// <summary>Draws the traffic endpoints.</summary>
    public bool ShowEndpoints { get; set; }

    private MeshInstance3D? _debugLines;
    private ImmediateMesh? _debugMesh;
    private double _debugDue;

    /// <summary>How far ahead a car's way is drawn (m): a generated car's to its destination at most this.</summary>
    private const float PathDrawn = 600f;

    private static readonly Color[] DestinationColours =
    [
        new(0.2f, 0.6f, 1f), new(1f, 0.55f, 0.1f), new(0.75f, 0.3f, 1f), new(0.1f, 0.9f, 0.8f),
        new(1f, 0.3f, 0.6f), new(0.6f, 1f, 0.2f), new(1f, 0.9f, 0.2f), new(0.5f, 0.5f, 1f),
    ];

    /// <summary>The colour a destination's cars are drawn in.</summary>
    public static Color DestinationColour(int id) => DestinationColours[(id % DestinationColours.Length + DestinationColours.Length) % DestinationColours.Length];

    private void StepDebugDraw(double dt)
    {
        if (!ShowPaths && !ShowEndpoints)
        {
            if (_debugLines is not null) _debugLines.Visible = false;
            return;
        }
        if ((_debugDue -= dt) > 0) return;
        _debugDue = 0.2;
        if (_debugLines is null)
        {
            _debugMesh = new ImmediateMesh();
            _debugLines = new MeshInstance3D
            {
                Mesh = _debugMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled, RenderPriority = 10,
                },
            };
            AddChild(_debugLines);
        }
        _debugLines.Visible = true;
        var mesh = _debugMesh!;
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        int lines = 0;
        if (ShowPaths)
            foreach (var car in _cars) lines += DrawWay(mesh, car);
        if (ShowEndpoints)
            foreach (var s in _endpoints.Values) lines += DrawEndpoint(mesh, s);
        if (lines == 0)
        {
            // an empty surface is an error: one invisible line
            Ribbon(mesh, Vector3.Zero, Vector3.Zero, Colors.Transparent, 0f);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>A flat band <paramref name="width"/> m wide from <paramref name="a"/> to <paramref name="b"/> (a line is a pixel from above), seen from both sides.</summary>
    private static void Ribbon(ImmediateMesh mesh, Vector3 a, Vector3 b, Color colour, float width)
    {
        var d = b - a;
        // across it on the ground; a post (straight up) across east-west
        var across = new Vector3(-d.Z, 0f, d.X);
        across = across.LengthSquared() > 1e-6f ? across.Normalized() * (width * 0.5f) : new Vector3(width * 0.5f, 0f, 0f);
        Vector3[] quad = [a - across, a + across, b + across, a - across, b + across, b - across];
        foreach (var v in quad)
        {
            mesh.SurfaceSetColor(colour);
            mesh.SurfaceAddVertex(v);
        }
    }

    private int DrawWay(ImmediateMesh mesh, Vehicle car)
    {
        var up = Vector3.Up * 1.5f;
        var colour = car.Planned is { Count: > 0 } way && car.ToId > 0 ? DestinationColour(car.ToId) : new Color(0.7f, 0.7f, 0.7f, 0.8f);
        // the legs from its own: its planned way to the destination, else the legs its route has planned ahead
        var legs = new List<(LaneEdge Edge, bool Forward)>();
        var current = car.Route.Legs[car.Route.Leg];
        int k = car.Planned?.IndexOf(current) ?? -1;
        if (k >= 0) legs.AddRange(car.Planned!.Skip(k));
        else legs.AddRange(car.Route.Legs.Skip(car.Route.Leg));
        // each leg's strip where a transition steers it, as FollowTransition and HandOver pick it: from the path ahead, else the path behind
        var strips = new int[legs.Count];
        for (int i = 0; i < legs.Count; i++) strips[i] = StripOn(legs, i, i == 0 ? car.Strip : -1);
        int n = 0;
        float drawn = 0f;
        var last = car.Head + up;
        for (int i = 0; i < legs.Count && drawn < PathDrawn; i++)
        {
            var (edge, forward) = legs[i];
            float from = i == 0 ? car.Route.Arc : 0f;
            for (float s = from + 4f; s < edge.Length + 4f && drawn < PathDrawn; s += 4f, drawn += 4f)
            {
                float along = Mathf.Min(s, edge.Length);
                var (p, tangent) = edge.Sample(forward ? along : edge.Length - along);
                var dir = forward ? tangent : -tangent;
                var right = new Vector3(-dir.Z, 0f, dir.X).Normalized();
                p += right * LineAt(car, legs, i, along, strips[i]);
                Ribbon(mesh, last, p + up, colour, 0.6f);
                last = p + up;
                n++;
                if (along >= edge.Length) break;
            }
        }
        return n;
    }

    /// <summary>The transition strip a car drives on leg <paramref name="i"/> of <paramref name="legs"/>: the one leading to the path ahead, else the one the path behind ends on; -1 none.</summary>
    private static int StripOn(List<(LaneEdge Edge, bool Forward)> legs, int i, int current)
    {
        if (legs[i].Edge.Transition is null || legs[i].Edge.Path is not null) return -1;
        for (int j = i + 1; j < legs.Count; j++)
        {
            if (legs[j].Edge.Path is { } path) return legs[j - 1].Edge.StripAt(legs[j - 1].Forward, atTravelEnd: true, path.FromOffset);
            if (legs[j].Edge.Transition is null) break;
        }
        for (int j = i - 1; j >= 0; j--)
        {
            if (legs[j].Edge.Path is { } path) return legs[j + 1].Edge.StripAt(legs[j + 1].Forward, atTravelEnd: false, -path.ToOffset);
            if (legs[j].Edge.Transition is null) break;
        }
        return current;
    }

    /// <summary>
    /// Where a car drives across leg <paramref name="i"/>, <paramref name="along"/> m into it, right of the edge's line (m): a
    /// junction path's own line; a transition's strip; an approach's lane as its record lays it out, eased onto from its usual line;
    /// else its usual line. The drawing's copy of what <see cref="Approach"/> and <see cref="FollowTransition"/> steer to.
    /// </summary>
    private static float LineAt(Vehicle car, List<(LaneEdge Edge, bool Forward)> legs, int i, float along, int strip)
    {
        var (edge, forward) = legs[i];
        if (edge.Path is not null) return 0f;
        float usual = KeepRight(edge, forward, car.Habit);
        if (strip >= 0 && edge.LaneAt(forward, along, strip) is { } lane) return lane;
        if ((forward ? edge.ApproachAtEnd : edge.ApproachAtStart) is var (approach, back) && approach.MultiLane
            && i + 1 < legs.Count && legs[i + 1].Edge.Path is { } path)
        {
            float d = Mathf.Max(0f, edge.Length - along - back);
            float onto = Mathf.Clamp((approach.Reach + 15f - d) / 15f, 0f, 1f);
            return usual + (approach.LaneCentre - usual) * onto + approach.Lateral(approach.LaneNearest(path.FromOffset), d);
        }
        return usual;
    }

    private int DrawEndpoint(ImmediateMesh mesh, EndpointState s)
    {
        if (s.Edge is null) return 0;
        var (p, tangent) = s.Edge.Sample(s.Pos);
        var heading = s.Forward ? tangent : -tangent;
        var colour = s.Ep.Generates && s.Ep.Destination ? Colors.Yellow : s.Ep.Generates ? Colors.LimeGreen : Colors.Red;
        var top = p + Vector3.Up * 8f;
        var side = new Vector3(-heading.Z, 0f, heading.X);
        var tip = top + heading * 5f;
        Vector3[] segments =
        [
            p, top,
            top, tip,
            tip, tip - heading * 1.5f + side * 1f,
            tip, tip - heading * 1.5f - side * 1f,
        ];
        for (int i = 0; i + 1 < segments.Length; i += 2) Ribbon(mesh, segments[i], segments[i + 1], colour, 0.8f);
        // its colour as a destination: a ring at its foot
        if (s.Ep.Destination)
            for (int i = 0; i < 12; i++)
            {
                float a = i * Mathf.Tau / 12f, b = (i + 1) * Mathf.Tau / 12f;
                Ribbon(mesh, p + new Vector3(Mathf.Cos(a), 0.3f, Mathf.Sin(a)) * 4f, p + new Vector3(Mathf.Cos(b), 0.3f, Mathf.Sin(b)) * 4f,
                    DestinationColour(s.Ep.Id), 0.8f);
            }
        return 4;
    }
}
