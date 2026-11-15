using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The colonies' lights seen from afar (#1946, docs/plans/moon-surface.md section 9): every staffed colony's windows,
/// its pads' edge lights, the runway's edge and end lights and the outposts' floods as points of light on the Moon's globe,
/// one mesh for them all in the Moon's fixed frame (<see cref="BodyGlobe.AddBodyFixed"/>: from lunar orbit with the anchor
/// on the Moon, or from anywhere the globe is drawn). A point is a quad the shader (<c>colony_lights.gdshader</c>) lays a
/// few pixels across whatever the distance, lit only where the sun is down at it (the plan's night side), brighter nearer,
/// gone within 10 km where the colony itself is built. The empty colonies: one failing red light in the pit, the Cold War
/// base's dome and a few windows still on. Built once from the plans; the sun's direction written when it moves.
/// </summary>
public partial class ColonyLights : Node3D
{
    private static readonly StringName SunDir = "sun_dir";

    private readonly BodyGlobe _globe;
    private MeshInstance3D? _mesh;
    private ShaderMaterial? _material;
    private Vector3 _sunWritten;

    /// <summary>The points built (the check's count).</summary>
    public int Count { get; private set; }

    public static ColonyLights? Active { get; private set; }

    public ColonyLights(BodyGlobe globe)
    {
        _globe = globe;
        Name = "ColonyLights";
    }

