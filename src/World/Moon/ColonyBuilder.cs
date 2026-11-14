using System.Collections.Generic;
using Godot;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// A colony's node from its plan (#1940): every module from the kit (shared meshes) turned along its axis on the
/// plateau, its solid shapes, the colony's tubes and alleys as one mesh, the pads and the road. In the colony's frame
/// (x east, y up, z south, the plateau at y = 0); <see cref="MoonSurface"/> places it from doubles. Built once when the
/// camera comes within reach, never per frame.
/// </summary>
public static class ColonyBuilder
{
    public static Node3D Build(MoonColony c, ColonyPlan plan)
    {
        var root = new Node3D { Name = c.Id };
        var body = new StaticBody3D { Name = "Solid" };
        root.AddChild(body);
        foreach (var m in plan.Modules)
        {
            var mesh = ColonyKit.Module(m.Kind, m.Length, m.Width, m.Height, m.Storeys, c.Theme);
            var mi = m.Kind == ModuleKind.Biodome ? Glazed(mesh) : ColonyKit.Instance(mesh);
            // authored along +Z, turned to -Z by the scratch: -Z along the module's axis
            var t = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            mi.Transform = t;
            mi.Name = $"{m.Kind}{m.Id}";
            root.AddChild(mi);
            foreach (var (centre, shape) in Shapes(m))
                body.AddChild(new CollisionShape3D { Shape = shape, Transform = t * centre });
        }
        var links = new List<(Vector3, Vector3, bool)>();
        foreach (var l in plan.Links)
        {
            var a = plan.Doors[l.DoorA];
            var b = plan.Doors[l.DoorB];
            links.Add((new Vector3((float)a.X, 0, (float)a.Z), new Vector3((float)b.X, 0, (float)b.Z), l.Alley));
            // a tube's solid: a box along it
            var pa = new Vector3((float)a.X, 0, (float)a.Z);
            var pb = new Vector3((float)b.X, 0, (float)b.Z);
            var d = pb - pa;
            if (d.Length() < 0.5f) continue;
            var basis = new Basis(Vector3.Up, Mathf.Atan2(d.X, d.Z));
            body.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(2.6f, 2.6f, d.Length()) },
                Transform = new Transform3D(basis, (pa + pb) / 2 + Vector3.Up * ColonyKit.TubeY),
            });
        }
        if (links.Count > 0) root.AddChild(ColonyKit.Instance(ColonyKit.Links(links, c.Theme)));
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
        return root;
    }

    private static MeshInstance3D Glazed(ArrayMesh m)
    {
        var mi = new MeshInstance3D { Mesh = m };
        Avatar.MeshScratch.Paint(mi, Avatar.HumanMeshBuilder.Material(), ColonyKit.GlassMaterial());
        return mi;
    }

    /// <summary>A module's solid shapes in its own frame (the long axis along z).</summary>
    private static IEnumerable<(Transform3D At, Shape3D Shape)> Shapes(ColonyModule m)
    {
        float l = (float)m.Length, w = (float)m.Width, h = (float)m.Height;
        switch (m.Kind)
        {
            case ModuleKind.Hub:
            case ModuleKind.Stack:
                yield return (new Transform3D(Basis.Identity, new Vector3(0, h / 2, 0)), new CylinderShape3D { Radius = w / 2, Height = h });
                break;
            case ModuleKind.Can:
                yield return (new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, w / 2 + 0.9f, 0)),
                    new CylinderShape3D { Radius = w / 2, Height = l });
                yield return (new Transform3D(Basis.Identity, new Vector3(0, 0.45f, 0)), new BoxShape3D { Size = new Vector3(w * 0.8f, 0.9f, l * 0.7f) });
                break;
            case ModuleKind.Dome:
                yield return (new Transform3D(Basis.Identity, new Vector3(0, h * 0.4f, 0)), new CylinderShape3D { Radius = w / 2 * 0.9f, Height = h * 0.8f });
                break;
            case ModuleKind.Biodome:
                // the ring wall only for now (phase 3 walks in)
                yield return (new Transform3D(Basis.Identity, new Vector3(0, 0.6f, 0)), new CylinderShape3D { Radius = w / 2 + 0.3f, Height = 1.4f });
                break;
            case ModuleKind.Hangar:
                yield return (new Transform3D(Basis.Identity, new Vector3(0, h / 2 + 0.3f, 0)), new BoxShape3D { Size = new Vector3(w, h, l) });
                break;
        }
    }
}
