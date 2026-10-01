using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// <c>godot --headless --path . -- --connect host:port --netsmooth[,seconds[,label]] [--at E,N]</c>: how
/// smooth does a remote player look to this client? Picks the nearest remote <see cref="FootPlayer"/>
/// that is moving (faster than 5 m/s if anyone is, so a vehicle rather than a walker), records its rendered position every frame, and at the end prints (and writes to
/// <c>test_output/loadtest/&lt;label&gt;/netsmooth.txt</c>) per-frame step statistics against the
/// distance it should cover at its own speed: freezes (a frame that hardly moved), snaps (a frame
/// that jumped) and the acceleration a viewer would see. Frames are capped at 60 fps, since a
/// headless process otherwise renders thousands of frames between two network updates.
/// </summary>
public partial class NetSmoothProbe : Node
{
    private const double PickAfter = 4, PickWindow = 2;

    private readonly Node3D _players;
    private readonly double _seconds;
    private readonly string _label;

    private double _t;
    private Node3D? _target;
    private readonly Dictionary<Node3D, Vector3> _first = new();
    private readonly List<(double T, double Dt, Vector3 P)> _rec = new();
    private bool _done;
    private ulong _lastTicks;
    private string _targetInfo = "";

    public NetSmoothProbe(Node3D players, double seconds, string label)
    {
        _players = players;
        _seconds = seconds;
        _label = label;
    }

