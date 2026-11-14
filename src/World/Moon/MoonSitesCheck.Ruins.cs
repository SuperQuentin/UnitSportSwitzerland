using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

public partial class MoonSitesCheck
{
    /// <summary>
    /// <c>--ruinscheck</c> (#1944), after the last named empty colony (<c>--moonsites lavatube:close --ruinscheck</c>): the
    /// walker steps in, reads every terminal (E at each, every page, back to its title), and at the pit colony stands on the
    /// tower's bridge and walks out onto the rim. Fails if a colony has no terminal, a page does not show, or the walker does
    /// not end on the rim's ground. Windowed: <c>terminal.png</c> (the first log's first page) and <c>tower.png</c>.
    /// </summary>
    public static bool RuinsCheck => CmdArgs.Has("--ruinscheck");

    private int _ruin;
    private double _ruinT;
    private int _term, _pages;
    private Vector3 _ruinTarget;

    private void RuinStep(double delta)
    {
        _ruinT += delta;
        var s = _sites[_index];
        var me = _local();
        var colony = MoonColonies.Find(PlacedId(s));
        if (colony == null || MoonSurface.Active?.Sites.TryGetValue(colony.Id, out var placed) != true) { Finish("no colony in place"); return; }
        var plan = ColonyPlan.For(colony);
        var frame = placed!.Node.GlobalTransform;
        ColonyTerminals? set = null;
        foreach (var t in ColonyTerminals.Sets)
            if (t.IsInsideTree() && t.GetParent() == placed.Node) set = t;
        switch (_ruin)
        {
            case 1:
                Key(Godot.Key.T, true);
                RuinNext(2);
                break;
            case 2:
                Key(Godot.Key.T, false);
                RuinNext(3);
                break;
            case 3:
                if (me is { } m && m.Camera.Current && m.IsOnFloor() && _ruinT > 1.5)
                {
                    if (set == null || set.Count == 0) { Finish($"{colony.Id}: no terminal"); return; }
                    RuinLog($"{set.Count} terminal(s), the helmet {(m.HeadwearId == (int)Avatar.Headwear.SpaceHelmet ? "on" : "off")}");
                    _term = 0;
                    RuinNext(4);
                }
                else if (_ruinT > 20) Finish("the walker did not stand within 20 s of T");
                break;
            case 4:
            {
                // stand before the terminal, facing it, and page through
                var at = set!.At(_term);
                var face = set.Facing(_term);
                me!.PlaceAt(at + face * 0.95f + Vector3.Up * 0.05f, Mathf.Atan2(-face.X, -face.Z) + Mathf.Pi);
                _pages = 0;
                RuinNext(5);
                break;
            }
            case 5:
            {
                if (_ruinT < 0.5) break;
                if (!ColonyTerminals.TryUse(me!)) { Finish($"E at terminal {_term} did nothing"); return; }
                string shown = set!.Shown(_term);
                if (_pages == 0)
                {
                    if (!shown.Contains("1/")) { Finish($"terminal {_term}: no first page ({shown.Split('\n')[0]})"); return; }
                    RuinLog($"terminal {_term}: {shown.Split('\n')[0]}");
                    if (_term == 0 && !_headless) Shot("terminal", at: set.At(0) + set.Facing(0) * 0.85f + set.Facing(0).Cross(Vector3.Up) * 0.75f + Vector3.Up * 1.55f, look: set.At(0) + Vector3.Up * 1.15f);
                }
                _pages++;
                _ruinT = 0;
                // back at its title: the next terminal, or the tower
                if (!shown.Contains('\n'))
                {
                    _term++;
                    if (_term < set.Count) { RuinNext(4); break; }
                    RuinNext(plan.Tower != null ? 6 : 9);
                }
                break;
            }
            case 6:
            {
                if (_pending != null) break;
                // on the bridge, 9 m short of the rim, walking out to it
                var tw = plan.Tower!.Value;
                var (ax, az) = ColonyPlan.Along(tw.Yaw);
                var u = new Vector3((float)ax, 0, (float)az);
                var end = new Vector3((float)tw.X, 0, (float)tw.Z) + u * (float)(ColonyPlan.TowerHalf + 13);
                var heights = _ground()!.Heights;
                var axes = _origin.Frame.Axes!;
                var endWorld = frame * end;
                var n = heights.NormalUnder(axes.ToEcef(endWorld.X, endWorld.Y, endWorld.Z));
                var (gx, gy, gz) = axes.FromEcef(heights.Ground(n));
                var rim = new Vector3((float)gx, (float)gy, (float)gz);
                var dir = (frame.Basis * u).Normalized();
                me!.PlaceAt(rim - dir * 9 + Vector3.Up * 1.0f, Mathf.Atan2(-dir.X, -dir.Z));
                _ruinTarget = rim + dir * 4;
                me.WalkControls = () => (dir, false);
                RuinLog(string.Create(CultureInfo.InvariantCulture, $"on the bridge, {(frame.AffineInverse() * rim).Y:F1} m over the pit's floor"));
                RuinNext(7);
                break;
            }
            case 7:
                if (_ruinT > 1.2 && !_headless && _pending == null && !_towerShot)
                {
                    _towerShot = true;
                    var p = me!.GlobalPosition;
                    var toRim = (_ruinTarget - p).Normalized();
                    var across = toRim.Cross(frame.Basis * Vector3.Up).Normalized();
                    Shot("tower", at: p - toRim * 28 + across * 26 - frame.Basis * Vector3.Up * 12, look: p - toRim * 14 - frame.Basis * Vector3.Up * 16);
                }
                if (_ruinT > 7)
                {
                    me!.WalkControls = null;
                    var p = me.GlobalPosition;
                    var heights = _ground()!.Heights;
                    var axes = _origin.Frame.Axes!;
                    var n = heights.NormalUnder(axes.ToEcef(p.X, p.Y, p.Z));
                    var (_, gy, _) = axes.FromEcef(heights.Ground(n));
                    double over = p.Y - gy;
                    float floorY = (frame.AffineInverse() * p).Y;
                    RuinLog(string.Create(CultureInfo.InvariantCulture, $"walked out: {over:F2} m over the ground there, {floorY:F1} m over the pit's floor"));
                    if (floorY < 15 || over > 1.5) { Finish("the walker did not reach the rim from the bridge"); return; }
                    RuinNext(9);
                }
                break;
            case 9:
                if (_pending == null && _ruinT > 0.5) Finish(null);
                break;
        }
    }

    private bool _towerShot;

    private void Shot(string name, Vector3 at, Vector3 look)
    {
        _airCam ??= new Camera3D { Name = "AirlockShot", Fov = 70 };
        if (_airCam.GetParent() == null) AddChild(_airCam);
        _airCam.GlobalPosition = at;
        _airCam.LookAt(look, Vector3.Up);
        _airCam.MakeCurrent();
        _pending = name;
        _pendingFrames = 4;
    }

    private void RuinNext(int stage)
    {
        _ruin = stage;
        _ruinT = 0;
    }

    private static void RuinLog(string what) => GD.Print($"[moonsites]   ruins: {what}");
}
