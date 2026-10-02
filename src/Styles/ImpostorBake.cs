using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Styles;

/// <summary>
/// <c>--bake-impostors</c> (windowed: a headless run renders nothing): pictures each catalogue
/// tree for the realistic billboards (<c>shaders/body/tree.gdshaderinc</c>, <c>impostor()</c>) and
/// writes them next to the models, then quits with RESULT PASS/FAIL. Run it again whenever
/// <c>tools/trees</c> regenerates the trees.
///
/// <para>
/// Two pictures a tree, unlit colour with the leaf cards' alpha: side on (512x256, the unit
/// tree's x -1..1 by y 0..1) and from above (256x256, x and z -1..1, image down = +z). The
/// billboard lights them itself with a rounded-crown normal.
/// </para>
/// </summary>
public partial class ImpostorBake : Node
{
    public static bool Requested => CmdArgs.Has("--bake-impostors");

    private const string Dir = "res://assets/realistic/trees/";

    public override async void _Ready()
    {
        int failures = 0;
        foreach (var id in new[] { ModelCatalog.TreeConifer, ModelCatalog.TreeBroadleaf })
        {
            var mesh = ModelCatalog.Resolve(VisualStyle.RealisticLow, id) is { } found
                ? Load(found.Path) : null;
            if (mesh == null)
            {
                GD.PrintErr($"[bake] FAIL: no model for {id}");
                failures++;
                continue;
            }
            string name = id == ModelCatalog.TreeConifer ? "conifer" : "broadleaf";
            failures += await Picture(mesh, id, new Vector2I(512, 256), side: true, $"{name}_side.png");
            failures += await Picture(mesh, id, new Vector2I(256, 256), side: false, $"{name}_top.png");
        }
        GD.Print($"RESULT {(failures == 0 ? "PASS" : $"FAIL ({failures})")}");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    private static ArrayMesh? Load(string path)
    {
        var scene = GD.Load<PackedScene>(path).Instantiate();
        ArrayMesh? mesh = null;
        foreach (var node in scene.FindChildren("*", "MeshInstance3D", true, false))
            if (((MeshInstance3D)node).Mesh is ArrayMesh m) { mesh = m; break; }
        scene.Free();
        return mesh;
    }

    private async Task<int> Picture(ArrayMesh mesh, string id, Vector2I size, bool side, string file)
    {
        var vp = new SubViewport
        {
            Size = size,
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        var instance = new MeshInstance3D { Mesh = mesh };
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            instance.SetSurfaceOverrideMaterial(s, Unlit(id, leaves: IsLeaves(mesh, s)));
        var cam = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            // the vertical extent: y 0..1 side on, z -1..1 from above
            Size = side ? 1f : 2f,
            Current = true,
        };
        if (side)
        {
            cam.Position = new Vector3(0, 0.5f, 4f);
        }
        else
        {
            cam.Position = new Vector3(0, 3f, 0);
            cam.Rotation = new Vector3(-Mathf.Pi / 2f, 0, 0);
        }
        vp.AddChild(instance);
        vp.AddChild(cam);
        AddChild(vp);
        for (int f = 0; f < 4; f++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = vp.GetTexture().GetImage();
        vp.QueueFree();
        var error = image.SavePng(ProjectSettings.GlobalizePath(Dir + file));
        GD.Print($"[bake] {file} {image.GetWidth()}x{image.GetHeight()}: {error}");
        return error == Error.Ok ? 0 : 1;
    }

    /// <summary>Whether a catalogue tree's surface is its leaf cards (the generator names them).</summary>
    public static bool IsLeaves(ArrayMesh mesh, int surface)
    {
        string name = mesh.SurfaceGetName(surface);
        if (name.Length > 0) return name.Contains("leaves", System.StringComparison.OrdinalIgnoreCase);
        return mesh.SurfaceGetMaterial(surface) is { } m ? m.ResourceName.Contains("leaves") : surface == 1;
    }

    private static StandardMaterial3D Unlit(string id, bool leaves)
    {
        bool conifer = id == ModelCatalog.TreeConifer;
        string bark = conifer ? "pine" : "oak";
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = GD.Load<Texture2D>(Dir + (leaves ? (conifer ? "leaves_pine.png" : "leaves_ash.png") : $"bark_{bark}_color.jpg")),
            Uv1Scale = leaves || conifer ? Vector3.One : new Vector3(0.5f, 5f, 1f),
            Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
            AlphaScissorThreshold = 0.45f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }
}
