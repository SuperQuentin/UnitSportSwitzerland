using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Styles;

namespace UnitSport.World.Ropeways;

/// <summary>
/// The lifts' carriers (#912), boxes in vertex colour for the prop material. Each hangs from its
/// origin, the grip on the cable, and faces −Z, up the line:
/// <list type="bullet">
/// <item>a four-seat chair 2.3 m below;</item>
/// <item>a gondola cabin, its floor 3.2 m below;</item>
/// <item>a T-bar: a 1 m rod, stretched to the ground per carrier, and the bar, placed at the ground;</item>
/// <item>a rope tow's handle.</item>
/// </list>
/// Cached: one mesh per part, shared by every line.
/// </summary>
public static class RopewayMeshBuilder
{
    public enum Part { Chair, Cabin, Rod, Bar, Handle }

    private static readonly Dictionary<Part, ArrayMesh> Cache = new();

    private static readonly Color Steel = new(0.32f, 0.33f, 0.35f);
    private static readonly Color Seat = new(0.12f, 0.28f, 0.62f);
    private static readonly Color Body = new(0.78f, 0.12f, 0.10f);
    private static readonly Color Glass = new(0.55f, 0.70f, 0.82f);
    private static readonly Color Roof = new(0.85f, 0.85f, 0.83f);
    private static readonly Color Yellow = new(0.95f, 0.75f, 0.12f);

    public static ArrayMesh Mesh(Part part)
    {
        if (Cache.TryGetValue(part, out var cached)) return cached;
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        void Box(Vector3 centre, Vector3 size, Color c)
        {
            var h = size * 0.5f;
            st.SetColor(c with { A = 0 });
            // six faces, wound clockwise seen from outside (Godot's front)
            Vector3[] n = [Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward];
            foreach (var f in n)
            {
                var u = Mathf.Abs(f.Y) > 0.5f ? Vector3.Right : Vector3.Up;
                var v = f.Cross(u);
                var o = centre + f * h;
                Vector3 Corner(float a, float b) => o + u * (a * Mathf.Abs(u.Dot(h))) + v * (b * Mathf.Abs(v.Dot(h)));
                Vector3 p0 = Corner(-1, -1), p1 = Corner(1, -1), p2 = Corner(1, 1), p3 = Corner(-1, 1);
                st.AddVertex(p0); st.AddVertex(p2); st.AddVertex(p1);
                st.AddVertex(p0); st.AddVertex(p3); st.AddVertex(p2);
            }
        }
        switch (part)
        {
            case Part.Chair:
                Box(new Vector3(0, -1.15f, 0), new Vector3(0.07f, 2.3f, 0.07f), Steel);          // hanger
                Box(new Vector3(0, -2.3f, 0.15f), new Vector3(2.4f, 0.08f, 0.55f), Seat);         // bench
                Box(new Vector3(0, -1.95f, 0.42f), new Vector3(2.4f, 0.6f, 0.07f), Seat);         // back
                Box(new Vector3(0, -1.75f, -0.45f), new Vector3(2.3f, 0.05f, 0.05f), Steel);      // safety bar
                Box(new Vector3(0, -2.75f, -0.35f), new Vector3(2.2f, 0.05f, 0.25f), Steel);      // footrest
                Box(new Vector3(-1.2f, -2.0f, 0f), new Vector3(0.05f, 0.7f, 0.05f), Steel);       // side frame
                Box(new Vector3(1.2f, -2.0f, 0f), new Vector3(0.05f, 0.7f, 0.05f), Steel);
                break;
            case Part.Cabin:
                Box(new Vector3(0, -0.5f, 0), new Vector3(0.08f, 1.0f, 0.08f), Steel);            // hanger
                Box(new Vector3(0, -1.05f, 0), new Vector3(1.9f, 0.12f, 1.7f), Roof);             // roof
                Box(new Vector3(0, -1.55f, 0), new Vector3(1.85f, 0.9f, 1.65f), Glass);           // windows
                Box(new Vector3(0, -2.6f, 0), new Vector3(1.9f, 1.2f, 1.7f), Body);               // body
                Box(new Vector3(0, -3.22f, 0), new Vector3(1.8f, 0.06f, 1.6f), Steel);            // floor
                break;
            case Part.Rod:
                Box(new Vector3(0, -0.5f, 0), new Vector3(0.05f, 1f, 0.05f), Steel);              // 1 m, stretched
                break;
            case Part.Bar:
                Box(new Vector3(0, 0.75f, 0), new Vector3(1.0f, 0.06f, 0.06f), Yellow);           // the T, at hip height
                Box(new Vector3(0, 0.9f, 0), new Vector3(0.09f, 0.3f, 0.09f), Yellow);          // a sleeve round the rod, clear of its faces
                break;
            default:
                Box(new Vector3(0, -0.15f, 0), new Vector3(0.12f, 0.3f, 0.12f), Yellow);
                break;
        }
        st.GenerateNormals();
        ShowcaseTrace.Mark();
        var mesh = st.Commit();
        Cache[part] = mesh;
        return mesh;
    }