    public static (double Seconds, string Label)? ParseArgs()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--netsmooth"))
            {
                var parts = a.Split(',');
                double s = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 60;
                return (s, parts.Length > 2 ? parts[2] : "default");
            }
        return null;
    }

    public override void _Ready()
    {
        if (Engine.MaxFps == 0) Engine.MaxFps = 60;
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        // wall clock, not delta: the engine smooths a long frame's delta over the next ones,
        // which would hide exactly the hitches this is looking for
        ulong now = Time.GetTicksUsec();
        double dt = _lastTicks == 0 ? delta : (now - _lastTicks) / 1e6;
        _lastTicks = now;
        _t += dt;
        var local = _players.GetNodeOrNull<Node3D>(Multiplayer.GetUniqueId().ToString());
        if (_target == null)
        {
            Pick(local);
            return;
        }
        if (!GodotObject.IsInstanceValid(_target) || !_target.IsInsideTree()) { Finish("target left"); return; }
        _rec.Add((_t, dt, _target.GlobalPosition));
        FloorGap(_target);
        if (_t - _rec[0].T >= _seconds) Finish(null);
    }

    // the remote's height over the collision floor this client has under it (#125: does a
    // replicated car stay on a road carried by a retaining wall, or hover and sink?)
    private readonly List<double> _gaps = new();
    private int _noFloor;
    private readonly List<string> _gapRows = new() { "t,x,y,z,gap" };   // world frame
    private Godot.Collections.Array<Rid>? _exclude;

    private void FloorGap(Node3D target)
    {
        if (_exclude == null)
        {
            _exclude = new Godot.Collections.Array<Rid>();
            var stack = new Stack<Node>();
            stack.Push(target);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is CollisionObject3D body) _exclude.Add(body.GetRid());
                foreach (Node child in node.GetChildren()) stack.Push(child);
            }
        }
        var p = target.GlobalPosition;
        var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 2f, p - Vector3.Up * 6f);
        q.Exclude = _exclude;
        var hit = target.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count == 0) { _noFloor++; return; }
        _gaps.Add(p.Y - hit["position"].AsVector3().Y);
        _gapRows.Add(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F1},{2:F1},{3:F1},{4:F3}", _t, p.X, p.Y, p.Z, _gaps[^1]));
    }

    /// <summary>Watches every remote for PickWindow seconds, then takes the nearest one that moved.</summary>
    private void Pick(Node3D? local)
    {
        if (local == null || _t < PickAfter) return;
        if (_first.Count == 0)
        {
            foreach (var n in _players.GetChildren())
                if (n is FootPlayer p && p != local) _first[p] = p.GlobalPosition;
        }
        if (_t < PickAfter + PickWindow) return;

        // a vehicle if one is moving (the race pack is what the observer is parked beside), else
        // whoever is walking
        float best = float.MaxValue;
        bool fast = _first.Any(kv => GodotObject.IsInstanceValid(kv.Key) && kv.Key.GlobalPosition.DistanceTo(kv.Value) > 5f * PickWindow);
        foreach (var (p, from) in _first)
        {
            if (!GodotObject.IsInstanceValid(p) || p.GlobalPosition.DistanceTo(from) < (fast ? 5f : 1f) * PickWindow) continue;
            // flat: a client that has just connected may still be falling onto the terrain
            var off = p.GlobalPosition - local.GlobalPosition;
            float d = new Vector2(off.X, off.Z).Length();
            if (d < best) { best = d; _target = p; }
        }
        // up to 90 s: a scripted driver (--garagecheck drive) starts once this client has landed
        // from its spawn drop and has collision under the remote
        if (_target == null && _t > PickAfter + PickWindow + 90) Finish("no moving remote player found");
        else if (_target != null)
        {
            _targetInfo = $"{_target.Name} ({(CarCatalog.For(((FootPlayer)_target).Ride)?.Label ?? ((FootPlayer)_target).Ride.ToString())}) at {best:F0} m";
            GD.Print($"[netsmooth] watching {_targetInfo} for {_seconds:F0} s");
        }
    }

    private void Finish(string? problem)
    {
        _done = true;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"label {_label}");
        if (_targetInfo.Length > 0) sb.AppendLine("target " + _targetInfo);
        if (_rec.Count < 40)
            sb.AppendLine("RESULT no data: " + (problem ?? "too few frames"));
        else
        {
            if (problem != null) sb.AppendLine($"note: {problem} after {_rec[^1].T - _rec[0].T:F1} s, partial result");
            Analyse(sb, inv);
        }
        GD.Print(sb.ToString());
        try
        {
            string dir = ProjectSettings.GlobalizePath($"res://test_output/loadtest/{_label}");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "netsmooth.txt"), sb.ToString());
            File.WriteAllLines(Path.Combine(dir, "floorgap.csv"), _gapRows);
            // the raw track, for profiles the summary does not cover (a bump at a level crossing, #124)
            File.WriteAllLines(Path.Combine(dir, "netsmooth_track.csv"), _rec.Select(r =>
                string.Create(inv, $"{r.T:F4},{r.P.X:F3},{r.P.Y:F3},{r.P.Z:F3}")));
        }
        catch (Exception e) { GD.PushWarning($"[netsmooth] cannot write result: {e.Message}"); }
        GetTree().Quit();
    }

    private static double Pct(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        var s = new List<double>(v);
        s.Sort();
        return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
    }

    private void Analyse(StringBuilder sb, CultureInfo inv)
    {
        int n = _rec.Count;
        const int Half = 15;   // reference speed: the mean over +-15 frames (~0.5 s)
        var steps = new List<double>();
        var ratios = new List<double>();
        var accels = new List<double>();
        var speeds = new List<double>();
        int freezes = 0, snaps = 0, moving = 0;
        double longestFreeze = 0, run = 0;
        Vector3 prevVel = Vector3.Zero;
        for (int i = 1; i < n; i++)
        {
            double dt = _rec[i].Dt;
            double step = _rec[i].P.DistanceTo(_rec[i - 1].P);
            steps.Add(step);
            var vel = (_rec[i].P - _rec[i - 1].P) / (float)dt;
            if (i > 1) accels.Add((vel - prevVel).Length() / dt);
            prevVel = vel;

            int a = Math.Max(0, i - Half), b = Math.Min(n - 1, i + Half);
            double v = _rec[a].P.DistanceTo(_rec[b].P) / Math.Max(1e-6, _rec[b].T - _rec[a].T);
            speeds.Add(v);
            if (v <= 2) { run = 0; continue; }
            moving++;
            double expect = v * dt;
            ratios.Add(step / expect);
            if (step < 0.05 * expect)
            {
                freezes++;
                run += dt * 1000;
                longestFreeze = Math.Max(longestFreeze, run);
            }
            else run = 0;
            if (step > 3 * expect) snaps++;
        }
        double vMean = speeds.Count > 0 ? speeds.Average() : 0;
        double dtMean = (_rec[^1].T - _rec[0].T) / (n - 1);
        sb.AppendLine(string.Format(inv, "frames {0} over {1:F1} s (mean dt {2:F1} ms), moving frames {3}", n, _rec[^1].T - _rec[0].T, dtMean * 1000, moving));
        sb.AppendLine(string.Format(inv, "remote_speed_mean_ms {0:F2}", vMean));
        sb.AppendLine(string.Format(inv, "step_p50_m {0:F3}", Pct(steps, 0.5)));
        sb.AppendLine(string.Format(inv, "step_p99_m {0:F3}", Pct(steps, 0.99)));
        sb.AppendLine(string.Format(inv, "step_ratio_p50 {0:F2}  (step / (v*dt), moving frames)", Pct(ratios, 0.5)));
        sb.AppendLine(string.Format(inv, "step_ratio_p99 {0:F2}", Pct(ratios, 0.99)));
        sb.AppendLine(string.Format(inv, "accel_p99_ms2 {0:F1}", Pct(accels, 0.99)));
        sb.AppendLine(string.Format(inv, "freeze_frames {0}  (step < 5% of v*dt while v > 2 m/s)", freezes));
        sb.AppendLine(string.Format(inv, "freeze_longest_ms {0:F0}", longestFreeze));
        sb.AppendLine(string.Format(inv, "snap_frames {0}  (step > 3x v*dt)", snaps));
        sb.AppendLine(string.Format(inv, "floor_gap_m p1 {0:F3} p50 {1:F3} p99 {2:F3}  (remote height over the collision under it; {3} frames with no floor loaded)",
            Pct(_gaps, 0.01), Pct(_gaps, 0.5), Pct(_gaps, 0.99), _noFloor));
    }
}
