using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Trailer;

/// <summary>
/// One <see cref="Cast"/> member in the world: a <see cref="FootPlayer"/> put at its spot (or on its
/// road), mounted, launched and moved as its <see cref="Drive"/> says, as the probes do
/// (<see cref="DriveProbe"/>, <see cref="RideProbe"/>, <see cref="FlightCheckProbe"/>,
/// <see cref="BoatCheck"/>). An NPC body (no camera, its own ground anchor) unless it walks: an NPC
/// on foot only stands.
/// </summary>
public sealed class Actor
{
    public readonly Cast Spec;
    public readonly int Index;
    public FootPlayer? Body { get; private set; }

    /// <summary>Placed, mounted and launched: ready for the director's "go".</summary>
    public bool Ready { get; private set; }
    public string? Failed { get; private set; }

    /// <summary>Prints where it is twice a second (<c>--trailer-log</c>), to time a shot.</summary>
    public static readonly bool Log = CmdArgs.Has("--trailer-log");

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Node _stage;
    private readonly RouteBook _routes;
    private AutoPilot? _pilot;
    private RaceRoute? _route;
    private string RouteKey => Spec.Route ?? $"#{Index}";
    private double _moving = -1, _logged;
    private Vector3 _ahead = Vector3.Forward;
    private bool _launched, _afloat;
    private float _arc = float.NaN;

    /// <summary>The shot's other actors by index, for one that boards another's machine.</summary>
    private readonly System.Func<int, Actor?> _cast;

    public Actor(Cast spec, int index, Node stage, ChunkManager chunks, WorldOrigin origin, RouteBook routes, System.Func<int, Actor?> cast)
    {
        Spec = spec;
        Index = index;
        _stage = stage;
        _chunks = chunks;
        _origin = origin;
        _routes = routes;
        _cast = cast;
    }

    private bool OnRoad => Spec.Drive is Drive.Road or Drive.Follow || Spec.Route != null;
    private bool Boat => Spec.Board != null;

    /// <summary>A spot on the surface, in this frame's world space (null: the ground is not in yet).</summary>
    private Vector3? Ground(Spot spot)
    {
        var at = _origin.ToWorld(spot.E, spot.N, 0);
        // the surface is enough: the body (an NPC anchors its own ground) waits for its collision itself
        if (!_chunks.TryGetSurface(at, out float g)) return null;
        return at with { Y = g };
    }

    /// <summary>One physics step before the go: road, placement, mount, launch. Idempotent once ready.</summary>
    public void Prepare()
    {
        if (Ready || Failed != null) return;
        if (OnRoad && _route == null)
        {
            switch (_routes.Get(RouteKey, Spec, _chunks, _origin))
            {
                case RouteBook.State.Building: return;
                case RouteBook.State.None: Failed = "no road near its spot"; return;
            }
            _route = _routes.Route(RouteKey);
        }

        if (Body == null)
        {
            Spawn();
            return;
        }

        if (!Body.IsOnFloor() && !_launched && !_afloat) return;
        // a boat is mounted and put on the water in one step: a steamer left mounted on its dry spot
        // for a frame stands in whatever is there (Chillon's castle and railway), and is wrecked
        if (Boat && !_afloat && !World.WaterField.TryLevelAt(_origin.ToWorld(Spec.At.E, Spec.At.N, 0), out _)) return;
        if (Spec.Ride != RideKind.OnFoot && Body.Ride != Spec.Ride)
        {
            if (!Body.SetRide(Spec.Ride)) { Failed = $"mount {Spec.Ride} refused"; return; }
            if (Spec.Setup != 0) Body.SetCarSetup(Spec.Setup);
            if (Body.Vehicle is Car car) car.Headlights = Spec.Lights;
            Body.DoorsOpen = Spec.Doors;
            if (Spec.Trailer >= 0 && !Body.SpawnTrailer(Spec.Trailer, 1f)) GD.PrintErr($"[trailer] actor {Index}: trailer {Spec.Trailer} refused");
        }
        if (Boat && !_afloat)
        {
            // got in on the shore: now on the water at its spot
            var at = _origin.ToWorld(Spec.At.E, Spec.At.N, 0);
            if (!World.WaterField.TryLevelAt(at, out float level)) return;
            Body.PlaceBoat(at with { Y = level - Spec.Draught }, -Mathf.DegToRad(Spec.Heading),
                Forward(Spec.Heading) * Spec.Launch);
            _afloat = true;
        }
        if (Spec.Drive == Drive.Fly && !_launched)
        {
            var at = _origin.ToWorld(Spec.At.E, Spec.At.N, 0);
            float floor = _chunks.TryGetSurface(at, out float g) ? g : Body.GlobalPosition.Y;
            float y = Spec.At.Agl ? floor + Spec.At.H : Spec.At.H;
            Body.Rotation = new Vector3(0, -Mathf.DegToRad(Spec.Heading), 0);
            float climb = Mathf.DegToRad(Spec.Climb);
            Body.DebugLaunch(at with { Y = y }, (Forward(Spec.Heading) * Mathf.Cos(climb) + Vector3.Up * Mathf.Sin(climb)) * Spec.Launch);
            _launched = true;
        }
        if (_route != null && Spec.Drive == Drive.Road)
        {
            _pilot = AutoPilot.For(_route, Body);
            if (_pilot == null) { Failed = $"no autopilot for {Spec.Ride}"; return; }
            _pilot.Temperament(Spec.Skill, Spec.Aggression, 4000 + Index);
            _pilot.Go = false;
            // the pilot looks for its place on the line forward from the start: one put down along a
            // zigzag would stop at the leg before, metres away, and drive that one
            _pilot.D.Near = _route.Line.IndexAt(_arc);
        }
        Body.DanceId = Spec.Dance;
        Body.HeldItemId = Spec.Item;
        if (Spec.FirstPerson) Body.ViewForCheck(thirdPerson: false);
        Ready = true;
    }

