using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --arrivalcheck[,out_prefix] [--at E,N] [--npcs N] [--style 0..3] [--shift N]
/// [--seconds S]</c>
///
/// <para>
/// Offline check of <see cref="NpcArrival"/> (#51): the race road from the spawn, a parked stand-in
/// player's car at the start, and N NPC cars appearing out of sight and driving to their grid
/// slots — styles varied as the server would choose them, or all <c>--style</c> (0 behind, 1 three-
/// point turn, 2 handbrake turn, 3 donut). When all are in (or after <c>--seconds</c>), the grid
/// is "sent" (<c>--shift N</c>: as if N more racers had joined, every slot 8 m further up) and the
/// countdown runs; at GO each NPC's slot error is measured. Filmed by a camera on whichever NPC is
/// manoeuvring; with a prefix a frame every 0.4 s is written (<c>prefix_0001_npc2_turn-back.png</c>).
/// Non-zero exit unless every NPC is within 1.5 m and 10° of its slot at GO, without a teleport
/// or an impact.
/// </para>
/// </summary>
public partial class ArrivalProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _prefix;
    private readonly int _n = CmdArgs.Int("--npcs") is int n ? Mathf.Clamp(n, 1, 8) : 4;
    private readonly ArrivalStyle? _only = CmdArgs.Int("--style") is int st ? (ArrivalStyle)Mathf.Clamp(st, 0, 3) : null;
    private readonly int _shift = CmdArgs.Int("--shift") ?? 0;
    private readonly double _entry = CmdArgs.Double("--seconds") ?? 110;
    private readonly int _seed = CmdArgs.Int("--seed") ?? 51;

    private bool _requested, _done;
    private RaceRoute? _route, _lane;
    private float _zero;
    private FootPlayer? _stand;
    private readonly List<Npc> _npcs = new();
    private double _t, _goIn = -1, _sinceFrame, _wait;
    private int _frames;
    private Camera3D? _cam;
    private Npc? _focus;
    private readonly List<string> _log = new();

    private sealed class Npc
    {
        public int Index, Slot;
        public FootPlayer Player = null!;
        public NpcArrival Arrival = null!;
        public int Impacts;
        public float ContactTime, Closest = float.MaxValue;
    }

    public ArrivalProbe(ChunkManager chunks, WorldOrigin origin, string? prefix)
    {
        _chunks = chunks;
        _origin = origin;
        _prefix = prefix;
    }

    public static (bool Requested, string? Prefix) ParseArgs() => CmdArgs.FlagWithShot("--arrivalcheck");

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        float dt = (float)delta;
        _wait += delta;
        if (_wait > _entry + 200) { GD.Print("[arrival] TIMEOUT"); End(); return; }
        if (!_requested)
        {
            if (_chunks.Source == null) return;
            _requested = true;
            var (e, nn) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, nn, 0);
            var source = _chunks.Source!;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var route = await RaceRoute.BuildAsync(source, _origin, at);
                Callable.From(() =>
                {
                    if (route == null) { GD.Print("[arrival] no road near the spawn"); Finish(1); return; }
                    _route = route;
                    var (c, w, zero) = NpcArrival.Lane(route);
                    _lane = RaceRoute.FromPoints(c, w);
                    _zero = zero;
                    GD.Print($"[arrival] route {route.Arc[^1]:F0} m, {route.Behind.Count * 2} m behind the start; lane {_lane.Arc[^1]:F0} m, start at {zero:F0}");
                }).CallDeferred();
            });
            return;
        }
        if (_lane == null || _route == null) return;

        if (_stand == null)
        {
            var p0 = NpcArrival.PointAt(_lane, _zero);
            if (!_chunks.TryGetHeight(p0, out float g0)) return;
            // the player who asked for the race, parked at the start (on the left of the road): in the way of the ones from behind
            var t0 = NpcArrival.TangentAt(_lane, _zero);
            _stand = Body("Player", (p0 + t0.Cross(Vector3.Up) * -1.2f) with { Y = g0 + 1.2f }, Mathf.Atan2(-t0.X, -t0.Z));
            var plan = NpcArrival.Plan(_lane, _zero, 1, _n, 1 + _n, new[] { _stand.GlobalPosition }, new[] { _stand.GlobalPosition }, _seed, _only);
            for (int i = 0; i < plan.Count; i++)
            {
                var e = plan[i];
                var npc = new Npc { Index = i + 1, Slot = e.Slot, Player = Body($"Npc{i + 1}", e.At, e.Yaw) };
                npc.Arrival = new NpcArrival(npc.Player, _lane, _zero, e.Style, e.Variant, e.Slot, 1 + _n)
                {
                    Log = line => _log.Add($"{_t,6:F1}s npc{npc.Index}: {line}"),
                };
                var me = npc;
                npc.Player.RideControls = () => me.Arrival.Drive((float)GetPhysicsProcessDeltaTime(), Bodies(me.Player));
                npc.Player.Impacted += lost =>
                {
                    if (lost < 1f) return;
                    me.Impacts++;
                    _log.Add($"{_t,6:F1}s npc{me.Index}: IMPACT -{lost * 3.6f:F0} km/h ({me.Arrival.Phase})");
                };
                _npcs.Add(npc);
                var (ls, _) = NpcArrival.Frame(_lane, e.At, -1);
                GD.Print($"[arrival] npc{npc.Index}: {e.Style} (variant {e.Variant}) for slot {e.Slot + 1}, appears {ls - _zero:F0} m from the start, "
                    + $"{RaceRoute.Flat(e.At - _stand.GlobalPosition).Length():F0} m from the player");
            }
            return;
        }

        // on their cars once they are on the ground
        foreach (var b in _npcs.Select(x => x.Player).Append(_stand))
            if (b.Vehicle is not Car && b.IsOnFloor()) b.SetRide(CarCatalog.All[0].Kind);
        if (_stand.Vehicle is Car) _stand.RideControls ??= () => new RideInput(0f, 0f, 0f, false, Handbrake: true);

        _t += delta;
        foreach (var x in _npcs)
            foreach (var y in _npcs.Where(y => y.Index > x.Index).Append(new Npc { Index = 0, Player = _stand }))
            {
                // contact: closer than a car's width side by side, or its length nose to tail
                var rel = RaceRoute.Flat(y.Player.GlobalPosition - x.Player.GlobalPosition);
                float d = rel.Length();
                x.Closest = Mathf.Min(x.Closest, d);
                // the bodies are capsules, the cars 4.2 x 1.7 m: overlapping footprints, in x's frame, are a touch
                var nose = new Vector3(-Mathf.Sin(x.Player.Motion.Yaw), 0, -Mathf.Cos(x.Player.Motion.Yaw));
                if (Mathf.Abs(rel.Dot(nose)) < 4.2f && Mathf.Abs(rel.Cross(nose).Y) < 1.7f) {
                    if (x.ContactTime == 0 || (y.Index > 0 && y.ContactTime == 0))
                        _log.Add($"{_t,6:F1}s npc{x.Index} ({x.Arrival.Phase}) touches {(y.Index > 0 ? $"npc{y.Index} ({y.Arrival.Phase})" : "the player")} at {d:F1} m");
                    x.ContactTime += dt;
                    if (y.Index > 0) y.ContactTime += dt;
                }
            }

        if (_goIn < 0 && (_npcs.All(x => x.Arrival.Staged) || _t > _entry))
        {
            // the close of entry: the real grid, GO in 5 s
            int count = 1 + _n + _shift;
            var course = RaceCourse.Ground(_route, 1000f);
            foreach (var x in _npcs) x.Arrival.SetSlot(course.Slot(x.Slot, count).At, 5.0);
            var (a0, f0) = course.Slot(0, count);
            _stand.PlaceAt(a0 + Vector3.Up * 1.2f, Mathf.Atan2(-f0.X, -f0.Z));
            _goIn = 5.0;
            GD.Print($"[arrival] grid sent at {_t:F1} s ({count} slots): GO in 5 s");
        }
        else if (_goIn >= 0 && (_goIn -= delta) <= 0) { End(); return; }

        Film(dt);
    }

    private FootPlayer Body(string name, Vector3 at, float yaw)
    {
        var p = new FootPlayer { Name = name, Terrain = _chunks, Npc = true };
        AddChild(p);
        p.GlobalPosition = at;
        p.Rotation = new Vector3(0, yaw, 0);
        return p;
    }

    private List<NpcArrival.Body> Bodies(FootPlayer me)
    {
        var list = new List<NpcArrival.Body>();
        foreach (var x in _npcs)
            if (x.Player != me) list.Add(new NpcArrival.Body(x.Player.GlobalPosition, x.Player.Velocity, true));
        if (_stand != null) list.Add(new NpcArrival.Body(_stand.GlobalPosition, _stand.Velocity, false));
        return list;
    }

    /// <summary>
    /// The camera stays on one NPC through its manoeuvre (a turn, a drift, a park) and moves on when
    /// it holds; between manoeuvres, on the one still driving that is nearest its slot. From the
    /// side and above, so a turn shows the whole road.
    /// </summary>
    private void Film(float dt)
    {
        if (_cam == null)
        {
            _cam = new Camera3D { Name = "ArrivalCam", Fov = 55f, Far = 20000f };
            AddChild(_cam);
        }
        bool Busy(Npc x) => x.Arrival.Phase is not ("approach" or "hold") && x.Player.Vehicle is Car;
        if (_focus == null || !Busy(_focus))
        {
            var next = _npcs.FirstOrDefault(Busy)
                ?? _npcs.Where(x => !x.Arrival.Settled).OrderBy(x => x.Arrival.Error().Metres).FirstOrDefault();
            if (next != _focus) _placed = false;
            _focus = next;
        }
        var target = _focus?.Player ?? _stand!;
        var car = target.GlobalPosition + Vector3.Up * 0.8f;
        var (s, _) = NpcArrival.Frame(_lane!, car, -1);
        var t = NpcArrival.TangentAt(_lane!, s);
        var want = car + t.Cross(Vector3.Up) * 13f - t * 6f + Vector3.Up * 9f;
        _cam.GlobalPosition = _placed ? _cam.GlobalPosition.Lerp(want, MathX.Damp(2f, dt)) : want;
        _placed = true;
        if (_chunks.TryGetHeight(_cam.GlobalPosition, out float g) && _cam.GlobalPosition.Y < g + 2f)
            _cam.GlobalPosition = _cam.GlobalPosition with { Y = g + 2f };
        var look = car - _cam.GlobalPosition;
        if (look.LengthSquared() > 0.25f && Mathf.Abs(look.Normalized().Y) < 0.98f)
            _cam.GlobalBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        _chunks.SetSightlineCut(_cam.GlobalPosition, car, 3f);
        _cam.Current = true;

        if (_prefix == null || (_sinceFrame += dt) < 0.4) return;
        _sinceFrame = 0;
        var img = GetViewport().GetTexture().GetImage();
        img.Resize(640, 360);
        img.SavePng($"{_prefix}_{++_frames:D4}_npc{_focus?.Index ?? 0}_{_focus?.Arrival.Phase ?? "none"}.png");
    }

    private bool _placed;

    private void End()
    {
        if (_done) return;
        foreach (var line in _log) GD.Print($"[arrival]   {line}");
        bool ok = true;
        foreach (var x in _npcs)
        {
            var a = x.Arrival;
            var (m, deg) = a.Error();
            bool good = m <= 1.5f && deg <= 10f && a.Teleports == 0 && x.Impacts == 0;
            ok &= good;
            GD.Print($"[arrival] npc{x.Index} slot {x.Slot + 1} {a.Planned}{(a.Style != a.Planned ? $"->{a.Style}" : "")}: "
                + $"{a.Travelled:F0} m driven, top {a.Top * 3.6f:F0} km/h, {a.Reversed:F1} m reversed, {Mathf.RadToDeg(a.Rotation):F0}° turned, "
                + $"{a.Legs} legs, in at {(a.StagedAt < 0 ? "never" : $"{a.StagedAt:F1} s")}, at GO {m:F2} m / {deg:F1}°, "
                + $"{x.Impacts} impacts, {x.ContactTime:F1} s in contact (closest {x.Closest:F1} m), {a.Teleports} teleports {(good ? "OK" : "FAIL")}");
        }
        GD.Print(ok ? "[arrival] RESULT: every NPC drove into its slot" : "[arrival] RESULT: FAILED");
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        _done = true;
        GetTree().Quit(code);
    }
}
