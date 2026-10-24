using System.Linq;
using System.Text;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// <c>--soakcheck [laps]</c> (#1112): what grows the more of the map is visited. Once the world is up it jumps round a
/// loop of places 2.5 km apart (Geneva's by default, the tiles a Quest test copy holds; <c>--at</c> is the start) and
/// back, lap after lap, and each time it is back at the start, settled, prints the frame and physics times, memory,
/// objects, nodes, bodies, draws, and the nodes under each branch of the world. At the same place, anything still
/// growing after the first lap is held from places left behind. Ends with the growth per branch since lap 1.
/// </summary>
public sealed partial class SoakCheck : Node
{
    /// <summary>The loop's places round the start, metres east and north.</summary>
    private static readonly (double E, double N)[] Loop =
    {
        (2500, 0), (2500, 2500), (0, 2500), (-2500, 2500), (-2500, 0), (-2500, -2500), (0, -2500), (2500, -2500),
    };
    /// <summary>Seconds at each place (tiles round it load), and at the start before measuring.</summary>
    private const double Stay = 20, Settle = 25, Measure = 5;

    private readonly int _laps = CmdArgs.Int("--soakcheck") ?? 6;
    private (double E, double N) _start;
    private int _lap, _stop = -1;
    private double _clock;
    private bool _measuring;
    private double _sumProcess, _sumPhysics, _sumFrame;
    private int _frames;
    private Dictionary<string, int>? _firstBranches;
    private readonly StringBuilder _report = new();

    public SoakCheck() => Name = "SoakCheck";

    public static bool Requested() => CmdArgs.Has("--soakcheck");

    public override void _Ready()
    {
        _start = SpawnPoint.ParseLv95("--at") ?? (2500300, 1118450);
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Process(double delta)
    {
        if (Teleporter.Instance is not { } teleporter || _lap > _laps) return;
        _clock += delta;
        if (_stop < 0)
        {
            // at the start: settle, then measure for a few seconds
            if (!_measuring && _clock >= Settle) { _measuring = true; _clock = 0; _sumProcess = _sumPhysics = _sumFrame = 0; _frames = 0; }
            if (_measuring)
            {
                _sumProcess += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
                _sumPhysics += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
                _sumFrame += delta * 1000;
                _frames++;
                if (_clock >= Measure)
                {
                    Report();
                    _measuring = false;
                    _lap++;
                    if (_lap > _laps) { Finish(); return; }
                    Jump(teleporter, 0);
                }
            }
            return;
        }
        if (_clock < Stay) return;
        if (_stop + 1 < Loop.Length) Jump(teleporter, _stop + 1);
        else
        {
            teleporter.TeleportTo(_start.E, _start.N, "soak start");
            _stop = -1;
            _clock = 0;
        }
    }

    private void Jump(Teleporter teleporter, int stop)
    {
        _stop = stop;
        _clock = 0;
        teleporter.TeleportTo(_start.E + Loop[stop].E, _start.N + Loop[stop].N, $"soak {stop}");
    }

    private void Report()
    {
        var root = GetTree().Root;
        var branches = Branches(root);
        _firstBranches ??= branches;
        double bodies = root.FindChildren("*", nameof(CollisionObject3D), true, false).Count;
        string line = string.Join(", ", new[]
        {
            Inv($"[soak] lap {_lap}: frame {_sumFrame / _frames:F1} ms (process {_sumProcess / _frames:F1}, physics {_sumPhysics / _frames:F1})"),
            Inv($"static {Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1e6:F0} MB"),
            Inv($"video {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1e6:F0} MB"),
            Inv($"objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}"),
            Inv($"resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount):F0}"),
            Inv($"nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):F0}, orphans {Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount):F0}"),
            Inv($"bodies {bodies:F0}, draws {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):F0}"),
            Inv($"physics active {Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects):F0}"),
        });
        GD.Print(line);
        _report.AppendLine(line);
        // the nodes out of the tree and never freed, by class and name: whatever left them behind
        var orphans = new Dictionary<string, int>();
        foreach (ulong id in Node.GetOrphanNodeIds())
            if (InstanceFromId(id) is Node orphan)
            {
                string key = $"{orphan.GetClass()} {System.Text.RegularExpressions.Regex.Replace(orphan.Name.ToString(), "[0-9_@]+", "")}"
                    + (orphan.GetParent() == null ? " (root)" : "");
                orphans[key] = orphans.GetValueOrDefault(key) + 1;
            }
        GD.Print($"[soak]   orphan ids listed: {Node.GetOrphanNodeIds().Count}");
        if (_lap == _laps) Node.PrintOrphanNodes();
        foreach (var (key, count) in orphans.OrderByDescending(kv => kv.Value).Take(12))
            GD.Print(System.FormattableString.Invariant($"[soak]   orphan {count,6} {key}"));
        foreach (var (name, count) in branches.OrderByDescending(kv => kv.Value).Take(14))
            GD.Print(System.FormattableString.Invariant($"[soak]   {count,7} {name} ({count - _firstBranches.GetValueOrDefault(name):+0;-0;0} since lap 0)"));
    }

    /// <summary>Nodes under each child of the world (and of the root's other children), by name without numbers.</summary>
    private static Dictionary<string, int> Branches(Node root)
    {
        var counts = new Dictionary<string, int>();
        foreach (var top in root.GetChildren())
        {
            var world = top.FindChild("World", true, false) ?? top;
            foreach (var child in world.GetChildren())
            {
                string name = System.Text.RegularExpressions.Regex.Replace(child.Name.ToString(), "[0-9_@]+", "");
                counts[name] = counts.GetValueOrDefault(name) + 1 + Count(child);
            }
        }
        return counts;
    }

    private static string Inv(System.FormattableString s) => System.FormattableString.Invariant(s);

    private static int Count(Node node)
    {
        int n = 0;
        foreach (var c in node.GetChildren()) n += 1 + Count(c);
        return n;
    }

    private void Finish()
    {
        GD.Print($"[soak] RESULT: done, {_laps} laps\n{_report}");
        GetTree().Quit();
    }
}