    /// <summary>A flat unit vector along a compass bearing (0 north = −Z, 90 east = +X).</summary>
    private static Vector3 Forward(float bearing) =>
        new(Mathf.Sin(Mathf.DegToRad(bearing)), 0f, -Mathf.Cos(Mathf.DegToRad(bearing)));

    private void Spawn()
    {
        Vector3 at;
        float bearing = Spec.Heading;
        Vector3? carried = null;
        FootPlayer? carrier = null;
        if (Spec.Aboard >= 0)
        {
            // on another actor's machine once that one is afloat or in the air, moving as it moves
            if (_cast(Spec.Aboard) is not { Ready: true, Body: { } host }) return;
            at = host.GlobalTransform * Spec.Deck;
            bearing = Mathf.RadToDeg(-host.Rotation.Y) + Spec.Heading;
            carried = host.Velocity;
            carrier = host;
        }
        else if (Spec.FromDoor)
        {
            // on the step of the nearest front door, facing out, the door asked open
            var near = _origin.ToWorld(Spec.At.E, Spec.At.N, 0);
            if (!_chunks.TryGetSurface(near, out float ng)) return;
            if (Interiors.DoorIndex.NearestEntrance(near with { Y = ng }, 40f) is not { } door) return;
            var outward = (door.Outward with { Y = 0 }).Normalized();
            at = door.World + outward * 0.8f;
            if (_chunks.TryGetSurface(at, out float dg)) at.Y = dg;
            bearing = Mathf.RadToDeg(Mathf.Atan2(outward.X, -outward.Z));
            Interiors.InteriorManager.Instance?.OpenDoorForCamera(door.World + outward * 1.5f, 6f);
        }
        else if (_route != null)
        {
            float s = Mathf.Clamp(_routes.StartArc(RouteKey) + Spec.Arc, 2f, _route.Line.Length - 2f);
            at = _route.Line.PointAt(s);
            var fwd = MathX.Flat(_route.Line.PointAt(s + 2f) - _route.Line.PointAt(s - 2f)).Normalized();
            bearing = Mathf.RadToDeg(Mathf.Atan2(fwd.X, -fwd.Z));
            if (!_chunks.TryGetHeight(at, out float rg)) return;
            at.Y = rg;
            _arc = s;
        }
        else if (Ground(Boat ? Spec.Board!.Value : Spec.At) is { } g) at = g;
        else return;

        // an NPC body anchors its own ground and has no camera; one on foot only stands (on the ground,
        // under a deck: it never boards one), so a walker is not one, nor one aboard a machine
        bool npc = Spec.Drive != Drive.Walk && Spec.Aboard < 0;
        // a figure's look is found by its rider index (Appearance.For): a player's is its peer's, the
        // same for every actor here, so an NPC is named as a race entrant of owner 0 (an index of its
        // own) and its look registered there; a walker registers its own, the one walking in its shot
        var name = npc ? Net.PlayerReplication.NodeName(Net.PlayerReplication.NpcId(0, Index + 1)) : $"Actor{Index}";
        var body = new FootPlayer { Name = name, Terrain = _chunks, Npc = npc };
        body.Rotation = new Vector3(0, -Mathf.DegToRad(bearing), 0);
        body.AppearanceBits = Spec.Who?.AppearanceBits ?? Avatar.Appearance.ForSeed(Spec.Seed).Pack();
        if (npc) Avatar.Appearance.Register(body.RiderIndex(), body.AppearanceBits);
        if (Spec.Who is { } who) body.OutfitBits = who.OutfitBits;
        _stage.AddChild(body);
        // put down inside the machine's hull, which would take it for a crash (a wrecked steamer, a
        // wrecked freighter): only the deck the walk builds round it is solid to it, as for a player aboard
        if (carrier != null)
        {
            body.AddCollisionExceptionWith(carrier);
            carrier.AddCollisionExceptionWith(body);
        }
        if (carried is { } v)
        {
            body.DebugLaunch(at + Vector3.Up * 0.3f, v);
            _launched = true;
        }
        else body.GlobalPosition = at + Vector3.Up * (Spec.FromDoor ? 0.95f : 1.2f);
        _ahead = Forward(bearing);
        Body = body;
    }

