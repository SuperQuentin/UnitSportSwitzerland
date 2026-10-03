using Godot;
using UnitSport.Core;
using UnitSport.Avatar;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Walking about inside a vehicle as it moves (#162): a bus's saloon now, a train's coach, a boat's
/// deck or a plane's cabin later (any <see cref="Rideable.Decks"/>). See <c>docs/notes/player/walk-aboard.md</c>.
///
/// <para>
/// The walk is the ordinary on-foot movement, against the vehicle's deck: on this player's own peer
/// every walkable vehicle nearby gets its deck as collision (<see cref="DeckLayer"/>), moved with
/// the vehicle as it is drawn, every frame. Aboard, the player is carried by the vehicle's motion in
/// the same frame (position and heading), its velocity kept relative to the vehicle, and only the
/// deck is solid to it. Every other peer draws it from the vehicle's copy and the position in the
/// vehicle's frame it publishes (<see cref="DeckPos"/>), never from its own interpolated position.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>The physics layer of the decks a walking player collides with (layer 11).</summary>
    public const uint DeckLayer = 1u << 10;

    /// <summary>
    /// The vehicle this player walks about in: a player's peer id (it is being driven, or rolls on
    /// with somebody seated), or <c>v:</c> and a parked vehicle's name; empty when not aboard.
    /// Replicated with <see cref="DeckSection"/>, <see cref="DeckPos"/> and <see cref="DeckYaw"/>.
    /// </summary>
    [Export] public string DeckOn { get; set; } = "";
    /// <summary>The section of it whose frame <see cref="DeckPos"/> is in (a bus's front or rear half).</summary>
    [Export] public int DeckSection { get; set; }
    /// <summary>Where this player stands in that section's frame.</summary>
    [Export] public Vector3 DeckPos { get; set; }
    /// <summary>Its heading relative to that section's.</summary>
    [Export] public float DeckYaw { get; set; }

    public bool Aboard => DeckOn != "";

    /// <summary>For checks: how many walkable vehicles' decks are built round this player now.</summary>
    public int DeckSetsBuilt => _decks.Count;

    /// <summary>For checks: the three seats nearest, with their distances, in the vehicle this player walks about in.</summary>
    public string SeatsNearHere()
    {
        if (HostNamed(DeckOn) is not { } host || RideOfHost(host) is not { } ride) return "-";
        return string.Join(" ", ride.Seats.Select((s, i) => (i, AisleSpot(host, ride, i) is { } spot ? spot.DistanceTo(GlobalPosition) : 99f))
            .OrderBy(x => x.Item2).Take(3).Select(x => $"{x.i}:{x.Item2:F2}"));
    }

    /// <summary>For checks: the states that take the walk over.</summary>
    public string WalkState => $"wait {_deckWait:F2} mantle {_mantling} stun {_stunTimer:F2} slide {_sliding} placed {_placed} walk {(WalkControls != null)}";


    /// <summary>Knocked down (a hard hit, a vehicle's hard brake): lying, no control, for a moment.</summary>
    public bool Stunned => _stunTimer > 0f;

    /// <summary>Decks are built for walkable vehicles whose middle is this near, m (a bus's).</summary>
    private const float DeckReach = 30f;

    /// <summary>
    /// How near a walkable vehicle's middle must be for its decks to be built: <see cref="DeckReach"/>,
    /// or its decks' farthest reach from the middle plus 10 m when that is more (#303: a walker at the
    /// bow of a 76 m steamer is 38 m from its middle, and had no deck under them at 30).
    /// </summary>
    private static float DeckReachOf(Rideable ride)
    {
        float far = 0f;
        foreach (var deck in ride.Decks)
        {
            var a = deck.Aboard;
            far = Mathf.Max(far, new Vector2(Mathf.Max(Mathf.Abs(a.Position.X), Mathf.Abs(a.End.X)), Mathf.Max(Mathf.Abs(a.Position.Z), Mathf.Abs(a.End.Z))).Length());
        }
        return Mathf.Max(DeckReach, far + 10f);
    }
    /// <summary>Aboard, the body is a person's width: a bus's aisle is 57 cm between its seats.</summary>
    private const float AboardRadius = 0.2f;

    /// <summary>One vehicle's decks as collision here: a body per section, its door parts, and what was excepted.</summary>
    private sealed class DeckSet
    {
        public required Node3D Host;
        public required string Key;
        public required Rideable Ride;
        public readonly List<(VehicleDeck Deck, StaticBody3D Body, List<(CollisionShape3D Shape, DeckBox Box)> DoorParts)> Sections = new();
        public readonly List<CollisionObject3D> Excepted = new();
        /// <summary>The vehicle's level velocity, measured from how its first section is drawn moving, smoothed.</summary>
        public Vector3 Velocity;
        public Vector3 LastPos;
        public bool Measured;
    }

    private readonly Dictionary<string, DeckSet> _decks = new();
    private double _deckScan;
    /// <summary>The on-foot collision mask, decks included (set in _Ready).</summary>
    private uint _walkMask;
    /// <summary>The section frame this player was last carried from.</summary>
    private Transform3D _carriedFrom;
    private bool _deckCarried;
    /// <summary>Just stood up or left the wheel: carried at this velocity until the vehicle's deck is here to stand on.</summary>
    private float _deckWait;
    private Vector3 _deckWaitVelocity;

    /// <summary>
    /// How long a driver who got up from the wheel waits for the parked vehicle's deck: the server
    /// spawns it a round trip later, more under load. Past it, they step out by the door instead.
    /// </summary>
    private const float StandInWait = 5f;

    /// <summary>Got up from the wheel (#162): the way out by the door if the deck never comes, and where they stood up.</summary>
    private (Vector3 Door, Vector3 Right, float Side, Transform3D Frame, Rideable Vehicle, Vector3 From)? _standInExit;
    /// <summary>The vehicle's velocity smoothed, its acceleration, and how long that has been hard: the push a standing passenger feels.</summary>
    private Vector3 _deckFrameVel, _deckAccel;
    private bool _deckFrameValid;
    private float _hardFor;
    /// <summary>A remote copy's position in the deck's frame, eased toward the published one.</summary>
    private Vector3 _deckShown;
    private bool _deckShownValid;

    // ---- hosts ---------------------------------------------------------------------------------

    private static string KeyOf(Node3D host) => host is FootPlayer p ? p.Name.ToString() : "v:" + host.Name;

    private Node3D? HostNamed(string key) => key.StartsWith("v:")
        ? VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(key[2..])
        : GetParent()?.GetNodeOrNull<FootPlayer>(key);

    private static Rideable? RideOfHost(Node3D host) => host switch
    {
        FootPlayer p when p.Ride != RideKind.OnFoot && !p.RidingAlong => VehicleOf(p),
        VehicleBody { Wrecked: false } v => v.Ride,
        _ => null,
    };

    /// <summary>The node whose transform is section <paramref name="k"/>'s frame, as drawn now.</summary>
    private static Node3D? SectionFrame(Node3D host, int k) => host switch
    {
        FootPlayer p => k == 0 ? p._visual : p.GetNodeOrNull<Node3D>($"Section{k}")?.GetNodeOrNull<Node3D>("Visual"),
        // a parked vehicle has no model on a headless peer (a check): its own node is its frame then
        VehicleBody v => k == 0 ? v.Visual ?? v : v.Visual?.GetNodeOrNull<Node3D>($"Section{k}"),
        _ => null,
    };

    private static byte DoorsOfHost(Node3D host) => host switch
    {
        FootPlayer p => p.BusDoors,
        VehicleBody v => v.BusDoors,
        _ => 0,
    };

    /// <summary>
    /// How a vehicle moves over the ground, level, as measured from where it is drawn: what a player
    /// stepping on or off takes away or keeps (its rise and fall come with the carrying). Not its
    /// published velocity: a bus standing still published 33 m/s (a body's velocity is not its
    /// motion), and a passenger boarding it shot up through its roof.
    /// </summary>
    private Vector3 VelocityOfHost(Node3D host) =>
        _decks.TryGetValue(KeyOf(host), out var set) && set.Measured ? set.Velocity : Vector3.Zero;

    /// <summary>A frame's yaw, as a node's <c>Rotation.Y</c> (its back, +Z, on the level).</summary>
    private static float YawOf(Transform3D frame) => Mathf.Atan2(frame.Basis.Z.X, frame.Basis.Z.Z);

    // ---- this player's own peer: the decks as collision, carried with them ----------------------

    /// <summary>
    /// Every frame, after the vehicles' copies have moved (<c>ProcessPriority</c>): each deck nearby
    /// is put where its vehicle is drawn now, and a player aboard goes with its section, turning
    /// with it. Collision and player move together, so nothing between them moves at all.
    /// </summary>
    private void CarryOnDeck(float dt)
    {
        if (_ride != null || RidingWith != 0)
        {
            if (Aboard) LeaveDeck(keepVelocity: false);
            ClearDecks();
            return;
        }
        // The vehicle under this player stopped being one here (its driver got out: it is a parked
        // vehicle now, or somebody else's, arriving a round trip later). Its deck would follow the
        // driver's walking body: carried on at its last speed until the vehicle is back, then aboard.
        if (Aboard && (HostNamed(DeckOn) is not { } under || RideOfHost(under) is not { Walkable: true }))
        {
            var speed = _decks.TryGetValue(DeckOn, out var gone) ? gone.Velocity : Vector3.Zero;
            var world = Velocity + speed;
            LeaveDeck(keepVelocity: false);
            Velocity = world;
            _deckWait = 2.5f;
            _deckWaitVelocity = world with { Y = 0 };
            _standInExit = null;
            _deckScan = 0;
        }
        // waiting for the deck to stand on: looked for every frame, not twice a second, so it and the
        // exception from the vehicle's hull come the frame the vehicle does — in between, the parked
        // bus closed round a player not excepted from it, and shoved them out onto its roof
        if (_deckWait > 0f && !Aboard) _deckScan = 0;
        _deckScan -= dt;
        if (_deckScan <= 0) { _deckScan = 0.5; ScanDecks(); }

        foreach (var set in _decks.Values)
        {
            if (dt > 0f && SectionFrame(set.Host, 0) is { } first && IsInstanceValid(first) && first.IsInsideTree())
            {
                var pos = first.GlobalPosition;
                if (set.Measured)
                {
                    var raw = ((pos - set.LastPos) / dt) with { Y = 0 };
                    // a jump of the drawn frame (a copy appearing, a teleport) is not motion
                    if (raw.Length() < 120f) set.Velocity = set.Velocity.Lerp(raw, MathX.Damp(10f, dt));
                }
                set.LastPos = pos;
                set.Measured = true;
            }
            byte doors = DoorsOfHost(set.Host);
            foreach (var (deck, body, doorParts) in set.Sections)
            {
                var frame = SectionFrame(set.Host, deck.Section);
                bool here = frame != null && IsInstanceValid(frame) && frame.IsInsideTree();
                body.CollisionLayer = here ? DeckLayer : 0;
                if (!here) continue;
                body.GlobalTransform = frame!.GlobalTransform.Orthonormalized();
                foreach (var (shape, box) in doorParts)
                {
                    bool open = (doors >> box.Door & 1) != 0;
                    shape.Disabled = box.Part == DeckPart.DoorShut ? open : !open;
                    // a steamer's open plank tilts to the pier's head alongside (#383), as it is drawn
                    if (open && box.Part == DeckPart.DoorStep && set.Ride is Steamer) FitPlank(shape, box.Door, frame.GlobalTransform);
                }
            }
        }

        if (!Aboard || !_decks.TryGetValue(DeckOn, out var mine) || SectionFrame(mine.Host, DeckSection) is not { } section) return;
        var now = section.GlobalTransform.Orthonormalized();
        if (_deckCarried)
        {
            // the section's whole motion since the last frame: along, round, and (a ship's deck,
            // #303) pitching and rolling, so the walker rises and falls with the spot they stand on
            var delta = now * _carriedFrom.AffineInverse();
            float turn = MathX.WrapAngle(YawOf(now) - YawOf(_carriedFrom));
            var carried = delta * GlobalPosition;
            if (dt > 0f)
            {
                // how the deck under the walker moves (a rolling deck swings a walker high on it)
                var spot = ((carried - GlobalPosition) / dt) with { Y = 0 };
                if (spot.Length() < 120f) _deckSpotVel = _deckSpotValid ? _deckSpotVel.Lerp(spot, 1f - Mathf.Exp(-10f * dt)) : spot;
                _deckSpotValid = true;
            }
            GlobalPosition = carried;
            Rotation = new Vector3(0, Rotation.Y + turn, 0);
            _viewYaw += turn;
            // the velocity is the vehicle's frame's: it turns with it
            Velocity = new Basis(Vector3.Up, turn) * Velocity;
        }
        _carriedFrom = now;
        _deckCarried = true;
        DeckPos = now.AffineInverse() * GlobalPosition;
        DeckYaw = MathX.WrapAngle(Rotation.Y - YawOf(now));
    }

    /// <summary>The plank's box laid as <see cref="World.GangwayFit"/> says, moved only when it changes (a centimetre).</summary>
    private void FitPlank(CollisionShape3D shape, int door, Transform3D frame)
    {
        var (run, drop) = World.GangwayFit.Of(frame, door);
        var key = (Mathf.RoundToInt(run * 100f), Mathf.RoundToInt(drop * 100f));
        ulong id = shape.GetInstanceId();
        if (_plankFits.TryGetValue(id, out var was) && was == key) return;
        _plankFits[id] = key;
        if (shape.Shape is not BoxShape3D b) return;
        var box = SteamerMeshBuilder.PlankBox(door, key.Item1 / 100f, key.Item2 / 100f);
        b.Size = box.Size;
        shape.Transform = new Transform3D(box.Basis, box.Centre);
    }

    /// <summary>Each plank shape's fit as last laid (centimetres of run and drop), so it is moved only on a change.</summary>
    private readonly Dictionary<ulong, (int, int)> _plankFits = new();

    /// <summary>
    /// The origin moved (#185, offline): the deck state kept in world space moves with it. The frame
    /// it was carried from, above all: stale, the next carry jumps the player by the whole shift.
    /// </summary>
    private void ShiftDeck(Core.OriginShift shift)
    {
        _carriedFrom = shift.Apply(_carriedFrom);
        _deckWaitVelocity = shift.Direction(_deckWaitVelocity);
        _deckFrameVel = shift.Direction(_deckFrameVel);
        _deckSpotVel = shift.Direction(_deckSpotVel);
        _deckAccel = shift.Direction(_deckAccel);
        _stumble = shift.Direction(_stumble);
        foreach (var set in _decks.Values)
        {
            set.LastPos = shift.Point(set.LastPos);
            set.Velocity = shift.Direction(set.Velocity);
        }
        if (_standInExit is { } e)
            _standInExit = (shift.Point(e.Door), shift.Direction(e.Right), e.Side, shift.Apply(e.Frame), e.Vehicle, shift.Point(e.From));
    }

    /// <summary>Builds the decks of walkable vehicles that came near and frees those that left.</summary>
    private void ScanDecks()
    {
        var near = new List<Node3D>();
        foreach (var p in GetTree().GetNodesInGroup(Group).OfType<FootPlayer>())
            if (p != this && RideOfHost(p) is { Walkable: true } walked && p.GlobalPosition.DistanceTo(GlobalPosition) < DeckReachOf(walked)) near.Add(p);
        foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
            // only once its frame stands on the ground (VehicleBody.Posed): a frame later it jumps there
            if (RideOfHost(v) is { Walkable: true } parked && v.Posed && v.GlobalPosition.DistanceTo(GlobalPosition) < DeckReachOf(parked)) near.Add(v);

        var keys = near.Select(KeyOf).ToHashSet();
        foreach (var gone in _decks.Keys.Where(k => !keys.Contains(k) || !IsInstanceValid(_decks[k].Host)).ToList())
        {
            if (gone == DeckOn) continue;   // never pull the floor from under a player standing on it
            FreeDeck(_decks[gone]);
            _decks.Remove(gone);
        }
        foreach (var host in near)
        {
            string key = KeyOf(host);
            if (_decks.TryGetValue(key, out var known) && known.Host == host) continue;
            if (known != null) FreeDeck(known);
            _decks[key] = BuildDeck(host, key, RideOfHost(host)!);
        }
        int priority = _decks.Count > 0 || RidingWith != 0 || Aboard ? 10 : 0;
        if (ProcessPriority != priority) ProcessPriority = priority;
    }

    private DeckSet BuildDeck(Node3D host, string key, Rideable ride)
    {
        // its velocity starts as the vehicle publishes it (level, sane), until its motion is measured
        var start = host switch { VehicleBody v => v.Velocity, FootPlayer p => p.WorldVelocity, _ => Vector3.Zero } with { Y = 0 };
        var set = new DeckSet { Host = host, Key = key, Ride = ride, Velocity = start.LimitLength(60f) };
        foreach (var deck in ride.Decks)
        {
            var body = new StaticBody3D { Name = $"Deck_{key.Replace(':', '_')}_{deck.Section}", TopLevel = true, CollisionLayer = 0, CollisionMask = 0 };
            // created where it stands: put there after entering the world, Jolt sweeps a body from the
            // origin to its place in the next step, and a deck's roof swept up through the player
            // standing in the aisle, who came out on top of it (#162)
            if (SectionFrame(host, deck.Section) is { } at && at.IsInsideTree()) body.Transform = at.GlobalTransform.Orthonormalized();
            var doorParts = new List<(CollisionShape3D, DeckBox)>();
            foreach (var box in deck.Boxes)
            {
                var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Transform = new Transform3D(box.Basis, box.Centre) };
                body.AddChild(shape);
                if (box.Part != DeckPart.Solid) doorParts.Add((shape, box));
            }
            AddChild(body);
            set.Sections.Add((deck, body, doorParts));
        }
        // the vehicle's own hull is not in the way of a player walking up to it or inside it: its deck is
        if (host is CollisionObject3D hull) set.Excepted.Add(hull);
        set.Excepted.AddRange(host.GetChildren().OfType<CharacterBody3D>().Where(c => c.Name.ToString().StartsWith("Section")));
        foreach (var other in set.Excepted) AddCollisionExceptionWith(other);
        return set;
    }

    private void FreeDeck(DeckSet set)
    {
        // out of the physics now: freed at the end of the frame, a step could still stand on it
        foreach (var (_, body, _) in set.Sections) if (IsInstanceValid(body)) { body.CollisionLayer = 0; body.QueueFree(); }
        foreach (var other in set.Excepted) if (IsInstanceValid(other)) RemoveCollisionExceptionWith(other);
    }

    /// <summary>Off any deck and every deck body out of the physics now (getting into a vehicle).</summary>
    private void LeaveDecksNow()
    {
        if (Aboard) LeaveDeck(keepVelocity: false);
        _deckWait = 0f;
        _standInExit = null;
        foreach (var set in _decks.Values)
            foreach (var (_, body, _) in set.Sections)
                if (IsInstanceValid(body)) { body.CollisionLayer = 0; body.ProcessMode = ProcessModeEnum.Disabled; }
        ClearDecks();
    }

    private void ClearDecks()
    {
        _plankFits.Clear();
        if (_decks.Count == 0) return;
        foreach (var set in _decks.Values) FreeDeck(set);
        _decks.Clear();
    }

    /// <summary>
    /// Every physics step before the walk: whether this player stands in a deck's aboard volume (it
    /// boards or steps off as it walks in or out), and the push of the vehicle's accelerations.
    /// True while it is held in the air waiting for a deck that is not here yet: the walk is skipped.
    /// </summary>
    private bool DeckPhysics(float dt)
    {
        // which deck it stands in, if any: the one it is on first, a little stickier than the others
        // (a bus's two halves overlap only in the bellows)
        (DeckSet Set, VehicleDeck Deck, Transform3D Frame)? Inside()
        {
            foreach (var set in _decks.Values.OrderBy(s => s.Key == DeckOn ? 0 : 1))
                foreach (var (deck, _, _) in set.Sections)
                {
                    if (SectionFrame(set.Host, deck.Section) is not { } node) continue;
                    var frame = node.GlobalTransform.Orthonormalized();
                    var local = frame.AffineInverse() * GlobalPosition;
                    bool current = set.Key == DeckOn && deck.Section == DeckSection;
                    if (deck.Contains(local, current ? 0.15f : 0f)) return (set, deck, frame);
                }
            return null;
        }

        if (Inside() is { } at)
        {
            if (!Aboard) BoardDeck(at.Set, at.Deck.Section, at.Frame);
            else if (at.Set.Key != DeckOn || at.Deck.Section != DeckSection)
            {
                // into the other half of an articulated bus, or another vehicle: relative to it now
                if (at.Set.Key != DeckOn && HostNamed(DeckOn) is { } old) Velocity += VelocityOfHost(old) - VelocityOfHost(at.Set.Host);
                DeckOn = at.Set.Key;
                DeckSection = at.Deck.Section;
                _carriedFrom = at.Frame;
                _deckCarried = true;
            }
            Sway(at.Set, at.Deck, at.Frame, dt);
            return false;
        }

        if (Aboard) LeaveDeck(keepVelocity: true);
        if (_deckWait > 0f)
        {
            // stood up from a seat, or off the wheel, before this peer has the deck it stands on
            _deckWait -= dt;
            GlobalPosition += _deckWaitVelocity * dt;
            Velocity = _deckWaitVelocity;
            if (_deckWait <= 0f && _standInExit is { } exit)
            {
                // the parked vehicle never came: out by its door, where it will stand, not in it
                var moved = GlobalPosition - exit.From;
                var frame = exit.Frame with { Origin = exit.Frame.Origin + moved };
                _standInExit = null;
                GlobalPosition = FindExit(exit.Door + moved, exit.Right, exit.Side, frame, exit.Vehicle, grounded: true);
            }
            return true;
        }
        return false;
    }

    /// <summary>Steps aboard: from now on carried, its velocity the vehicle's frame's, only the deck solid.</summary>
    private void BoardDeck(DeckSet set, int section, Transform3D frame)
    {
        _stumble = Vector3.Zero;
        DeckOn = set.Key;
        DeckSection = section;
        _carriedFrom = frame;
        _deckCarried = true;
        _deckSpotValid = false;
        _deckWait = 0f;
        _standInExit = null;
        _deckFrameValid = false;
        Velocity -= VelocityOfHost(set.Host);
        CollisionMask = DeckLayer;
        _capsule.Radius = AboardRadius;
    }

    /// <summary>Off the deck (out of a door, or into a seat): the world's again, at the vehicle's speed plus its own if it steps off.</summary>
    private void LeaveDeck(bool keepVelocity)
    {
        if (keepVelocity && HostNamed(DeckOn) is { } host) Velocity += VelocityOfHost(host);
        DeckOn = "";
        _deckCarried = false;
        _deckFrameValid = false;
        CollisionMask = _walkMask;
        _capsule.Radius = BodyRadius;
    }

    /// <summary>
    /// The vehicle's accelerations, felt standing (<see cref="PassengerService.Inertia"/>): none
    /// (steady), a little and a hard hit knocks you down (sway), all of it (full). Holding a pole or a
    /// rail takes most of it.
    /// </summary>
    private void Sway(DeckSet set, VehicleDeck deck, Transform3D frame, float dt)
    {
        var mode = PassengerService.Inertia;
        if (dt <= 0f) return;
        // The vehicle's acceleration from its velocity, smoothed before and after: a copy's drawn
        // position is interpolated and its velocity arrives 30 times a second, and differentiating
        // either raw read every step as a lurch (a standing passenger was knocked over at 3 km/h).
        // A turn shows as the velocity turning: the push out of a bend comes with it.
        var velocity = VelocityOfHost(set.Host);
        // a ship's deck (#303): the spot the walker stands on, which its roll and pitch swing about too
        // and an airliner's cabin pitches and banks with it (#416)
        bool tilting = set.Ride is Boat or Airliner;
        if (tilting && _deckSpotValid) velocity = _deckSpotVel;
        if (!_deckFrameValid) { _deckFrameVel = velocity; _deckAccel = Vector3.Zero; _hardFor = 0f; }
        var smooth = _deckFrameVel.Lerp(velocity, MathX.Damp(8f, dt));
        var accel = (smooth - _deckFrameVel) / dt;
        _deckFrameVel = smooth;
        _deckFrameValid = true;
        _deckAccel = _deckAccel.Lerp(accel with { Y = 0 }, MathX.Damp(5f, dt));
        if (mode == PassengerService.DeckInertia.Steady) return;

        var local = frame.AffineInverse() * GlobalPosition;
        // holding on: standing by a pole (the rails overhead would be everywhere, so they do not count)
        bool holding = deck.Holds.Any(h => new Vector2(local.X, local.Z).DistanceTo(h) < 0.35f);
        bool full = mode == PassengerService.DeckInertia.Full;
        MaxDeckAccel = Mathf.Max(MaxDeckAccel, _deckAccel.Length());
        // A stumble of its own, not added to the walk: the walk eases its velocity to what the
        // stick asks at 12 m/s², more than any bus brakes, and ate every push whole. The feet's grip
        // takes the stumble back, the vehicle's acceleration feeds it; holding a pole takes most of it.
        float share = (full ? 1f : 0.45f) * (holding ? 0.25f : 1f);
        float grip = full ? 2f : 1.5f;
        float cap = full ? 3f : 1f;
        _stumble -= _deckAccel * share * dt;
        if (tilting)
        {
            // a deck heeled or trimmed is a slope: gravity along it pushes downhill, the feet take it back
            var up = frame.Basis.Y.Normalized();
            var downhill = new Vector3(up.X, 0f, up.Z) * Rideable.Gravity;
            _stumble += downhill * share * dt;
            MaxDeckTilt = Mathf.Max(MaxDeckTilt, Mathf.Acos(Mathf.Clamp(up.Y, -1f, 1f)));
        }
        _stumble = _stumble.MoveToward(Vector3.Zero, grip * dt).LimitLength(cap);
        if (IsOnFloor() && _stumble.LengthSquared() > 1e-4f) MoveAndCollide(_stumble * dt);
        // knocked down by a hard brake, a sharp swerve, a crash: one that lasts, not a jolt
        float a = _deckAccel.Length();
        float knock = (full ? 4.5f : 7f) * (holding ? 2f : 1f);
        _hardFor = a > knock ? _hardFor + dt : 0f;
        if (_hardFor > (full ? 0.25f : 0.35f) && _stunTimer <= 0f && IsOnFloor())
        {
            _stunTimer = 1.2f;
            _hardFor = 0f;
            _stumble -= _deckAccel.Normalized() * Mathf.Min(a * 0.2f, cap);
        }
    }

    /// <summary>The stumble the vehicle's accelerations give a standing player, m/s, in the world's axes.</summary>
    private Vector3 _stumble;

    /// <summary>For checks: the stumble now, m/s (world axes).</summary>
    public Vector3 Stumble => _stumble;

    /// <summary>For checks: the steepest a deck stood under this player since the last reset, rad (a ship's heel and trim).</summary>
    public float MaxDeckTilt { get; set; }

    /// <summary>The deck's own velocity under the walker (a ship's, #303), level, smoothed: from how the carry moved them.</summary>
    private Vector3 _deckSpotVel;
    private bool _deckSpotValid;

    /// <summary>For checks: the hardest acceleration of the vehicle felt aboard since the last reset, m/s².</summary>
    public float MaxDeckAccel { get; set; }

    // ---- the vehicle's side: players walking in it are not in the way of its hull ----------------

    private readonly HashSet<FootPlayer> _guests = new();

    /// <summary>
    /// The local host of a walkable vehicle, every physics step: the players walking up to its doors
    /// or about in it are excepted from its hull and its sections'. Without it, on the driver's peer
    /// a passenger's copy stepping into the doorway overlapped the hull and the bus was shoved aside
    /// and up by it: the doorway backed away from the passenger, who stalled on the step and popped
    /// onto the roof.
    /// </summary>
    private void IgnoreGuests()
    {
        var bodies = new List<PhysicsBody3D> { this };
        bodies.AddRange(_sections);
        WatchGuests(this, _ride is { Walkable: true } ride && _visual != null ? ride : null, _guests, bodies, k => SectionFrame(this, k));
    }

    /// <summary>
    /// Excepts from <paramref name="bodies"/> every player standing within a metre of <paramref name="ride"/>'s
    /// decks (or none, with no ride), and lets go of those who walked off. Shared with parked vehicles.
    /// </summary>
    public static void WatchGuests(Node node, Rideable? ride, HashSet<FootPlayer> guests, IReadOnlyList<PhysicsBody3D> bodies,
        Func<int, Node3D?> frameOf)
    {
        var near = new HashSet<FootPlayer>();
        if (ride != null)
            foreach (var p in node.GetTree().GetNodesInGroup(Group).OfType<FootPlayer>())
            {
                if (bodies.Contains(p)) continue;
                foreach (var deck in ride.Decks)
                    if (frameOf(deck.Section) is { } frame
                        && deck.Contains(frame.GlobalTransform.AffineInverse() * p.GlobalPosition, 1f))
                    {
                        near.Add(p);
                        break;
                    }
            }
        foreach (var p in near)
            if (guests.Add(p))
                foreach (var body in bodies) body.AddCollisionExceptionWith(p);
        foreach (var p in guests.Where(g => !near.Contains(g)).ToList())
        {
            guests.Remove(p);
            if (!IsInstanceValid(p)) continue;
            foreach (var body in bodies) if (IsInstanceValid(body)) body.RemoveCollisionExceptionWith(p);
        }
    }

    // ---- door buttons, inside and out -----------------------------------------------------------

    /// <summary>
    /// The door button within a hand's reach of this player's chest, of any walkable vehicle near
    /// (driven, or parked), and whether that door is open; null when none.
    /// </summary>
    public (Node3D Host, int Door, bool Open)? ButtonInReach()
    {
        var chest = GlobalPosition + Vector3.Up * 1.1f;
        (Node3D, int, bool)? best = null;
        float bestDist = PassengerService.ButtonReach;
        var hosts = GetTree().GetNodesInGroup(Group).OfType<FootPlayer>().Where(p => p != this).Cast<Node3D>()
            .Concat(VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>());
        foreach (var host in hosts)
        {
            if (RideOfHost(host) is not { Walkable: true } ride || host.GlobalPosition.DistanceTo(GlobalPosition) > DeckReachOf(ride)) continue;
            byte doors = DoorsOfHost(host);
            foreach (var deck in ride.Decks)
            {
                if (SectionFrame(host, deck.Section) is not { } frame) continue;
                foreach (var button in deck.Buttons)
                {
                    var at = frame.GlobalTransform * button.At;
                    float d = at.DistanceTo(chest);
                    // a ship's gangway buttons outside are pressed from the pier its plank reaches
                    // (1.3 m off the hull, #383): reached from that far, a bell-pull rather than a bus's push
                    if (ride is Steamer && (frame.GlobalTransform.Basis * button.Normal).Dot(chest - at) > 0f)
                        d -= ShipButtonReach - PassengerService.ButtonReach;
                    if (d < bestDist) { bestDist = d; best = (host, button.Door, (doors >> button.Door & 1) != 0); }
                }
            }
        }
        return best;
    }

    /// <summary>How far a ship's gangway button outside is reached from, m (a pier's face is 1.3 m off the hull).</summary>
    private const float ShipButtonReach = 1.9f;

    /// <summary>E or G at a door's button: the door opens or shuts, whoever presses it. True when there was one.</summary>
    private bool TryDoorButton()
    {
        if (_ride != null || RidingWith != 0 || ButtonInReach() is not { } button) return false;
        switch (button.Host)
        {
            case VehicleBody parked: Vehicles?.ToggleDoor(parked, (byte)(1 << button.Door)); break;
            case FootPlayer host: PassengerService.Instance?.PressDoor(host, button.Door); break;
        }
        return true;
    }

    /// <summary>The host of a bus: somebody pressed one of its doors' buttons (the server checked they stand at it).</summary>
    public void DoorPressed(int door)
    {
        if (_ride is Truck { IsBus: true } bus) bus.ToggleDoor(door);
        else if (_ride is Steamer steamer && door is >= 0 and < Steamer.GangwayCount) steamer.DoorsOpen ^= (byte)(1 << door);
        else if (_ride is Airliner jet) jet.ToggleDoor(door);
    }

    // ---- seats and the wheel from the aisle ------------------------------------------------------

    /// <summary>
    /// The seat (or the wheel, 0) whose place to stand at is nearest a point, within reach; −1 when
    /// none. Measured to where you stand to take it (<see cref="AisleSpot"/>), not to the seat: the
    /// driver's seat is up on its platform, and a passenger seat beside it came out nearer.
    /// </summary>
    private static int SeatNear(Node3D host, Rideable ride, Vector3 point, float reach)
    {
        int best = -1;
        float bestDist = reach;
        for (int i = 0; i < ride.Seats.Length; i++)
        {
            if (AisleSpot(host, ride, i) is not { } spot) continue;
            float d = spot.DistanceTo(point);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// E walking aboard: sit in the seat in reach, or take the wheel. A parked vehicle is taken over
    /// (claimed), driverless from that seat or driven; in one somebody hosts, the server hands it out.
    /// </summary>
    private bool TryDeckSeat()
    {
        if (HostNamed(DeckOn) is not { } host || RideOfHost(host) is not { } ride) return false;
        int seat = SeatNear(host, ride, GlobalPosition, 1.3f);
        if (seat < 0) { PassengerService.Say("No seat within reach."); return true; }
        if (host is VehicleBody parked)
        {
            Vehicles?.Claim(parked, state => TakeVehicle(state, seat));
            return true;
        }
        if (host is not FootPlayer owner) return false;
        if (seat == 0)
        {
            if (owner.SeatIndex == 0) PassengerService.Say("Somebody is driving.");
            else PassengerService.Instance?.AskWheelOf(owner);
        }
        else PassengerService.Instance?.AskSeatAt(owner, seat);
        return true;
    }

    /// <summary>What E does walking about aboard, for the on-screen hints: sit down, take the wheel, or nothing in reach (null).</summary>
    public string? DeckHint
    {
        get
        {
            if (!Aboard || HostNamed(DeckOn) is not { } host || RideOfHost(host) is not { } ride) return null;
            int seat = SeatNear(host, ride, GlobalPosition, 1.3f);
            if (seat < 0) return null;
            if (seat > 0) return "Sit down";
            return host is FootPlayer { SeatIndex: 0 } ? null : "Take the wheel";
        }
    }

    /// <summary>Where standing up from <paramref name="seat"/> puts you: beside it, toward the aisle, on its floor (world).</summary>
    private static Vector3? AisleSpot(Node3D host, Rideable ride, int seat)
    {
        if (seat < 0 || seat >= ride.Seats.Length || SectionFrame(host, ride.Seats[seat].Section) is not { } frame) return null;
        var s = ride.Seats[seat];
        if (ride.StandSpot(seat) is { } own) return frame.GlobalTransform * own;
        // the driver's corner is a block to the walk: out past it, by the front door
        float step = seat == 0 ? 0.8f : 0.55f;
        var local = new Vector3(s.Hip.X - Mathf.Sign(s.Hip.X) * step, s.Floor + 0.05f, s.Hip.Z);
        return frame.GlobalTransform * local;
    }

    /// <summary>
    /// On one's feet at <paramref name="spot"/> in a walkable vehicle going at <paramref name="velocity"/>,
    /// aboard as soon as its deck is here. <paramref name="exit"/>: up from the wheel of a vehicle
    /// being parked, the way out by its door should its deck not come.
    /// </summary>
    private void StandIn(Vector3 spot, Vector3 velocity, (Vector3 Door, Vector3 Right, float Side, Transform3D Frame, Rideable Vehicle)? exit = null)
    {
        GlobalPosition = spot;
        Velocity = velocity;
        _deckWait = exit != null ? StandInWait : 2f;
        _deckWaitVelocity = velocity;
        _deckScan = 0;
        _standInExit = exit is { } e ? (e.Door, e.Right, e.Side, e.Frame, e.Vehicle, spot) : null;
    }

    /// <summary>A passenger in a walkable vehicle stands up into the aisle, at any speed.</summary>
    private bool StandUp()
    {
        var host = Host;
        if (host == null || VehicleOf(host) is not { Walkable: true } vehicle || AisleSpot(host, vehicle, SeatIndex) is not { } spot)
            return false;
        PassengerService.Instance?.LeaveSeat();
        var velocity = host.WorldVelocity;
        float yaw = host.GlobalRotation.Y + _lookYaw;
        if (_seated != null && IsInstanceValid(_seated)) _seated.QueueFree();
        _seated = null;
        RidingWith = 0;
        SeatIndex = 0;
        _host = null;
        _body.Disabled = false;
        _lookYaw = 0f;
        _viewYaw = yaw;
        Rotation = new Vector3(0, yaw, 0);
        RefreshVisual(force: true);
        _walkerHidden = false;
        StandIn(spot, velocity);
        return true;
    }

    // ---- every other peer: drawn from the vehicle's copy ----------------------------------------

    /// <summary>A remote copy aboard: where its vehicle is drawn, plus the published spot on it, eased. False when its vehicle is not here.</summary>
    private bool PlaceOnDeck(float dt)
    {
        if (HostNamed(DeckOn) is not { } host || SectionFrame(host, DeckSection) is not { } node) { _deckShownValid = false; return false; }
        // eased between sends, but a jump (just boarded, just stood up) is taken at once
        _deckShown = _deckShownValid && _deckShown.DistanceTo(DeckPos) < 1f ? _deckShown.Lerp(DeckPos, MathX.Damp(14f, dt)) : DeckPos;
        _deckShownValid = true;
        var frame = node.GlobalTransform.Orthonormalized();
        GlobalPosition = frame * _deckShown;
        Rotation = new Vector3(0, YawOf(frame) + DeckYaw, 0);
        return true;
    }
}
