using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --garagelinks[,outdir[,villages]]</c> (#558): the garage doors of the
/// generated world's villages, on the real streamed tiles. Visits each village in turn, counts the
/// blocks that rolled a garage door, and for every road link measures the stub against the ground
/// (buried, floating) and against physics (a ray from above must land on the stub). Takes street
/// level screenshots of the first pavement and the first access road it finds. Windowed.
/// </summary>
public partial class GarageLinkProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string _dir;
    private readonly int _villages;
    private FootPlayer? _player;
    private Camera3D? _cam;
    private int _town, _phase;
    private double _t, _phaseT;
    private bool _shotSidewalk, _shotStub;
    private int _blocks, _blocks3, _garages, _sidewalks, _stubs, _bad, _ramps, _locked;
    private readonly List<DoorIndex.Entry> _todo = new();
    private DoorIndex.Entry? _shoot;
    private Vector3 _stand, _look;

    public GarageLinkProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        _chunks = chunks;
        _origin = origin;
        var parts = (shot ?? "").Split('|');
        _dir = parts[0].Length > 0 ? parts[0] : "test_output/garagelinks";
        _villages = 6;
        var args = CmdArgs.Value(CmdArgs.All, "--garagevillages");
        if (args != null && int.TryParse(args, out int n)) _villages = n;
        Directory.CreateDirectory(_dir);
    }

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--garagelinks");

    private void Land(Vector3 at, float yaw = 0)
    {
        if (!_chunks.TryGetHeight(at, out float g)) g = at.Y;
        _player!.LeaveInterior(new Vector3(at.X, g + 1f, at.Z), yaw);
    }

    public override void _Process(double delta)
    {
        _t += delta; _phaseT += delta;
        if (_t > 1500) { Finish("timed out"); return; }
        if (_player == null)
        {
            var (se, sn) = SpawnPoint.ParseTarget();
            var sat = _origin.ToWorld(se, sn, 0);
            if (!_chunks.TryGetHeight(sat, out float sg)) return;
            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(sat.X, sg + 1f, sat.Z);
            _cam = new Camera3D { Fov = 70, Far = 600 };
            AddChild(_cam);
            var me = _player;
            if (InteriorManager.Instance != null) InteriorManager.Instance.LocalPlayer = () => me;
            return;
        }
        var towns = Occasions.OccasionTowns.All;
        if (towns.Count == 0) return;
        if (_town >= towns.Count || _done >= _villages) { Finish(null); return; }
        var town = towns[_town];
        switch (_phase)
        {
            case 0: // fly to the village
            {
                var at = _origin.ToWorld(town.E, town.N, 0);
                if (!_chunks.TryGetHeight(at, out _))
                {
                    // stream the village in: stand over it, then wait for its ground
                    if (!_sent) { _sent = true; GD.Print($"[garage] flying to {town.Name}"); _player.LeaveInterior(new Vector3(at.X, 600, at.Z), 0); }
                    if (_phaseT > 20) { GD.Print($"[garage] no ground at {town.Name}"); Next(); }
                    return;
                }
                Land(at);
                _phase = 1; _phaseT = 0;
                GD.Print($"[garage] village {town.Name} at {town.E:F0},{town.N:F0}");
                break;
            }
            case 1: // let its tiles commit
                if (_phaseT < 30) return;
                Survey(town);
                _phase = 2; _phaseT = 0; _done++;
                break;
            case 2:
                if (_todo.Count == 0 && _shoot == null) { Next(); return; }
                if (_shoot == null)
                {
                    _shoot = _todo[0]; _todo.RemoveAt(0);
                    var d = _shoot.Value;
                    var side = new Vector3(-d.Outward.Z, 0, d.Outward.X);
                    // street level: from the road side, a few metres off the door's axis
                    float back = d.Link.Length + 5f;
                    _stand = d.World + d.Outward * back + side * 3f;
                    _look = d.World + Vector3.Up * 1.2f + d.Outward * (d.Link.Length * 0.5f);
                    Land(_stand);
                    _phaseT = 0; _shotStage = 0;
                    return;
                }
                if (_phaseT < 3.5) { PlaceCam(); return; }
                TakeShot();
                break;
        }
    }

    private int _shotStage;

    private void PlaceCam()
    {
        if (_shoot is not { } d || _cam == null) return;
        if (_player!.GlobalPosition.DistanceTo(_stand) > 3f) Land(_stand);
        float g = _chunks.TryGetHeight(_player.GlobalPosition, out float gg) ? gg : _player.GlobalPosition.Y;
        _cam.GlobalPosition = new Vector3(_player.GlobalPosition.X, g + 1.7f, _player.GlobalPosition.Z);
        _cam.LookAt(_look, Vector3.Up);
        _cam.MakeCurrent();
    }

    private void TakeShot()
    {
        var d = _shoot!.Value;
        PlaceCam();
        string name = $"{(d.Link.Kind == LinkKind.Sidewalk ? "sidewalk" : "stub")}_{_town}_{_shotStage}";
        var img = GetViewport().GetTexture().GetImage();
        string path = $"{_dir}/{name}.png";
        img.SavePng(path);
        GD.Print($"[garage] shot {path}: {d.Key} {d.Link.Kind} length {d.Link.Length:F1} m, rise {d.Link.RoadY - d.World.Y:F2} m");
        if (_shotStage == 0)
        {
            // second view: from the side, along the link, to see it against the slope
            var side = new Vector3(-d.Outward.Z, 0, d.Outward.X);
            _stand = d.World + d.Outward * (d.Link.Length * 0.5f) + side * 7f;
            _look = d.World + d.Outward * (d.Link.Length * 0.5f) + Vector3.Up * 0.3f;
            Land(_stand);
            _phaseT = 0; _shotStage = 1;
            return;
        }
        _shoot = null;
    }

    private bool _sent;
    private int _done;

    private void Next() { _sent = false; _town++; _phase = 0; _phaseT = 0; _todo.Clear(); _shoot = null; }

    private void Survey(Occasions.OccasionTowns.Town town)
    {
        var centre = _origin.ToWorld(town.E, town.N, 0);
        var byBuilding = new Dictionary<BuildingKey, List<DoorIndex.Entry>>();
        foreach (var e in DoorIndex.All())
        {
            if (new Vector2(e.World.X - centre.X, e.World.Z - centre.Z).Length() > 1500) continue;
            if (e.Kind is not (BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Other)) continue;
            if (!byBuilding.TryGetValue(e.Building, out var l)) byBuilding[e.Building] = l = new();
            l.Add(e);
        }
        GD.Print("[garage]   kinds: " + string.Join(", ", DoorIndex.All().GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}")));
        int blocks = 0, three = 0, garages = 0, sw = 0, st = 0, bad = 0;
        foreach (var (key, doors) in byBuilding)
        {
            blocks++;
            int front = doors.Count(d => !d.Vehicle);
            if (front >= 3) three++;
            foreach (var g in doors.Where(d => d.Vehicle && d.Link.Any))
            {
                garages++;
                if (g.Link.Kind == LinkKind.Sidewalk) sw++; else st++;
                // PR 2: its plan, as the server would make it: a ramp behind the door, or a locked door
                if (_chunks.Source != null && Task.Run(() => InteriorManager.LoadOrGenerate(_chunks.Source, $"{_dir}/plans", g.Building.ToString())).GetAwaiter().GetResult() is { } plan)
                {
                    bool ramp = plan.EntranceOf(g.Key.ToString()) != null && plan.Floors.Any(f => f.AllFlights().Any(x => x.Ramp));
                    if (ramp) _ramps++; else _locked++;
                    GD.Print($"[garage]   {g.Key}: {plan.Width:F0} x {plan.Depth:F0} m, {(ramp ? "a ramp down to the car park" : "no ramp: the door reads as locked (" + (InteriorGenerator.RampWhy ?? "?") + ")")}, "
                        + $"{InteriorValidator.Validate(plan).Count} validator problem(s)");
                }
                if (g.Link.Kind == LinkKind.Stub) bad += MeasureStub(g);
                bool want = g.Link.Kind == LinkKind.Sidewalk ? !_shotSidewalk : !_shotStub;
                if (want)
                {
                    if (g.Link.Kind == LinkKind.Sidewalk) _shotSidewalk = true; else _shotStub = true;
                    _todo.Add(g);
                }
            }
        }
        _blocks += blocks; _blocks3 += three; _garages += garages; _sidewalks += sw; _stubs += st; _bad += bad;
        GD.Print($"[garage] {town.Name}: {DoorIndex.All().Count()} doors drawn, {blocks} flats/mixed/other blocks with doors drawn, {three} with 3+ front doors, "
            + $"{garages} with a garage door ({sw} pavement, {st} access road), {bad} defect(s)");
    }

    /// <summary>The access road against the terrain and against a ray from above: 0 when fine.</summary>
    private int MeasureStub(DoorIndex.Entry d)
    {
        var space = _player!.GetWorld3D().DirectSpaceState;
        var o = d.Outward;
        var t = new Vector3(-o.Z, 0, o.X);
        float len = d.Link.Length, dy = d.Link.RoadY - d.World.Y;
        float maxBuried = 0, maxFloat = 0, maxRay = 0;
        int rayMiss = 0, samples = 0;
        for (int i = 0; i <= 20; i++)
            foreach (float along in new[] { -1.8f, -1f, 0f, 1f, 1.8f })
            {
                float out_ = Mathf.Lerp(0.6f, len - 0.2f, i / 20f);
                var p = d.World + t * along + o * out_;
                float u = Mathf.Clamp(out_ / len, 0, 1);
                float expect = d.World.Y + dy * u + d.Link.Hump * Mathf.Sin(Mathf.Pi * u) + 0.03f;
                if (!_chunks.TryGetHeight(p, out float ground)) continue;
                // the last 0.8 m meets the road's own kerb, up to ~15 cm proud of its centreline height
                bool atRoad = out_ > len - 0.8f;
                samples++;
                if (!atRoad) maxBuried = Mathf.Max(maxBuried, ground - expect);
                maxFloat = Mathf.Max(maxFloat, expect - ground);
                var q = PhysicsRayQueryParameters3D.Create(new Vector3(p.X, expect + 12, p.Z), new Vector3(p.X, expect - 12, p.Z));
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) { rayMiss++; continue; }
                float hy = ((Vector3)hit["position"]).Y;
                if (!atRoad) maxRay = Mathf.Max(maxRay, Mathf.Abs(hy - expect));
                else if (Mathf.Abs(hy - expect) > 0.25f) rayMiss++;
                if (Mathf.Abs(hy - expect) > 0.12f || ground - expect > 0.15f)
                    GD.Print($"[garage]     at out {out_:F1} m, along {along:F1}: terrain {ground - expect:+0.00;-0.00} m, ray {hy - expect:+0.00;-0.00} m vs the deck");
            }
        bool defect = maxBuried > 0.15f || maxRay > 0.15f || rayMiss > 0;
        GD.Print($"[garage]   stub {d.Key}: length {len:F1} m, rise {dy:F2} m, {samples} samples: terrain over stub {maxBuried:F2} m, "
            + $"stub over terrain {maxFloat:F2} m, ray off the deck by {maxRay:F2} m, {rayMiss} miss(es){(defect ? " DEFECT" : "")}");
        return defect ? 1 : 0;
    }

    private void Finish(string? failure)
    {
        GD.Print($"[garage] total over {_done} village(s): {_blocks} blocks, {_blocks3} with 3+ front doors, {_garages} garage doors "
            + $"({_sidewalks} pavement, {_stubs} access road), {_bad} stub defect(s), {_ramps} with a ramp planned, {_locked} locked");
        bool ok = failure == null && _bad == 0;
        if (failure != null) GD.Print($"[garage] FAIL {failure}");
        GD.Print(ok ? "[garage] RESULT: ok" : "[garage] RESULT: FAILED");
        SetProcess(false);
        GetTree().Quit(ok ? 0 : 1);
    }
}