    /// <summary>Starts it moving (the pre-roll begins): the controls are handed over from now on.</summary>
    public void Go(System.Func<Actor, IEnumerable<AutoPilot.Other>> others)
    {
        if (Body == null || !Ready) return;
        _moving = 0;
        var body = Body;
        switch (Spec.Drive)
        {
            case Drive.Road when _pilot != null:
                var pilot = _pilot;
                body.RideControls = () => pilot.Drive((float)body.GetPhysicsProcessDeltaTime(), _moving >= Spec.GoAt, others(this));
                break;
            case Drive.Follow when _route != null:
                body.RideControls = () => _moving < Spec.GoAt ? new RideInput(0f, 1f, 0f, false, Handbrake: true) : Follow(body);
                break;
            case Drive.Controls when Spec.Controls != null:
                body.RideControls = () => Spec.Controls(_moving);
                break;
            case Drive.Fly:
                body.FlyControls = Spec.Flight != null
                    ? () => Spec.Flight(_moving)
                    : () => new FlightInput(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);
                break;
            case Drive.Walk when Spec.Walk != null:
                body.WalkControls = () =>
                {
                    var (wish, run) = Spec.Walk(_moving);
                    var b = body.GlobalBasis;
                    var right = (b.X with { Y = 0 }).Normalized();
                    var ahead = (-b.Z with { Y = 0 }).Normalized();
                    return ((right * wish.X + ahead * wish.Y).LimitLength(1f), run);
                };
                break;
            case Drive.Stand when Spec.Ride != RideKind.OnFoot:
                body.RideControls = () => new RideInput(0f, 0f, 0f, false, Handbrake: true);
                break;
            case Drive.Stand when Spec.Aboard >= 0:
                // a walking body (see Spawn) standing on the deck: no keys of the player's moving it
                body.WalkControls = () => (Vector3.Zero, false);
                break;
        }
    }

