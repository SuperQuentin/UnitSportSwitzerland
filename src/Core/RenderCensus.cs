using System.Linq;
using System.Text;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Which parts of the scene the triangles come from (#1112): <c>--rendercensus [seconds]</c> walks the tree
/// once that long into the run and prints the visible meshes' triangles summed by the first two names of
/// their path under the root, biggest first. Rough on purpose: whole meshes, ignoring culling and LOD
/// ranges, which is enough to say whether the terrain, the horizon or the props are the bulk.
/// </summary>
public static partial class RenderCensus
{
    public static void Install(Node root)
    {
        // "--overdraw": the window draws how many times each pixel was shaded (Godot's overdraw view)
        // (and the headset's, which comes up a frame later)
        if (CmdArgs.Has("--overdraw"))
        {
            root.GetViewport().DebugDraw = Viewport.DebugDrawEnum.Overdraw;
            root.GetTree().CreateTimer(1).Timeout += () =>
            {
                if (XR.XrSession.Rig is { } rig) rig.HeadsetView.DebugDraw = Viewport.DebugDrawEnum.Overdraw;
            };
        }
        // "--hidenodes A,B": hides every node whose name contains one of them, again each second
        // (tiles come and go), to measure what a part of the scene costs the GPU
        if (CmdArgs.Value("--hidenodes") is { } list)
        {
            string[] names = list.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var timer = new Godot.Timer { WaitTime = 1, Autostart = true };
            timer.Timeout += () => Hide(root.GetTree().Root, names);
            root.AddChild(timer);
        }
        // "--hidetoggle A,B": those nodes hidden and shown again in turns of 5 s, the headset's GPU time averaged
        // per state: what a part of the scene costs, measured in one run (between runs a Quest varies by 2 ms).
        // Besides node names: @shadows (the sun's shadows off), @sky (a plain colour behind), @msaa (the headset's off)
        if (CmdArgs.Value("--hidetoggle") is { } toggled)
            root.AddChild(new HideToggle(toggled.Split(',', StringSplitOptions.RemoveEmptyEntries)));
        // "--hideui": every 2D layer hidden, each second, to measure what the 2D costs a headset
        if (CmdArgs.Has("--hideui"))
        {
            var timer = new Godot.Timer { WaitTime = 1, Autostart = true };
            timer.Timeout += () =>
            {
                foreach (var l in root.GetTree().Root.FindChildren("*", nameof(CanvasLayer), true, false).OfType<CanvasLayer>())
                    l.Visible = false;
            };
            root.AddChild(timer);
        }
        double? at = CmdArgs.Double("--rendercensus");
        if (at == null && !CmdArgs.Has("--rendercensus")) return;
        root.GetTree().CreateTimer(at ?? 30).Timeout += () => Print(root.GetTree().Root);
    }

    private static void Hide(Node node, string[] names, bool hide = true)
    {
        if (node is Node3D n3 && n3.Visible == hide)
            foreach (var name in names)
                if (node.Name.ToString().Contains(name)) { n3.Visible = !hide; return; }
        foreach (var c in node.GetChildren()) Hide(c, names, hide);
    }

    private sealed partial class HideToggle(string[] names) : Node
    {
        private Viewport.Msaa _msaa;
        private Godot.Environment.BGMode _background;

        private void Switch(SubViewport view, bool off)
        {
            var root = GetTree().Root;
            if (names.Contains("@shadows"))
                foreach (var light in root.FindChildren("*", nameof(DirectionalLight3D), true, false).OfType<DirectionalLight3D>())
                    light.ShadowEnabled = !off;
            if (names.Contains("@sky") && view.FindWorld3D()?.Environment is { } env)
            {
                if (off) (_background, env.BackgroundMode) = (env.BackgroundMode, Godot.Environment.BGMode.Color);
                else env.BackgroundMode = _background;
            }
            if (names.Contains("@msaa"))
            {
                if (off) (_msaa, view.Msaa3D) = (view.Msaa3D, Viewport.Msaa.Disabled);
                else view.Msaa3D = _msaa;
            }
        }

        private const double Turn = 5, Settle = 1;
        private double _clock;
        private bool _hidden;
        private readonly double[] _sum = new double[2];
        private readonly int[] _count = new int[2];
        private int _turns;

        public override void _Process(double delta)
        {
            if (XR.XrSession.Rig?.HeadsetView is not { } view) return;
            _clock += delta;
            int state = _hidden ? 1 : 0;
            if (_clock > Settle)
            {
                _sum[state] += RenderingServer.ViewportGetMeasuredRenderTimeGpu(view.GetViewportRid());
                _count[state]++;
            }
            if (_clock < Turn) return;
            _clock = 0;
            _hidden = !_hidden;
            // tiles come and go: the whole tree each turn
            Hide(GetTree().Root, names, _hidden);
            Switch(view, _hidden);
            if (++_turns % 4 == 0 && _count[0] > 0 && _count[1] > 0)
                GD.Print(System.FormattableString.Invariant(
                    $"[hidetoggle] {string.Join(',', names)}: shown {_sum[0] / _count[0]:F2} ms, hidden {_sum[1] / _count[1]:F2} ms, saves {_sum[0] / _count[0] - _sum[1] / _count[1]:F2} ms ({_count[0]}/{_count[1]} frames)"));
        }
    }

    private static void Print(Node root)
    {
        var sums = new Dictionary<string, (long Tris, int Nodes)>();
        Walk(root, root, sums);
        var sb = new StringBuilder("[census] triangles of visible meshes, by branch:\n");
        foreach (var (key, v) in sums.OrderByDescending(kv => kv.Value.Tris).Take(25))
            sb.Append(System.FormattableString.Invariant($"  {v.Tris / 1000.0,9:F0}k  {v.Nodes,6} nodes  {key}\n"));
        sb.Append(System.FormattableString.Invariant($"  total {sums.Where(kv => !kv.Key.StartsWith('*')).Sum(kv => kv.Value.Tris) / 1e6:F2} M"));
        // what the physics engine carries each tick: bodies by kind (awake or asleep), areas that watch overlaps
        var phys = new Dictionary<string, int>();
        foreach (var n in root.FindChildren("*", nameof(CollisionObject3D), true, false))
        {
            string kind = n switch
            {
                RigidBody3D rb => rb.Sleeping || rb.Freeze ? $"{n.GetClass()} asleep/frozen" : $"{n.GetClass()} awake",
                Area3D a => a.Monitoring || a.Monitorable ? $"Area3D monitoring={a.Monitoring} monitorable={a.Monitorable}" : "Area3D idle",
                _ => n.GetClass(),
            };
            phys[kind] = phys.GetValueOrDefault(kind) + 1;
        }
        foreach (var (kind, count) in phys.OrderByDescending(kv => kv.Value))
            sb.Append(System.FormattableString.Invariant($"\n  physics {count,5}  {kind}"));
        // where the areas and the moving bodies are
        var areas = new Dictionary<string, int>();
        foreach (var n in root.FindChildren("*", nameof(Area3D), true, false).Concat(root.FindChildren("*", nameof(AnimatableBody3D), true, false)))
        {
            string owner = System.Text.RegularExpressions.Regex.Replace(n.GetParent()?.Name.ToString() ?? "", "[0-9_@]+", "") + "/" + System.Text.RegularExpressions.Regex.Replace(n.Name.ToString(), "[0-9_@]+", "");
            areas[owner] = areas.GetValueOrDefault(owner) + 1;
        }
        foreach (var (owner, count) in areas.OrderByDescending(kv => kv.Value).Take(10))
            sb.Append(System.FormattableString.Invariant($"\n  area {count,5}  {owner}"));
        // every other picture of the world drawn each frame (portals, mirrors, cameras): each is a whole render
        foreach (var v in root.FindChildren("*", nameof(SubViewport), true, false).OfType<SubViewport>())
            if (!v.Disable3D && v.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled)
                sb.Append(System.FormattableString.Invariant($"\n  3D view {root.GetPathTo(v)} {v.Size.X}x{v.Size.Y} {v.RenderTargetUpdateMode} msaa {v.Msaa3D}"));
        GD.Print(sb.ToString());
    }

    private static void Walk(Node node, Node root, Dictionary<string, (long, int)> sums)
    {
        if (node is Node3D n3 && !n3.Visible) return;
        if (node is CanvasItem) return;
        long tris = node switch
        {
            MultiMeshInstance3D mm when mm.Multimesh is { Mesh: { } m } multi =>
                Triangles(m) * (multi.VisibleInstanceCount >= 0 ? multi.VisibleInstanceCount : multi.InstanceCount),
            MeshInstance3D mi when mi.Mesh is { } m => Triangles(m),
            _ => 0,
        };
        if (tris > 0)
        {
            var path = root.GetPathTo(node).ToString().Split('/');
            string key = string.Join('/', path.Take(Math.Min(CmdArgs.Int("--rendercensus", 2) ?? 3, path.Length - 1)));
            sums.TryGetValue(key, out var v);
            sums[key] = (v.Item1 + tris, v.Item2 + 1);
            // and by what the mesh is, whatever tile it is in: its own name without numbers
            string kind = "* " + System.Text.RegularExpressions.Regex.Replace(node.Name.ToString(), "[0-9_@]+", "") + $" ({node.GetClass()})";
            sums.TryGetValue(kind, out v);
            sums[kind] = (v.Item1 + tris, v.Item2 + 1);
        }
        foreach (var c in node.GetChildren()) Walk(c, root, sums);
    }

    private static long Triangles(Mesh mesh)
    {
        long t = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh is ArrayMesh am)
            {
                int idx = am.SurfaceGetArrayIndexLen(s);
                t += (idx > 0 ? idx : am.SurfaceGetArrayLen(s)) / 3;
            }
            else t += mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].As<Vector3[]>().Length / 3;
        }
        return t;
    }
}
