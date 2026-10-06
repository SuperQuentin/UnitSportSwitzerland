using Godot;
using UnitSport.Core;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--stairwalkcheck</c> (#571): whether a block of flats' stairwell can be climbed, from its
/// collision alone, with no world. Each <see cref="FlatCheck"/> block is planned and built with
/// the real mesh builder; then, for every stairwell and every floor, rays walk the line a player
/// takes: off the front landing up the first flight, across the half landing, up the second
/// flight onto the next floor's landing. Under every step there must be something to stand on at
/// the flight's height (the ramp), and above it head room, so the flight overhead never brains the
/// one climbing. Headless; prints a RESULT line.
/// </summary>
public partial class StairWalkCheck : Node3D
{
    public static bool Requested => CmdArgs.Has("--stairwalkcheck");

    private int _failures, _probes, _frames;
    private readonly List<InteriorLayout> _layouts = new();

    public override void _Ready()
    {
        var tile = FlatCheck.Tile();
        float x = 0;
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            var l = InteriorGenerator.Generate(tile, i, null, null);
            if (l == null || l.Type is not (BuildingType.Apartments or BuildingType.MixedUse) || l.Floors.Count < 2) continue;
            // side by side, far enough apart that no ray reaches the next one
            var node = InteriorNode.Create(l, InteriorMeshBuilder.Build(l), Styles.StyleKit.Material(Styles.MaterialRole.Interior),
                new Transform3D(Basis.Identity, new Vector3(x, 0, 0)));
            node.Name = "Block" + i;
            AddChild(node);
            _layouts.Add(l);
            x += 200;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        // the bodies are in the space a physics frame after they are added
        if (++_frames < 3) return;
        var space = GetWorld3D().DirectSpaceState;
        float x = 0;
        foreach (var l in _layouts)
        {
            Walk(space, l, new Vector3(x, 0, 0));
            x += 200;
        }
        GD.Print($"[stairwalk] {_probes} probes on {_layouts.Count} blocks");
        GD.Print($"[stairwalk] RESULT: {(_failures == 0 ? "ok" : $"FAILED ({_failures})")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
        SetPhysicsProcess(false);
    }

    private void Walk(PhysicsDirectSpaceState3D space, InteriorLayout l, Vector3 at)
    {
        float h = l.StoreyHeight;
        int bad = 0;
        for (int f = 0; f + 1 < l.Floors.Count; f++)
        {
            float y0 = l.FloorY(f);
            foreach (var fl in l.Floors[f].AllFlights().Where(x => x.Half))
            {
                float xm = (fl.X0 + fl.X1) / 2, ya = y0 + h * fl.From, yb = y0 + h * fl.To;
                Vector3 Foot(float along, float y) { var (px, pz) = fl.Point(xm, along); return new Vector3(px, y, pz); }
                int steps = (int)MathF.Ceiling((yb - ya) / 0.18f);
                float dir = Math.Sign(fl.ZTop - fl.ZBottom), tread = Math.Abs(fl.ZTop - fl.ZBottom) / steps;
                float zs = fl.ZBottom - dir * tread;
                // from a tread before the flight to its top, and a little onto what it arrives at
                for (float t = 0; t <= 1.08f; t += 0.08f)
                {
                    float z = zs + (fl.ZTop - zs) * Math.Min(t, 1f) + (t > 1 ? dir * 0.3f : 0);
                    float want = ya + (yb - ya) * Math.Clamp(t, 0f, 1f);
                    if (!Probe(space, at + Foot(z, want), out string why))
                    {
                        if (bad++ < 4) Fail($"{l.Key} floor {f} flight {fl.From:F1}-{fl.To:F1} at z {z:F2}: {why}");
                    }
                }
            }
            foreach (var g in l.Floors[f].Landings)
                for (float gx = g.X0 + 0.3f; gx < g.X1 - 0.2f; gx += 0.4f)
                    if (!Probe(space, at + new Vector3(gx, y0 + h * g.Level, (g.Z0 + g.Z1) / 2), out string why) && bad++ < 4)
                        Fail($"{l.Key} floor {f} half landing at x {gx:F2}: {why}");
        }
        if (bad == 0) GD.Print($"[stairwalk] ok   {l.Key}: {l.Floors.Count} floors, every flight and half landing walkable");
        else if (bad > 4) Fail($"{l.Key}: {bad - 4} more");
    }

    /// <summary>Something to stand on within a step of <paramref name="foot"/>, and head room over it.</summary>
    private bool Probe(PhysicsDirectSpaceState3D space, Vector3 foot, out string why)
    {
        _probes++;
        var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(foot + Vector3.Up * 0.5f, foot + Vector3.Down * 0.35f));
        if (down.Count == 0) { why = "nothing to stand on"; return false; }
        float y = ((Vector3)down["position"]).Y;
        if (Math.Abs(y - foot.Y) > 0.22f) { why = $"ground at {y - foot.Y:+0.00;-0.00} m from the flight's line"; return false; }
        var up = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(foot.X, y + 0.1f, foot.Z), new Vector3(foot.X, y + 1.9f, foot.Z)));
        if (up.Count > 0) { why = $"head room only {((Vector3)up["position"]).Y - y:F2} m"; return false; }
        why = "";
        return true;
    }

    private void Fail(string what)
    {
        _failures++;
        GD.Print($"[stairwalk] FAIL {what}");
    }
}
