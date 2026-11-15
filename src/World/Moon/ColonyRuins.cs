using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// What makes the two empty colonies what they are (#1944, docs/plans/moon-surface.md section 4). <b>The pit colony</b>:
/// regolith drifted against its modules and rubble fallen from the pit's wall, the lava tube's dark mouth in the wall
/// behind the sealed airlock and its welded hatch, red emergency lights flickering in its modules (<see cref="Flicker"/>),
/// and the stair tower up the wall to the rim with its bridge, all solid. <b>The Cold War base</b>: the grow lamps
/// still on in its biodome. Built once with the colony in its frame (x east, y up, z south; meshes pre-turned for the
/// scratch's half turn).
/// </summary>
public static partial class ColonyRuins
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Dust = C("8a867e"), DustDark = C("6f6b65"), Rock = C("5d5a55"), Mouth = C("08080a"),
        Steel = C("6f7377"), Hazard = C("e0a81a"), Black = C("1b1c1e");

    private static Vector3 T(Vector3 v) => new(-v.X, v.Y, -v.Z);

    /// <summary>
    /// Dresses a colony. <paramref name="groundY"/> gives the ground's height in the colony's frame at an east, north offset
    /// (the rim round the pit); <paramref name="body"/> takes the solids.
    /// </summary>
    public static void Dress(Node3D root, StaticBody3D body, MoonColony c, ColonyPlan plan, Func<double, double, float> groundY)
    {
        if (c.Theme == ColonyTheme.ColdWar)
        {
            foreach (var m in plan.Modules)
                if (m.Kind == ModuleKind.Biodome)
                    for (int i = 0; i < 3; i++)
                    {
                        var (x, z) = ColonyPlan.Along(m.Yaw + i * Math.Tau / 3);
                        root.AddChild(new OmniLight3D
                        {
                            Name = "GrowLamp", LightColor = new Color(1f, 0.85f, 0.6f), LightEnergy = 2.5f, OmniRange = 26, ShadowEnabled = false,
                            Position = new Vector3((float)(m.X + x * m.Width * 0.22), (float)(m.Width * 0.38), (float)(m.Z + z * m.Width * 0.22)),
                        });
                    }
            return;
        }
        if (c.Theme != ColonyTheme.Abandoned) return;
        var rng = new Random(c.Seed);
        var s = new MeshScratch();
        void Solid(Transform3D at, Vector3 size) => body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = at });

        // regolith drifted against the modules' flanks and over their feet, dust inside on the floors
        foreach (var m in plan.Modules)
        {
            var (ax, az) = ColonyPlan.Along(m.Yaw);
            var along = new Vector3((float)ax, 0, (float)az);
            var side = Vector3.Up.Cross(along).Normalized();
            var centre = new Vector3((float)m.X, 0, (float)m.Z);
            int lumps = m.Kind == ModuleKind.Hub ? 8 : 5;
            for (int i = 0; i < lumps; i++)
            {
                float t = (float)rng.NextDouble() * 2 - 1, sd = rng.Next(2) == 0 ? -1 : 1;
                Vector3 at = m.Kind is ModuleKind.Hub or ModuleKind.Dome
                    ? centre + new Vector3(Mathf.Sin(i * 2.4f), 0, Mathf.Cos(i * 2.4f)) * (float)(m.Width / 2 + 0.6)
                    : centre + along * t * (float)(m.Length / 2 - 0.5) + side * sd * (float)(m.Width / 2 + 0.4);
                float rr = 1.0f + (float)rng.NextDouble() * 1.4f;
                s.Tube(T(at) + Vector3.Down * 0.3f, T(at) + Vector3.Up * (rr * 0.7f), rr, rr * 0.2f, i % 2 == 0 ? Dust : DustDark, 7);
            }
            if (m.Kind is ModuleKind.Can or ModuleKind.Hub)
                for (int i = 0; i < 3; i++)
                {
                    var at = centre + along * ((float)rng.NextDouble() - 0.5f) * (float)(m.Length * 0.6) + side * ((float)rng.NextDouble() - 0.5f) * 1.6f;
                    s.Box(T(at) + Vector3.Up * (ColonyKit.FloorY + 0.03f), new Vector3(1.2f, 0.05f, 0.9f), Dust, new Basis(Vector3.Up, (float)rng.NextDouble() * 3));
                }
        }
        // rubble from the wall round the floor's edge, clear of the modules
        for (int i = 0; i < 70; i++)
        {
            float a = (float)rng.NextDouble() * Mathf.Tau, rr = (float)(c.PlateauM - 1 - rng.NextDouble() * 6);
            var at = new Vector3(Mathf.Sin(a) * rr, 0, -Mathf.Cos(a) * rr);
            if (!plan.Clear(at.X, at.Z, 2)) continue;
            float size = 0.4f + (float)rng.NextDouble() * (i % 9 == 0 ? 2.4f : 0.9f);
            s.Box(T(at) + Vector3.Up * (size * 0.3f), new Vector3(size, size * 0.8f, size * 1.1f), i % 3 == 0 ? DustDark : Rock,
                new Basis(Vector3.Up, a) * new Basis(Vector3.Right, (float)rng.NextDouble()));
            if (size > 1.8f) Solid(new Transform3D(new Basis(Vector3.Up, a), at + Vector3.Up * (size * 0.3f)), new Vector3(size * 0.8f, size * 0.7f, size));
        }
        // the lava tube's mouth behind the sealed airlock, its hatch welded shut
        if (plan.Sealed is { } sid)
        {
            var m = plan.Modules[sid];
            var (ax, az) = ColonyPlan.Along(m.Yaw);
            var along = new Vector3((float)ax, 0, (float)az);
            var basis = Basis.LookingAt(-along, Vector3.Up);
            var door = new Vector3((float)m.X, 0, (float)m.Z) + along * (float)(m.Length / 2);
            // the mouth: a wide dark arch in the wall, the rock's lip over it
            var mouth = door + along * 1.4f;
            s.Box(T(mouth + Vector3.Up * 4), new Vector3(11, 8, 0.3f), Mouth, Flip(basis));
            s.Box(T(mouth + Vector3.Up * 8.4f - along * 0.4f), new Vector3(13, 1.4f, 1.6f), Rock, Flip(basis));
            foreach (float sd in new[] { -6f, 6f })
                s.Box(T(mouth + basis.X * sd + Vector3.Up * 4 - along * 0.3f), new Vector3(2.2f, 8.5f, 1.4f), Rock, Flip(basis));
            // the hatch over the outer doorway: a plate, two welded bars across it, hazard paint
            var hatch = door + along * 0.12f + Vector3.Up * ColonyKit.FloorY;
            s.Box(T(hatch + Vector3.Up * (ColonyKit.DoorH / 2)), new Vector3(ColonyKit.DoorW + 0.3f, ColonyKit.DoorH + 0.2f, 0.12f), Steel, Flip(basis));
            foreach (float y in new[] { 0.6f, 1.5f })
                s.Box(T(hatch + Vector3.Up * y - along * 0.1f), new Vector3(ColonyKit.DoorW + 0.9f, 0.18f, 0.12f), Hazard, Flip(basis));
            Solid(new Transform3D(basis, hatch + Vector3.Up * (ColonyKit.DoorH / 2)), new Vector3(ColonyKit.DoorW + 0.3f, ColonyKit.DoorH, 0.3f));
        }
        // the stair tower and its bridge to the rim
        if (plan.Tower is { } tw) Tower(s, Solid, tw, c, groundY);
        var mi = ColonyKit.Instance(s.Build());
        mi.Name = "Ruins";
        root.AddChild(mi);

        // the emergency lights, red and failing, one under each module's ceiling
        var flicker = new Flicker { Name = "EmergencyLights" };
        foreach (var m in plan.Modules)
        {
            if (m.Kind is ModuleKind.Dome) continue;
            // on the axis, reaching to the shell and no farther: no shadows, so a longer reach would light it from outside
            bool round = m.Kind == ModuleKind.Hub;
            var l = new OmniLight3D
            {
                LightColor = new Color(1f, 0.16f, 0.1f), LightEnergy = 1.4f, OmniRange = (float)(m.Width / 2 - 0.2), ShadowEnabled = false,
                Position = new Vector3((float)m.X, round ? (float)m.Height / 2 : (float)(m.Width / 2 + 0.9), (float)m.Z),
            };
            flicker.AddChild(l);
            flicker.Lights.Add((l, rng.Next()));
        }
        root.AddChild(flicker);
    }

    /// <summary>The empty colonies' pieces in the model viewer, with the hangar's rolling door (#1943).</summary>
    [Core.Showcase("Moon colony")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Pieces()
    {
        yield return ("Terminal", () => Painted(ColonyTerminals.Desk(false)));
        yield return ("Terminal (Cold War)", () => Painted(ColonyTerminals.Desk(true)));
        yield return ("Stair tower (12 m)", () =>
        {
            var s = new MeshScratch();
            Tower(s, (_, _) => { }, (0, 0, 0), null!, (_, _) => 12f);
            return Painted(s.Build());
        });
        yield return ("Hangar rolling door", () => Painted(ColonyAirlocks.RollMesh(9.5f, 5f)));
    }

    private static Node3D Painted(ArrayMesh m) => ColonyKit.Instance(m);

    /// <summary>The pre-turn of a basis for the scratch's half turn (a box built along it lands along the original).</summary>
    private static Basis Flip(Basis b) => new(new Vector3(-b.X.X, b.X.Y, -b.X.Z), new Vector3(-b.Y.X, b.Y.Y, -b.Y.Z), new Vector3(-b.Z.X, b.Z.Y, -b.Z.Z));

    /// <summary>The stair flights' rise and slope, how wide a flight is, metres and radians.</summary>
    private const float MaxRise = 2.8f, Run = 4f, FlightW = 1.2f;

    /// <summary>
    /// The stair tower: switchback flights across its footprint (one lane each way, a landing at each turn) up to a deck at
    /// the rim's height, corner posts and rails, and a bridge from the deck out over the wall to the rim, all solid.
    /// </summary>
    private static void Tower(MeshScratch s, Action<Transform3D, Vector3> solid, (double X, double Z, double Yaw) tw, MoonColony c, Func<double, double, float> groundY)
    {
        var (ax, az) = ColonyPlan.Along(tw.Yaw);
        var u = new Vector3((float)ax, 0, (float)az);
        var v = Vector3.Up.Cross(u).Normalized();
        var foot = new Vector3((float)tw.X, 0, (float)tw.Z);
        float half = (float)ColonyPlan.TowerHalf, bridge = 13;
        var end = foot + u * (half + bridge);
        float h = Mathf.Max(groundY(end.X, -end.Z), MaxRise);
        int n = Mathf.CeilToInt(h / MaxRise);
        float rise = h / n;
        var lane = new Basis(u, Vector3.Up, u.Cross(Vector3.Up));
        for (int k = 0; k < n; k++)
        {
            var d = k % 2 == 0 ? v : -v;
            var laneAt = foot + u * (k % 2 == 0 ? -1.2f : 1.2f);
            var slope = (d * Run + Vector3.Up * rise).Normalized();
            var x = u;
            var y = slope.Cross(x);
            if (y.Y < 0) { x = -x; y = -y; }
            var basis = new Basis(x, y, slope);
            var mid = laneAt + Vector3.Up * (k * rise + rise / 2) - y * 0.1f;
            float len = Mathf.Sqrt(Run * Run + rise * rise);
            solid(new Transform3D(basis, mid), new Vector3(FlightW, 0.2f, len + 0.1f));
            s.Box(T(mid), new Vector3(FlightW, 0.2f, len + 0.1f), Steel, Flip(basis));
            // the landing at its top, across both lanes
            var land = foot + d * (Run / 2 + 0.6f) + Vector3.Up * ((k + 1) * rise - 0.1f);
            solid(new Transform3D(lane, land), new Vector3(2 * half - 0.6f, 0.2f, 1.2f));
            s.Box(T(land), new Vector3(2 * half - 0.6f, 0.2f, 1.2f), Black, Flip(lane));
            // its outer rail
            s.Box(T(land + d * 0.55f + Vector3.Up * 1.0f), new Vector3(2 * half - 0.6f, 0.06f, 0.06f), Hazard, Flip(lane));
        }
        // corner posts, a cross brace a storey, the deck at the top
        foreach (float su in new[] { -1f, 1f })
            foreach (float sv in new[] { -1f, 1f })
            {
                var p = foot + u * su * half + v * sv * half;
                s.Tube(T(p), T(p + Vector3.Up * (h + 1.1f)), 0.12f, Steel, 6);
                solid(new Transform3D(Basis.Identity, p + Vector3.Up * (h / 2)), new Vector3(0.25f, h, 0.25f));
            }
        for (int k = 1; k < n; k++)
            foreach (float sv in new[] { -1f, 1f })
                s.Box(T(foot + v * sv * half + Vector3.Up * (k * rise)), new Vector3(2 * half, 0.1f, 0.1f), Hazard, Flip(lane));
        var deck = foot + Vector3.Up * (h - 0.1f) + u * 0.6f;
        solid(new Transform3D(lane, deck), new Vector3(2 * half - 1.2f, 0.2f, 2 * half));
        s.Box(T(deck), new Vector3(2 * half - 1.2f, 0.2f, 2 * half), Black, Flip(lane));
        // the bridge out to the rim, rails either side
        var b0 = foot + u * half + Vector3.Up * (h - 0.1f);
        var bm = b0 + u * (bridge / 2);
        solid(new Transform3D(lane, bm), new Vector3(bridge + 0.6f, 0.2f, 1.8f));
        s.Box(T(bm), new Vector3(bridge + 0.6f, 0.2f, 1.8f), Steel, Flip(lane));
        foreach (float sv in new[] { -0.9f, 0.9f })
        {
            s.Box(T(bm + v * sv + Vector3.Up * 1.0f), new Vector3(bridge, 0.06f, 0.06f), Hazard, Flip(lane));
            solid(new Transform3D(lane, bm + v * sv + Vector3.Up * 0.6f), new Vector3(bridge, 1.2f, 0.08f));
            for (int i = 0; i <= 4; i++)
                s.Tube(T(b0 + u * (i * bridge / 4) + v * sv), T(b0 + u * (i * bridge / 4) + v * sv + Vector3.Up * 1.0f), 0.04f, Steel, 4);
        }
        // a lamp on the deck, a broken mast at the rim
        s.Tube(T(end + Vector3.Up * h), T(end + Vector3.Up * (h + 6) + v * 2.5f), 0.1f, Steel, 5);
    }

    /// <summary>The emergency lights: on most of the time, stuttering now and then, each to its own beat (a light's energy only).</summary>
    public partial class Flicker : Node3D
    {
        public readonly List<(OmniLight3D Light, int Seed)> Lights = new();
        private double _t;

        public override void _Process(double delta)
        {
            _t += delta;
            foreach (var (l, seed) in Lights)
            {
                // a slow carrier and a fast stutter, hashed per light: dark a fifth of the time
                double slow = Math.Sin(_t * 0.7 + seed % 97) + Math.Sin(_t * 1.9 + seed % 31);
                bool stutter = ((int)(_t * 14) * 2654435761u + (uint)seed) % 7 == 0;
                l.LightEnergy = slow < -1.1 || stutter ? 0.05f : 1.2f;
            }
        }
    }
}
