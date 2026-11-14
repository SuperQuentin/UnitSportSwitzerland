using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// A colony's node from its plan (#1940, #1941): every module from the kit (shared meshes) turned along its axis on the
/// plateau with its furniture inside, a hollow solid you walk into (floors at the doors' sill, walls round, doorways
/// open), the colony's tubes as one mesh with their own hollow solid, the steps down from each airlock's outer door,
/// the airlocks' doors and the air (<see cref="ColonyAirlocks"/>), the pads and the road. In the colony's frame (x east,
/// y up, z south, the plateau at y = 0); <see cref="MoonSurface"/> places it from doubles. Built once when the camera
/// comes within reach, never per frame.
/// </summary>
public static class ColonyBuilder
{
    private const float F = ColonyKit.FloorY, Wall = 0.2f;

    /// <param name="groundY">The ground's height in the colony's frame at an east, north offset (the pit's rim, #1944); the
    /// plateau's 0 when null.</param>
    public static Node3D Build(MoonColony c, ColonyPlan plan, Func<double, double, float>? groundY = null)
    {
        var root = new Node3D { Name = c.Id };
        var body = new StaticBody3D { Name = "Solid" };
        root.AddChild(body);
        void Solid(Transform3D at, Vector3 size) =>
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = at });

        foreach (var m in plan.Modules)
        {
            var mesh = ColonyKit.Module(m.Kind, m.Length, m.Width, m.Height, m.Storeys, c.Theme);
            var mi = m.Kind == ModuleKind.Biodome ? Glazed(mesh) : ColonyKit.Instance(mesh);
            // authored along +Z, turned to -Z by the scratch: -Z along the module's axis
            var t = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            mi.Transform = t;
            mi.Name = $"{m.Kind}{m.Id}";
            root.AddChild(mi);
            var (inside, solids) = ColonyInteriors.Furnish(m.Kind, plan.Rooms[m.Id], m.Length, m.Width, c.Theme);
            root.AddChild(new MeshInstance3D { Name = $"Inside{m.Id}", Mesh = inside, Transform = t, MaterialOverride = null }.Painted());
            foreach (var (centre, size) in solids) Solid(t * new Transform3D(Basis.Identity, centre), size);
            foreach (var (at, size) in Hollow(m)) Solid(t * at, size);
        }
        var links = new List<(Vector3, Vector3, bool)>();
        foreach (var l in plan.Links)
        {
            var a = plan.Doors[l.DoorA];
            var b = plan.Doors[l.DoorB];
            var pa = new Vector3((float)a.X, 0, (float)a.Z);
            var pb = new Vector3((float)b.X, 0, (float)b.Z);
            links.Add((pa, pb, l.Alley));
            var d = pb - pa;
            if (d.Length() < 0.5f) continue;
            // a tube's hollow: its floor at the sill, walls either side, a roof
            var at = new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(d.X, d.Z)), (pa + pb) / 2);
            float len = d.Length() + 0.2f, half = ColonyKit.TubeR - 0.25f;
            Solid(at * new Transform3D(Basis.Identity, new Vector3(0, F - 0.1f, 0)), new Vector3(2 * half + 0.4f, 0.2f, len));
            Solid(at * new Transform3D(Basis.Identity, new Vector3(-half - Wall / 2, F + 1.3f, 0)), new Vector3(Wall, 2.6f, len));
            Solid(at * new Transform3D(Basis.Identity, new Vector3(half + Wall / 2, F + 1.3f, 0)), new Vector3(Wall, 2.6f, len));
            Solid(at * new Transform3D(Basis.Identity, new Vector3(0, F + 2.5f, 0)), new Vector3(2 * half + 0.4f, 0.2f, len));
        }
        if (links.Exists(l => !l.Item3)) root.AddChild(ColonyKit.Instance(ColonyKit.Links(links, c.Theme)));
        var alleys = links.FindAll(l => l.Item3).ConvertAll(l => (l.Item1, l.Item2));
        if (alleys.Count > 0) root.AddChild(Glazed(ColonyKit.Alleys(alleys, c.Theme)));
        // the steps down from each airlock's outer door to the plateau
        var stairs = new List<(Vector3, float)>();
        foreach (var a in plan.Airlocks)
        {
            var d = plan.Doors[a.Outer];
            stairs.Add((new Vector3((float)d.X, 0, (float)d.Z), (float)d.Out));
            var dir = new Vector3(Mathf.Sin((float)d.Out), 0, -Mathf.Cos((float)d.Out));
            var basis = new Basis(Vector3.Up, Mathf.Atan2(dir.X, dir.Z));
            for (int k = 0; k < ColonyKit.Steps; k++)
            {
                float top = F - k * F / ColonyKit.Steps;
                var centre = new Vector3((float)d.X, top / 2, (float)d.Z) + dir * (k * ColonyKit.Tread + ColonyKit.Tread / 2);
                Solid(new Transform3D(basis, centre), new Vector3(1.6f, top, ColonyKit.Tread));
            }
        }
        if (stairs.Count > 0) root.AddChild(ColonyKit.Instance(ColonyKit.Stairs(stairs, c.Theme)));
        root.AddChild(new ColonyAirlocks(c, plan));
        foreach (var p in plan.Pads)
        {
            var pad = ColonyKit.Instance(ColonyKit.PadMesh((float)p.Radius, p.Number));
            pad.Position = new Vector3((float)p.X, 0, (float)p.Z);
            pad.Name = $"Pad{p.Number}";
            root.AddChild(pad);
            body.AddChild(new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = (float)p.Radius, Height = 0.42f },
                Position = new Vector3((float)p.X, -0.09f, (float)p.Z),
            });
        }
        if (plan.Road.Count > 1) root.AddChild(ColonyKit.Instance(ColonyKit.RoadMesh(plan.Road)));
        // the empty colonies (#1944): the pit's ruins and tower, the Cold War dome's lamps, their terminals' logs
        ColonyRuins.Dress(root, body, c, plan, groundY ?? ((_, _) => 0f));
        if (ColonyLogs.For(c.Theme).Length > 0) root.AddChild(new ColonyTerminals(c, plan));
        // the industry on the plateau (y = 0): arrays turned to the sun, the digger round its pit
        if (plan.Industry.Count > 0)
            root.AddChild(MoonIndustry.Build(plan.Industry, c.Theme, c.Lat, c.Lon, (e, n) => new Vector3((float)e, 0, (float)-n), body));
        return root;
    }

    private static MeshInstance3D Painted(this MeshInstance3D mi)
    {
        Avatar.MeshScratch.Paint(mi, Avatar.HumanMeshBuilder.Material(), ColonyKit.WindowMaterial());
        return mi;
    }

    private static MeshInstance3D Glazed(ArrayMesh m)
    {
        var mi = new MeshInstance3D { Mesh = m };
        Avatar.MeshScratch.Paint(mi, Avatar.HumanMeshBuilder.Material(), ColonyKit.GlassMaterial());
        return mi;
    }

    private static Transform3D At(float x, float y, float z, float yaw = 0) => new(new Basis(Vector3.Up, yaw), new Vector3(x, y, z));

    /// <summary>
    /// A module's hollow solid in its node's frame (the long axis along z; symmetric, so the scratch's half turn does
    /// not matter, except the hangar's landing, flipped here): a floor at the sill, walls of boxes round, end walls with
    /// the doorway open, a ceiling.
    /// </summary>
    private static IEnumerable<(Transform3D At, Vector3 Size)> Hollow(ColonyModule m)
    {
        float l = (float)m.Length, w = (float)m.Width, h = (float)m.Height, r = w / 2;
        float dw = ColonyKit.DoorW, top = F + ColonyKit.DoorH;
        switch (m.Kind)
        {
            case ModuleKind.Can:
            case ModuleKind.Airlock:
            {
                float y = r + 0.9f;
                // the floor: the shell's chord at the sill, no wider (no ledge outside)
                float chord = 2 * Mathf.Sqrt(Mathf.Max(0, r * r - (y - F) * (y - F)));
                yield return (At(0, F - 0.1f, 0), new Vector3(chord, 0.2f, l));
                // the shell: boxes round the axis (the floor's chord up to the crown)
                const int n = 12;
                for (int i = 0; i < n; i++)
                {
                    float a = Mathf.Tau * (i + 0.5f) / n;
                    var c = new Vector3(Mathf.Cos(a) * (r + Wall / 2), y + Mathf.Sin(a) * (r + Wall / 2), 0);
                    if (c.Y < F) continue;
                    yield return (new Transform3D(new Basis(Vector3.Back, a), c), new Vector3(Wall, 2 * r * Mathf.Sin(Mathf.Pi / n) * 1.15f, l));
                }
                foreach (float z in new[] { -l / 2, l / 2 })
                {
                    float side = (r - dw / 2);
                    yield return (At(-(dw / 2 + side / 2), y, z), new Vector3(side, 2 * r, Wall));
                    yield return (At(dw / 2 + side / 2, y, z), new Vector3(side, 2 * r, Wall));
                    yield return (At(0, (top + y + r) / 2, z), new Vector3(dw, y + r - top, Wall));
                }
                break;
            }
            case ModuleKind.Hub:
            case ModuleKind.Stack:
            {
                yield return (At(0, F / 2, 0), new Vector3(w * 0.72f, F, w * 0.72f));
                yield return (At(0, F / 2, 0), new Vector3(w * 0.5f, F, w * 0.9f));
                yield return (At(0, F / 2, 0), new Vector3(w * 0.9f, F, w * 0.5f));
                yield return (At(0, h, 0), new Vector3(w, 0.2f, w));
                const int n = 20;
                float pw = 2 * r * Mathf.Sin(Mathf.Pi / n) * 1.08f;
                for (int i = 0; i < n; i++)
                {
                    float a = Mathf.Tau * i / n;
                    var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                    if (i % (n / 4) == 0)
                    {
                        yield return (new Transform3D(new Basis(Vector3.Up, a), dir * r + Vector3.Up * ((top + h) / 2)), new Vector3(pw, h - top, Wall));
                        continue;
                    }
                    yield return (new Transform3D(new Basis(Vector3.Up, a), dir * r + Vector3.Up * (h / 2)), new Vector3(pw, h, Wall));
                }
                break;
            }
            case ModuleKind.Dome:
            {
                yield return (At(0, F / 2, 0), new Vector3(w * 0.68f, F, w * 0.68f));
                yield return (At(0, F / 2, 0), new Vector3(w * 0.4f, F, w * 0.92f));
                yield return (At(0, F / 2, 0), new Vector3(w * 0.92f, F, w * 0.4f));
                const int n = 16;
                float rr = r * 0.9f, pw = 2 * rr * Mathf.Sin(Mathf.Pi / n) * 1.1f;
                for (int i = 0; i < n; i++)
                {
                    float a = Mathf.Tau * (i + 0.5f) / n;
                    var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                    // the entrance (authored at +Z: the node's -Z after the scratch's half turn) stays open
                    if (Mathf.Abs(Mathf.Wrap(a - Mathf.Pi, -Mathf.Pi, Mathf.Pi)) < 0.3f) continue;
                    yield return (new Transform3D(new Basis(Vector3.Up, a), dir * rr + Vector3.Up * (F + 1.6f)), new Vector3(pw, 3.2f, Wall));
                }
                yield return (At(0, F + r * 0.75f, 0), new Vector3(w * 0.8f, 0.2f, w * 0.8f));
                break;
            }
            case ModuleKind.Hangar:
            {
                float floor = 0.6f;
                yield return (At(0, floor / 2, 0), new Vector3(w, floor, l));
                yield return (At(-r - Wall / 2, floor + h * 0.4f, 0), new Vector3(Wall, h * 0.8f, l));
                yield return (At(r + Wall / 2, floor + h * 0.4f, 0), new Vector3(Wall, h * 0.8f, l));
                yield return (At(0, floor + h, 0), new Vector3(w, 0.3f, l));
                // the vehicle door's end (authored +Z: the node's -Z): the wall either side of its opening and over it (the
                // door's own box is ColonyAirlocks'), the ramp down to the plateau (#1943)
                float dh = ColonyKit.HangarDoorHalf(w), lintel = floor + ColonyKit.HangarDoorH;
                foreach (float sx in new[] { -1f, 1f })
                    yield return (At(sx * (dh + (r - dh) / 2), floor + h / 2, -l / 2), new Vector3(r - dh, h, Wall));
                yield return (At(0, (lintel + floor + h) / 2, -l / 2), new Vector3(2 * dh, floor + h - lintel, Wall));
                float rampA = Mathf.Atan2(floor, ColonyKit.RampLen);
                yield return (new Transform3D(new Basis(Vector3.Right, -rampA), new Vector3(0, floor / 2 - 0.15f, -l / 2 - 0.3f - ColonyKit.RampLen / 2)),
                    new Vector3(2 * dh, 0.3f, ColonyKit.RampLen / Mathf.Cos(rampA) + 0.3f));
                float side = r - dw / 2;
                yield return (At(-(dw / 2 + side / 2), floor + h / 2, l / 2), new Vector3(side, h, Wall));
                yield return (At(dw / 2 + side / 2, floor + h / 2, l / 2), new Vector3(side, h, Wall));
                yield return (At(0, (top + floor + h) / 2, l / 2), new Vector3(dw, floor + h - top, Wall));
                // the landing at the sill and its steps (authored at -Z: the node's +Z)
                yield return (At(0, (F + floor) / 2, l / 2 - 1.0f), new Vector3(2.4f, F - floor, 2.0f));
                const int steps = 5;
                float rise = (F - floor) / steps;
                for (int k = 1; k < steps; k++)
                    yield return (At(0, floor + (F - floor - k * rise) / 2, l / 2 - 2.0f - (k - 0.5f) * 0.3f), new Vector3(2.4f, F - floor - k * rise, 0.3f));
                break;
            }
            case ModuleKind.Biodome:
            {
                // the garden's floor at 1.25, the ring wall and the glass's low band round it with the doorway open at the
                // node's -Z (authored +Z), the landing inside the doorway, the trees' trunks (#1944)
                yield return (At(0, 0.6f, 0), new Vector3(w * 0.72f, 1.3f, w * 0.72f));
                yield return (At(0, 0.6f, 0), new Vector3(w * 0.45f, 1.3f, w * 0.96f));
                yield return (At(0, 0.6f, 0), new Vector3(w * 0.96f, 1.3f, w * 0.45f));
                const int n = 28;
                float band = 1.2f + r * 0.3f, pw = 2 * r * Mathf.Sin(Mathf.Pi / n) * 1.1f;
                for (int i = 0; i < n; i++)
                {
                    float a = Mathf.Tau * (i + 0.5f) / n;
                    if (Mathf.Abs(Mathf.Wrap(a - Mathf.Pi, -Mathf.Pi, Mathf.Pi)) < 0.12f) continue;
                    var dir = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                    yield return (new Transform3D(new Basis(Vector3.Up, a), dir * (r + 0.2f) + Vector3.Up * (band / 2)), new Vector3(pw, band, Wall));
                }
                yield return (At(0, (F + 1.25f) / 2, -(r - 0.4f)), new Vector3(2.2f, F - 1.25f, 1.6f));
                // the overgrown garden is the Cold War's 64 m dome (the only one this wide)
                foreach (var (at, th, _) in ColonyKit.Trees(r, w >= 60))
                    yield return (At(-at.X, at.Y + th / 2, -at.Z), new Vector3(0.4f, th, 0.4f));
                break;
            }
        }
    }
}