    /// <summary>
    /// A lift station (#915), its origin on the snow at the middle of the platform, facing -Z out
    /// along the line: four posts at the corners, a roof <paramref name="height"/> up, and the bull
    /// wheel housing under the back of the roof. Open on every side.
    /// </summary>
    public static ArrayMesh Station(float width, float length, float height)
    {
        var st = new SurfaceTool();
        st.Begin(Godot.Mesh.PrimitiveType.Triangles);
        var box = BoxWriter(st);
        float hw = width / 2, hl = length / 2;
        foreach (var (x, z) in new[] { (-hw, -hl), (hw, -hl), (-hw, hl), (hw, hl) })
            box(new Vector3(x, height / 2, z), new Vector3(0.3f, height, 0.3f), Steel);
        box(new Vector3(0, height + 0.15f, 0), new Vector3(width + 1f, 0.3f, length + 1f), Roof);
        box(new Vector3(0, height - 0.6f, hl - 1.2f), new Vector3(Mathf.Min(width - 1f, 4f), 1.0f, 2f), Body);   // the bull wheel housing
        box(new Vector3(0, 0.1f, 0), new Vector3(width - 0.6f, 0.2f, length - 0.6f), Roof);                       // the platform
        st.GenerateNormals();
        ShowcaseTrace.Mark();
        return st.Commit();
    }

    /// <summary>A box writer into <paramref name="st"/>: centre, size, colour, wound for the front faces Godot draws.</summary>
    private static Action<Vector3, Vector3, Color> BoxWriter(SurfaceTool st) => (centre, size, c) =>
    {
        var h = size * 0.5f;
        st.SetColor(c with { A = 0 });
        Vector3[] faces = [Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward];
        foreach (var f in faces)
        {
            var u = Mathf.Abs(f.Y) > 0.5f ? Vector3.Right : Vector3.Up;
            var v = f.Cross(u);
            var o = centre + f * h;
            Vector3 Corner(float a, float b) => o + u * (a * Mathf.Abs(u.Dot(h))) + v * (b * Mathf.Abs(v.Dot(h)));
            Vector3 p0 = Corner(-1, -1), p1 = Corner(1, -1), p2 = Corner(1, 1), p3 = Corner(-1, 1);
            st.AddVertex(p0); st.AddVertex(p2); st.AddVertex(p1);
            st.AddVertex(p0); st.AddVertex(p3); st.AddVertex(p2);
        }
    };

    [Showcase("World", "Ropeways")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        foreach (var part in new[] { Part.Chair, Part.Cabin, Part.Handle })
        {
            var p = part;
            // hung from the cable: lifted so the viewer's ground does not swallow it
            yield return (p.ToString(), () => new MeshInstance3D { Mesh = Mesh(p), Position = new Vector3(0, 3.4f, 0), MaterialOverride = StyleKit.Material(MaterialRole.Prop) });
        }
        yield return ("Station", () => new MeshInstance3D { Mesh = Station(7.5f, 9f, 4f), MaterialOverride = StyleKit.Material(MaterialRole.Prop) });
        yield return ("T-bar", () =>
        {
            var root = new Node3D { Position = new Vector3(0, 6f, 0) };
            root.AddChild(new MeshInstance3D { Mesh = Mesh(Part.Rod), Scale = new Vector3(1, 5, 1), MaterialOverride = StyleKit.Material(MaterialRole.Prop) });
            root.AddChild(new MeshInstance3D { Mesh = Mesh(Part.Bar), Position = new Vector3(0, -5, 0), MaterialOverride = StyleKit.Material(MaterialRole.Prop) });
            return root;
        });
    }
}
