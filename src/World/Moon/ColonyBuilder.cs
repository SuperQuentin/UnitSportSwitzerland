using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// A colony's node from its plan (#1940, #1941): every module from the kit (shared meshes) turned along its axis on the
/// plateau with its inside an interior as any building's (#2000: <see cref="InteriorGenerator.Colony"/>, drawn, lit and
/// solid by <see cref="InteriorMeshBuilder"/>, an <see cref="InteriorNode"/> in place), the colony's tubes as one mesh with
/// an interior each, the steps down from each airlock's outer door,
/// the airlocks' doors and the air (<see cref="ColonyAirlocks"/>), the pads and the road. In the colony's frame (x east,
/// y up, z south, the plateau at y = 0); <see cref="MoonSurface"/> places it from doubles. Built once when the camera
/// comes within reach, never per frame.
/// </summary>
public static class ColonyBuilder
{
    private const float F = ColonyKit.FloorY;

    /// <param name="groundY">The ground's height in the colony's frame at an east, north offset (the pit's rim, #1944); the
    /// plateau's 0 when null.</param>
    /// <param name="server">On a server hosting the Moon (#2000, <see cref="ServerColonies"/>): its region's frame and its
    /// players' places, which the crew's bots take for the camera.</param>
    public static Node3D Build(MoonColony c, ColonyPlan plan, Func<double, double, float>? groundY = null,
        (Core.WorldOrigin Origin, Func<IReadOnlyList<Vector3>> Eyes)? server = null)
    {
        var root = new Node3D { Name = c.Id };
        var body = new StaticBody3D { Name = "Solid" };
        root.AddChild(body);
        void Solid(Transform3D at, Vector3 size) =>
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = at });

        foreach (var m in plan.Modules)
        {
            int side = plan.SideDoor(m.Id);
            var mesh = ColonyKit.Module(m.Kind, m.Length, m.Width, m.Height, m.Storeys, c.Theme, side);
            var mi = m.Kind == ModuleKind.Biodome ? Glazed(mesh) : ColonyKit.Instance(mesh);
            // authored along +Z, turned to -Z by the scratch: -Z along the module's axis
            var t = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            mi.Transform = t;
            mi.Name = $"{m.Kind}{m.Id}";
            root.AddChild(mi);
            // its inside: an interior as any building's (#2000), planned, furnished, drawn, lit and solid by the interiors
            // system, placed in the module's node frame on its floor
            var layout = InteriorGenerator.Colony(m, plan.Rooms[m.Id], c.Theme, side, $"{c.Id}/m{m.Id}");
            root.AddChild(Inside(layout, t * Up(InteriorGenerator.FloorOf(m.Kind)), $"Inside{m.Id}"));
            // the hangar's ramp down from its vehicle door to the plateau (#1943), outside the interior (its node's -Z)
            if (m.Kind == ModuleKind.Hangar)
            {
                float slab = ColonyKit.HangarSlab, rampA = Mathf.Atan2(slab, ColonyKit.RampLen), dh = ColonyKit.HangarDoorHalf((float)m.Width);
                Solid(t * new Transform3D(new Basis(Vector3.Right, -rampA), new Vector3(0, slab / 2 - 0.15f, -(float)m.Length / 2 - 0.3f - ColonyKit.RampLen / 2)),
                    new Vector3(2 * dh, 0.3f, ColonyKit.RampLen / Mathf.Cos(rampA) + 0.3f));
            }
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
            // a tube's (or a glazed alley's) inside: an interior of its own along it (#2000)
            var at = new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(d.X, d.Z)), (pa + pb) / 2);
            var tube = InteriorGenerator.ColonyLink(d.Length(), l.Alley, c.Theme, $"{c.Id}/t{l.DoorA}");
            root.AddChild(Inside(tube, at * Up(F + InteriorGenerator.ColonyFloorLift), $"Tube{l.DoorA}"));
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
            // solid as a ramp along the treads' noses (#1976): a walker coming back up met each 0.22 m riser as a wall
            float run = ColonyKit.Steps * ColonyKit.Tread, slope = Mathf.Atan2(F, run);
            var tilt = new Basis(Vector3.Right, slope);
            var normal = tilt * Vector3.Up;
            var mid = new Vector3(0, F / 2, run / 2) - normal * 0.15f;
            Solid(new Transform3D(basis * tilt, new Vector3((float)d.X, 0, (float)d.Z) + basis * mid),
                new Vector3(1.6f, 0.3f, Mathf.Sqrt(run * run + F * F) + 0.3f));
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
        // the staffed colonies' crews (#1945): decoration figures offline, the same schedule a server bot would follow
        if (c.Staffed) root.AddChild(new ColonyColonists(c, plan) { OriginOverride = server?.Origin, Eyes = server?.Eyes });
        // the industry on the plateau (y = 0): arrays turned to the sun, the digger round its pit
        if (plan.Industry.Count > 0)
            root.AddChild(MoonIndustry.Build(plan.Industry, c.Theme, c.Lat, c.Lon, (e, n) => new Vector3((float)e, 0, (float)-n), body));
        return root;
    }

    private static Transform3D Up(float y) => new(Basis.Identity, new Vector3(0, y, 0));

    /// <summary>A module's or a tube's interior node: its rooms' mesh in the interiors' lit material, and their collision.</summary>
    private static Node3D Inside(InteriorLayout layout, Transform3D placement, string name)
    {
        var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), placement);
        node.Name = name;
        return node;
    }

    private static MeshInstance3D Glazed(ArrayMesh m)
    {
        var mi = new MeshInstance3D { Mesh = m };
        Avatar.MeshScratch.Paint(mi, Avatar.HumanMeshBuilder.Material(), ColonyKit.GlassMaterial());
        return mi;
    }
}