    public override void _EnterTree() => Active = this;

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
    }

    private static Color C(string hex) => new(hex);

    private static readonly Color Window = C("ffd98a"), PadRed = C("ff4a30"), Edge = C("f2f6ff"), Flood = C("e8f0ff"), Emergency = C("ff2a18"),
        Dome = C("ffe2a8");

    /// <summary>One point: its place in the Moon's fixed frame (metres), its up, its colour, its strength 0..1.</summary>
    public readonly record struct Point(Vector3 At, Vector3 Up, Color Colour, float Strength);

    public override void _Process(double delta)
    {
        if (_mesh == null)
        {
            if (_globe.Ground is not { } ground) return;
            Build(ground.Heights);
        }
        bool drawn = _globe.Drawn;
        if (_mesh!.Visible != drawn) _mesh.Visible = drawn;
        if (!drawn) return;
        var sun = _globe.Sun;
        if (sun != Vector3.Zero && sun.DistanceSquaredTo(_sunWritten) > 1e-8f)
        {
            _sunWritten = sun;
            _material!.SetShaderParameter(SunDir, sun);
        }
    }

    private void Build(BodyHeights heights)
    {
        var points = Points(heights);
        Count = points.Count;
        var verts = new Vector3[points.Count * 4];
        var normals = new Vector3[points.Count * 4];
        var colours = new Color[points.Count * 4];
        var uvs = new Vector2[points.Count * 4];
        var index = new int[points.Count * 6];
        ReadOnlySpan<Vector2> corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, 1)];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            for (int k = 0; k < 4; k++)
            {
                verts[i * 4 + k] = p.At;
                normals[i * 4 + k] = p.Up;
                colours[i * 4 + k] = new Color(p.Colour.R, p.Colour.G, p.Colour.B, p.Strength);
                uvs[i * 4 + k] = corners[k];
            }
            index[i * 6] = i * 4; index[i * 6 + 1] = i * 4 + 1; index[i * 6 + 2] = i * 4 + 2;
            index[i * 6 + 3] = i * 4; index[i * 6 + 4] = i * 4 + 2; index[i * 6 + 5] = i * 4 + 3;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colours;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = index;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/colony_lights.gdshader") };
        _mesh = new MeshInstance3D
        {
            Name = "Lights", Mesh = mesh, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the shader places every point: never culled by a guess at the box (the root carries them far)
            CustomAabb = new Aabb(new Vector3(-3e6f, -3e6f, -3e6f), new Vector3(6e6f, 6e6f, 6e6f)), IgnoreOcclusionCulling = true,
            Visible = false,
        };
        var holder = new Node3D { Name = "ColonyLightsFrame" };
        holder.AddChild(_mesh);
        _globe.AddBodyFixed(holder);
        GD.Print($"[moon] the colonies' lights: {Count} points");
    }

    /// <summary>The points of every colony and outpost, from their plans, on the ground (with the stamps).</summary>
    public static List<Point> Points(BodyHeights heights)
    {
        var list = new List<Point>();
        foreach (var c in MoonColonies.All)
        {
            var frame = new EnuFrame(Ellipsoid.Moon.ToGeodetic(heights.Ground(c.Normal)), Ellipsoid.Moon);
            var plan = ColonyPlan.For(c);
            void Add(double x, double y, double z, Color col, float strength)
            {
                // the colony's frame: x east, y up, z south
                var o = frame.Origin;
                var p = new EcefPos(o.X + frame.East.X * x + frame.Up.X * y - frame.North.X * z,
                    o.Y + frame.East.Y * x + frame.Up.Y * y - frame.North.Y * z, o.Z + frame.East.Z * x + frame.Up.Z * y - frame.North.Z * z);
                list.Add(new Point(new Vector3((float)p.X, (float)p.Y, (float)p.Z), new Vector3((float)frame.Up.X, (float)frame.Up.Y, (float)frame.Up.Z), col, strength));
            }
            if (c.Theme == ColonyTheme.Abandoned)
            {
                Add(0, 4, 0, Emergency, 0.35f);
                continue;
            }
            bool cold = c.Theme == ColonyTheme.ColdWar;
            foreach (var m in plan.Modules)
            {
                if (m.Kind == ModuleKind.Biodome)
                {
                    for (int k = 0; k < 6; k++)
                    {
                        double a = k * Math.Tau / 6;
                        Add(m.X + Math.Sin(a) * m.Width * 0.25, m.Width * 0.3, m.Z + Math.Cos(a) * m.Width * 0.25, Dome, 0.8f);
                    }
                    continue;
                }
                // a module's windows: a few points round it at their height; the Cold War base's only some still lit
                int n = m.Kind == ModuleKind.Hub ? 6 : m.Kind == ModuleKind.Hangar ? 3 : 2;
                for (int k = 0; k < n; k++)
                {
                    if (cold && (m.Id + k) % 3 != 0) continue;
                    double a = k * Math.Tau / n + m.Yaw;
                    double r = Math.Max(m.Length, m.Width) / 2;
                    Add(m.X + Math.Sin(a) * r, 3, m.Z - Math.Cos(a) * r, Window, cold ? 0.55f : 0.9f);
                }
            }
            if (cold) continue;
            foreach (var pad in plan.Pads)
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.Tau / 8;
                    Add(pad.X + Math.Sin(a) * pad.Radius, 0.3, pad.Z + Math.Cos(a) * pad.Radius, PadRed, 0.8f);
                }
        }
        foreach (var o in MoonOutposts.All)
        {
            var frame = new EnuFrame(Ellipsoid.Moon.ToGeodetic(heights.Ground(o.Normal)), Ellipsoid.Moon);
            var probe = o.Probe;
            void AddAt(double e, double n, double up, Color col, float strength)
            {
                var g = heights.Ground(MoonSites.Offset(probe, e, n));
                double r = Math.Sqrt(g.X * g.X + g.Y * g.Y + g.Z * g.Z);
                var u = new Vector3((float)(g.X / r), (float)(g.Y / r), (float)(g.Z / r));
                list.Add(new Point(new Vector3((float)(g.X + u.X * up), (float)(g.Y + u.Y * up), (float)(g.Z + u.Z * up)), u, col, strength));
            }
            double h = o.Heading * Math.PI / 180;
            switch (o.Kind)
            {
                case OutpostKind.Runway:
                    // edge lights every 150 m both sides (the plan's are every 60 m: these are what a pilot reads from afar)
                    for (double s = 0; s <= o.LengthM + 0.1; s += 150)
                        foreach (double side in new[] { -o.RadiusM - 1.5, o.RadiusM + 1.5 })
                            AddAt(Math.Sin(h) * s + Math.Cos(h) * side, Math.Cos(h) * s - Math.Sin(h) * side, 0.4, Edge, 0.8f);
                    break;
                case OutpostKind.IceMine:
                    for (int k = 0; k < 4; k++)
                    {
                        double a = h + Math.PI / 4 + k * Math.PI / 2;
                        AddAt(Math.Sin(a) * 34, Math.Cos(a) * 34, 12, Flood, 1f);
                    }
                    break;
                case OutpostKind.MassDriver:
                    AddAt(0, 0, 4, Window, 0.7f);
                    AddAt(Math.Sin(h) * o.LengthM, Math.Cos(h) * o.LengthM, 4, PadRed, 0.7f);
                    break;
                case OutpostKind.RadioTelescope:
                    AddAt(-Math.Cos(h) * 150, Math.Sin(h) * 150, 3, Window, 0.6f);
                    break;
            }
        }
        return list;
    }
}