    /// <summary>
    /// <see cref="Drive.Follow"/>: steer at the road a speed-scaled distance ahead (pure pursuit; a
    /// two-wheeler as a bank, <c>tan φ = v²κ/g</c>, as the race pilot does), and hold
    /// <see cref="Cast.Speed"/>, slowed for the bends ahead (6 m/s² across, braking at 4 m/s² to them).
    /// The arc is searched near the last one, so a road that comes back on itself is not jumped.
    /// </summary>
    private RideInput Follow(FootPlayer body)
    {
        var line = _route!.Line;
        var pos = body.GlobalPosition;
        float best = float.MaxValue, at = float.IsNaN(_arc) ? 0f : _arc;
        for (float s = Mathf.Max(0f, at - 10f); s <= Mathf.Min(line.Length, at + 40f); s += 1f)
        {
            float d = MathX.Flat(line.PointAt(s) - pos).LengthSquared();
            if (d < best) { best = d; _arc = s; }
        }
        float speed = body.RideSpeed;
        var fwd = (-body.GlobalBasis.Z with { Y = 0 }).Normalized();
        var target = line.PointAt(Mathf.Min(line.Length, _arc + Mathf.Clamp(speed * 1.1f, 7f, 30f)));
        var to = MathX.Flat(target - pos);
        var want = to.Normalized();
        float angle = Mathf.Atan2(fwd.Cross(want).Y, fwd.Dot(want));   // + = to the left
        float steer;
        if (body.Vehicle is Motorbike or Bicycle or Skis)
        {
            float kappa = 2f * Mathf.Sin(angle) / Mathf.Max(to.Length(), 1f);
            float bank = Mathf.Atan(Mathf.Max(speed, 2f) * Mathf.Max(speed, 2f) * kappa / Rideable.Gravity);
            steer = Mathf.Clamp(-bank / 0.75f, -1f, 1f);
        }
        else steer = Mathf.Clamp(-angle * 2.2f, -1f, 1f);

        float limit = Spec.Speed;
        for (float d = 4f; d < Mathf.Max(30f, speed * 3f); d += 4f)
        {
            float k = Curvature(line, _arc + d);
            float corner = Mathf.Sqrt(6f / Mathf.Max(k, 1e-4f));
            limit = Mathf.Min(limit, Mathf.Sqrt(corner * corner + 2f * 4f * d));
        }
        // a stop asked for along the road: braking to it at 3 m/s², stood still past it
        if (!float.IsNaN(Spec.StopAt))
        {
            float left = _routes.StartArc(RouteKey) + Spec.StopAt - _arc;
            limit = Mathf.Min(limit, left <= 0f ? 0f : Mathf.Sqrt(2f * 3f * left));
        }
        float err = limit - speed;
        bool end = _arc > line.Length - 15f || limit <= 0.01f;
        return new RideInput(end ? 0f : Mathf.Clamp(err * 0.35f, 0f, 1f), end ? 1f : Mathf.Clamp(-err * 0.3f, 0f, 1f), steer, false,
            Handbrake: end && speed < 0.5f);
    }

    /// <summary>How sharply the line bends at arc <paramref name="s"/>, 1/m.</summary>
    private static float Curvature(RaceLine line, float s)
    {
        var a = MathX.Flat(line.PointAt(s) - line.PointAt(s - 4f));
        var b = MathX.Flat(line.PointAt(s + 4f) - line.PointAt(s));
        if (a.LengthSquared() < 0.01f || b.LengthSquared() < 0.01f) return 0f;
        return Mathf.Abs(Mathf.Atan2(a.Cross(b).Y, a.Dot(b))) / 8f;
    }

    /// <summary>Its pilot's state for another car's racecraft.</summary>
    public AutoPilot? Pilot => _pilot;

    /// <summary>One physics step while it moves: the clock its scripts read, and its travel frame.</summary>
    public void Step(double dt, int shot)
    {
        if (Body == null || !GodotObject.IsInstanceValid(Body)) return;
        if (_moving >= 0) _moving += dt;
        if (Ready && Body.HeldItemId != Spec.Item) Body.HeldItemId = Spec.Item;
        if (Body.BackItemId != 0) Body.BackItemId = 0;
        var v = Body.IsFlying ? Body.Flight.Velocity : Body.Ride == RideKind.OnFoot ? Body.Velocity : Body.WorldVelocity;
        var flat = v with { Y = 0 };
        var want = flat.Length() > 2f ? flat.Normalized() : (-Body.GlobalBasis.Z with { Y = 0 }).Normalized();
        _ahead = _ahead.Slerp(want, Mathf.Clamp((float)dt * 4f, 0f, 1f)).Normalized();
        if (Log && _moving >= 0 && _moving - _logged >= 0.5)
        {
            _logged = _moving;
            var g = _origin.ToGlobal(Body.GlobalPosition);
            string arc = _pilot != null ? $" arc {_pilot.Arc:F0} m" : !float.IsNaN(_arc) ? $" arc {_arc:F0} m" : "";
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[trailer] shot {shot} t={_moving:F1} actor {Index} {Body.Ride} at {g.E:F0},{g.N:F0} alt {g.Alt:F0} {v.Length() * 3.6f:F0} km/h{arc}"));
        }
    }

    /// <summary>
    /// The driver's eye in the world and the way it looks, as the cockpit camera has it: the rig's
    /// <c>EyeFrame</c> on the vehicle's body (so the dash stays put when the nose dips); else the
    /// ride's first-person eye on the body.
    /// </summary>
    public (Vector3 Eye, Vector3 Ahead)? Seat
    {
        get
        {
            if (Body == null || !GodotObject.IsInstanceValid(Body) || Body.Vehicle is not { } ride) return null;
            if (Body.Visual is { } visual
                && ((visual as Avatar.CarRig)?.EyeFrame ?? (visual as Avatar.HeavyRig)?.EyeFrame) is { } frame)
            {
                var eye = visual.GlobalTransform * frame;
                return (eye.Origin, (-eye.Basis.Z).Normalized());
            }
            return (Body.GlobalTransform * ride.FirstPersonEye, (-Body.GlobalBasis.Z).Normalized());
        }
    }

    /// <summary>Its position and flat travel direction, for actor-relative camera keys.</summary>
    public (Vector3 At, Vector3 Ahead)? Frame =>
        Body != null && GodotObject.IsInstanceValid(Body) ? (Body.GlobalPosition, _ahead) : null;

    public void Free()
    {
        if (Body != null && GodotObject.IsInstanceValid(Body)) Body.QueueFree();
        Body = null;
    }
}

/// <summary>
/// The roads a shot's actors drive, built once per name on a worker (<see cref="RaceRoute.BuildAsync"/>)
/// from the first actor's spot toward its <see cref="Cast.Toward"/> (or the way it faces).
/// </summary>
public sealed class RouteBook
{
    public enum State { Building, Ready, None }

    private readonly Dictionary<string, (State State, RaceRoute? Route, float Start)> _routes = new();
    private readonly int _shot;

    public RouteBook(int shot) => _shot = shot;

    public State Get(string key, Cast spec, ChunkManager chunks, WorldOrigin origin)
    {
        if (_routes.TryGetValue(key, out var entry)) return entry.State;
        if (chunks.Source is not { } source) return State.Building;
        var at = origin.ToWorld(spec.At.E, spec.At.N, 0);
        var far = spec.Toward ?? spec.At.Toward(spec.Heading, 400f);
        var toward = origin.ToWorld(far.E, far.N, 0);
        _routes[key] = (State.Building, null, 0f);
        var frame = origin.Frame;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var route = await RaceRoute.BuildAsync(source, frame, at, toward: toward);
            Callable.From(() =>
            {
                if (route == null) { _routes[key] = (State.None, null, 0f); return; }
                // the arc the actors' own arcs count from: the route's point nearest the spot
                var pts = route.Line.Points;
                int k = Enumerable.Range(0, pts.Count).MinBy(i => MathX.Flat(pts[i] - at).LengthSquared());
                _routes[key] = (State.Ready, route, route.Line.Arc[k]);
                GD.Print($"[trailer] road '{key}': {route.Class}, {route.Length:F0} m, the spot at {route.Line.Arc[k]:F0} m");
                if (Actor.Log) Dump(key, route, frame);
            }).CallDeferred();
        });
        return State.Building;
    }

    /// <summary>The route's line every 5 m in LV95 with its arc, to place a camera along it.</summary>
    private void Dump(string key, RaceRoute route, OriginFrame frame)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output/trailer/routes");
        Directory.CreateDirectory(dir);
        using var w = new StreamWriter(Path.Combine(dir, $"shot{_shot:00}_{key.Replace('#', 'a')}.csv"));
        w.WriteLine("arc,e,n,alt");
        for (float s = 0; s < route.Line.Length; s += 5f)
        {
            var g = frame.ToGlobal(route.Line.PointAt(s));
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{s:F0},{g.E:F1},{g.N:F1},{g.Alt:F1}"));
        }
    }

    public RaceRoute? Route(string key) => _routes.TryGetValue(key, out var e) ? e.Route : null;

    /// <summary>A point on road <paramref name="key"/>: <c>at.Z</c> m along it from its spot, <c>at.X</c> to the right, <c>at.Y</c> over the ground.</summary>
    public Vector3? Point(string key, Vector3 at, ChunkManager chunks)
    {
        if (Route(key) is not { } route) return null;
        float s = Mathf.Clamp(StartArc(key) + at.Z, 0f, route.Line.Length);
        var p = route.Line.PointAt(s);
        var fwd = MathX.Flat(route.Line.PointAt(s + 2f) - route.Line.PointAt(Mathf.Max(0f, s - 2f))).Normalized();
        var right = fwd.Cross(Vector3.Up).Normalized();
        p += right * at.X;
        float g = chunks.TryGetSurface(p, out float h) ? h : p.Y;
        return p with { Y = Mathf.Max(g, p.Y) + at.Y };
    }
    public float StartArc(string key) => _routes.TryGetValue(key, out var e) ? e.Start : 0f;
}
