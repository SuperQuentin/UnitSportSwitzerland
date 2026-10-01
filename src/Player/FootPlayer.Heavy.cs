using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Trucks and buses (#70): the bodies of the sections behind the cab, coupling, the truck's own
/// controls and their replication. The first section is this player's own body, driven like a car;
/// every section behind it is a <see cref="CharacterBody3D"/> of its own, placed from the
/// articulation angles and moved by <c>MoveAndSlide</c>, so a trailer collides with the world — and
/// what it hits goes back into the train's physics (<see cref="HeavyTrain.Contacts"/>).
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// The coupled trailer (<see cref="TrailerCatalog.Code"/>, with its load), 0 for none. Replicated
    /// beside <see cref="RideKindId"/>, whose meaning it depends on, and reset with it in
    /// <see cref="ApplyRide"/>.
    /// </summary>
    [Export] public int TrailerCode { get; set; }

    /// <summary>The train's joint angles (rad), written by the owner every frame: what a remote copy poses the sections from.</summary>
    [Export] public Vector4 TrainPose { get; set; }

    /// <summary>A truck being driven, or null.</summary>
    public Truck? Heavy => _ride as Truck;

    /// <summary>Section k's body is <c>_sections[k − 1]</c>.</summary>
    private readonly List<CharacterBody3D> _sections = new();
    private int _visualTrailer;
    private float _truckPitch;
    private readonly float[] _shownAngles = new float[Truck.MaxJoints];

    /// <summary>How close a hitch must come to a trailer's kingpin or drawbar eye to couple, m.</summary>
    private const float CoupleReach = 0.9f;

    // ---- the section bodies ------------------------------------------------------------------

    /// <summary>
    /// The sections behind the first, rebuilt with the visual (a new truck, a trailer coupled or
    /// dropped), on the owner and on every remote copy alike so everyone hits what they see.
    /// </summary>
    private void FitSections(RideKind kind)
    {
        foreach (var s in _sections)
        {
            if (IsInstanceValid(s)) RemoveCollisionExceptionWith(s);
            s.QueueFree();
        }
        _sections.Clear();
        _visualTrailer = TrailerCode;
        var truck = _ride as Truck;
        if (truck == null && HeavyCatalog.For(kind) is { } spec)
        {
            // a remote copy: its own instance of the owner's train, for the rigs and the geometry
            truck = new Truck(spec, TrailerCode);
            _remoteRide = truck;
        }
        if (truck == null || NetProxy) return;

        for (int k = 1; k < truck.SectionCount; k++)
        {
            var body = new CharacterBody3D
            {
                Name = $"Section{k}",
                TopLevel = true,
                CollisionLayer = CollisionLayer,
                CollisionMask = CollisionMask,
                FloorMaxAngle = Mathf.DegToRad(60f),
            };
            var rig = truck.SectionRig(k);
            rig.Name = "Visual";
            body.AddChild(rig);
            // the same two hull boxes as the cab's: measured from this section's own mesh
            var (lower, upper) = Avatar.MeshBounds.Split(rig, HullCut);
            foreach (var box in new[] { lower, upper })
            {
                float bottom = Mathf.Max(box.Position.Y, truck.HullLift);
                float top = box.End.Y;
                if (top - bottom < 0.1f || box.Size.X < 0.05f) continue;
                body.AddChild(new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = new Vector3(box.Size.X, top - bottom, box.Size.Z) },
                    Position = new Vector3(box.GetCenter().X, (top + bottom) / 2f, box.GetCenter().Z),
                });
            }
            AddChild(body);
            body.GlobalTransform = GlobalTransform * truck.NodeLocal(k);
            // both ways: a body's own motion only honours its own list, and the cab moving into
            // its own trailer's nose was pushed up onto it, and kept climbing
            body.AddCollisionExceptionWith(this);
            AddCollisionExceptionWith(body);
            foreach (var other in _sections) { body.AddCollisionExceptionWith(other); other.AddCollisionExceptionWith(body); }
            _sections.Add(body);
        }
        for (int j = 0; j < _shownAngles.Length; j++) _shownAngles[j] = truck.Articulation[j];
    }

    /// <summary>What the section bodies' own ray tests must not hit: this player and its train.</summary>
    private Godot.Collections.Array<Rid> TrainRids()
    {
        var rids = new Godot.Collections.Array<Rid> { GetRid() };
        foreach (var s in _sections) rids.Add(s.GetRid());
        return rids;
    }

    /// <summary>The ground's height under a point: whatever is solid there (a road, a bridge deck), else the terrain.</summary>

    private float GroundUnder(Vector3 p, Godot.Collections.Array<Rid> exclude)
    {

        var query = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 3f, p + Vector3.Down * 6f,
            CollisionMask & ~World.TreeColliders.Layer, exclude);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count > 0) return hit["position"].AsVector3().Y;
        return Terrain != null && Terrain.TryGetHeight(p, out float g) ? g : p.Y;
    }

    /// <summary>
    /// The cab's pitch on the ground under its axles: a 12 m bus on a 10% road leans with it, and its
    /// hull must, or the level box would dig into the slope ahead.
    /// </summary>
    private void PitchCab(Truck truck, Godot.Collections.Array<Rid> exclude)
    {
        var s = truck.Section(0);
        float cg = truck.Train.Bodies[0].CgAt;
        float front = HeavyTrain.FrontAxleAt(s), rear = HeavyTrain.RearGroupAt(s);
        var f = ToGlobal(new Vector3(0, 0, -(cg - front)));
        var r = ToGlobal(new Vector3(0, 0, -(cg - rear)));
        float target = Mathf.Atan2(GroundUnder(f, exclude) - GroundUnder(r, exclude), rear - front);
        _truckPitch = Mathf.Lerp(_truckPitch, Mathf.Clamp(target, -0.35f, 0.35f), 0.3f);
    }

    /// <summary>
    /// Where section <paramref name="k"/> should stand: across the ground from the chain of
    /// articulation angles, and pitched between the pin it hangs on (at the height the section
    /// ahead carries it) and the ground under its own axles.
    /// </summary>
    private Transform3D SectionWorld(Truck truck, int k, Godot.Collections.Array<Rid> exclude)
    {
        var flat = GlobalTransform * truck.NodeLocal(k);
        var b = truck.Train.Bodies[k];
        var parent = truck.Train.Bodies[k - 1];
        var forward = -flat.Basis.Z;
        float zRear = b.CgAt - HeavyTrain.RearGroupAt(b.Spec);   // + forward of the CG
        float ground = GroundUnder(flat.Origin + forward * zRear, exclude);
        float pitch = 0f;
        // a drawbar dolly stands on its own axle; anything on a fifth wheel, turntable or joint is
        // carried at the front by the section ahead
        if (b.Spec.Pivot is Coupling.FifthWheel or Coupling.Turntable or Coupling.BusJoint)
        {
            var parentWorld = k == 1
                ? GlobalTransform * new Transform3D(new Basis(Vector3.Right, _truckPitch), Vector3.Zero)
                : _sections[k - 2].GlobalTransform;
            var pin = parentWorld * new Vector3(0, parent.Spec.HitchHeight, -parent.HitchZ);
            float d = Mathf.Max(b.PivotZ - zRear, 0.5f);
            pitch = Mathf.Clamp(Mathf.Atan2(pin.Y - ground, d) - Mathf.Atan2(parent.Spec.HitchHeight, d), -0.4f, 0.4f);
        }
        var basis = flat.Basis * new Basis(Vector3.Right, pitch);
        return new Transform3D(basis, flat.Origin with { Y = ground - zRear * Mathf.Sin(pitch) });
    }
    /// <summary>
    /// Owner, after the cab has moved: every section slides toward where the train says it is. What
    /// stops one short (a wall, a bollard, a parked car) becomes a contact for the next step, and a
    /// section held well off its line takes the angle it is really at.
    /// </summary>
    private void StepSections(Truck truck, float dt)
    {
        var exclude = TrainRids();
        PitchCab(truck, exclude);
        if (_sections.Count == 0) return;
        var fwd = (-GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
        var left = (-GlobalTransform.Basis.X with { Y = 0 }).Normalized();
        Vector2 Train(Vector3 v) => new(v.Dot(fwd), v.Dot(left));
        float psi = 0f;

        for (int k = 1; k < truck.SectionCount && k - 1 < _sections.Count; k++)
        {
            var body = _sections[k - 1];
            var target = SectionWorld(truck, k, exclude);
            var move = target.Origin - body.GlobalPosition;

            body.GlobalBasis = target.Basis;
            body.Velocity = new Vector3(move.X, 0f, move.Z) / dt;
            body.MoveAndSlide();
            body.GlobalPosition = body.GlobalPosition with { Y = target.Origin.Y };

            var miss = (target.Origin - body.GlobalPosition) with { Y = 0 };
            if (miss.Length() > 0.02f)
                for (int i = 0; i < body.GetSlideCollisionCount(); i++)
                {
                    var hit = body.GetSlideCollision(i);
                    var n = hit.GetNormal() with { Y = 0 };
                    if (n.LengthSquared() < 0.01f) continue;
                    var lever = (hit.GetPosition() - body.GlobalPosition) with { Y = 0 };
                    truck.Train.Contacts.Add(new TrainContact(k, Train(lever), Train(n.Normalized()).Normalized()));
                    SectionHits++;
                }

            psi += truck.Articulation[k - 1];
            if (miss.Length() > 0.25f)
            {
                // held off its line: the angle it is really at, from its pin to where it stands
                var b = truck.Train.Bodies[k];
                var pin = k == 1 ? ToGlobal(new Vector3(0, 0, -truck.Train.Bodies[0].HitchZ))
                    : _sections[k - 2].ToGlobal(new Vector3(0, 0, -truck.Train.Bodies[k - 1].HitchZ));
                var toPin = Train((pin - body.GlobalPosition) with { Y = 0 });
                if (toPin.LengthSquared() > 0.01f && b.PivotZ > 0.1f)
                {
                    float actual = Mathf.Atan2(toPin.Y, toPin.X);
                    float change = Mathf.Wrap(actual - psi, -Mathf.Pi, Mathf.Pi);
                    if (Mathf.Abs(change) < 0.5f)
                    {
                        truck.Articulation[k - 1] = Mathf.Clamp(truck.Articulation[k - 1] + change, -b.Spec.MaxArticulation, b.Spec.MaxArticulation);
                        psi += change;
                    }
                }
            }
        }
    }

    /// <summary>Owner, each frame: the angles out to everyone, and the lamps and doors on every section's rig.</summary>
    private void PublishTrain(Truck truck)
    {
        TrainPose = new Vector4(truck.Articulation[0], truck.Articulation[1], truck.Articulation[2], 0f);
        for (int k = 1; k <= _sections.Count; k++)
            if (_sections[k - 1].GetNodeOrNull<Avatar.HeavyRig>("Visual") is { } rig) truck.Dress(rig, k, truck.Reversing);
    }

    /// <summary>A remote copy: the sections posed from the owner's angles, eased toward each new one.</summary>
    private void AnimateRemoteSections(float dt)
    {
        if (_remoteRide is not Truck truck) return;
        if (TrailerCode != _visualTrailer) { RefreshVisual(force: true); return; }
        float ease = 1f - Mathf.Exp(-15f * dt);
        var angles = new Vector3(TrainPose.X, TrainPose.Y, TrainPose.Z);
        for (int j = 0; j < Truck.MaxJoints; j++) _shownAngles[j] = Mathf.Lerp(_shownAngles[j], angles[j], ease);
        truck.SetAngles(new Vector3(_shownAngles[0], _shownAngles[1], _shownAngles[2]));
        _truckPitch = BodyPose.Basis.GetEuler().X;
        var exclude = TrainRids();
        bool reversing = (Mathf.RoundToInt(Anim.W) & 4) != 0;
        for (int k = 1; k <= _sections.Count && k < truck.SectionCount; k++)
        {
            var body = _sections[k - 1];
            body.GlobalTransform = SectionWorld(truck, k, exclude);
            if (body.GetNodeOrNull<Avatar.HeavyRig>("Visual") is { } rig) truck.Dress(rig, k, reversing);
        }
    }

    /// <summary>The camera's pull-in ray ignores the train it is looking along.</summary>
    private void ExcludeTrain(Godot.Collections.Array<Rid> rids)
    {
        foreach (var s in _sections) rids.Add(s.GetRid());
    }

    // ---- driving: before and after the train's step ---------------------------------------------

    /// <summary>The ignition and the clutch pedal into the truck, and last frame's section contacts are already in.</summary>
    private void PrepareTruck(Truck truck)
    {
        truck.EngineRunning = EngineOn;
        truck.Box.ClutchHeld = !Npc && RideControls == null && PlayerInput.Held(PlayerInput.Clutch);
    }

    /// <summary>What the step decided that the player hears about: a stall, a grind, the air, a rollover.</summary>
    private bool AfterTruckStep(Truck truck)
    {
        if (truck.Box.Stalled)
        {
            truck.Box.Stalled = false;
            EngineOn = false;
            EngineToggled?.Invoke(false);
        }
        switch (truck.Box.Event)
        {
            case "stall": Announced?.Invoke("STALLED", false); break;
            case "grind": Announced?.Invoke(InputHints.Format("GRIND — clutch ({clutch}) first"), false); break;
            case "overrev": Announced?.Invoke("Too fast for that gear", false); break;
            case "air": Announced?.Invoke("LOW AIR — SPRING BRAKES ON", false); break;
        }
        truck.Box.Event = null;
        if (truck.Train.Rolling >= 0)
        {
            Announced?.Invoke(truck.Train.Rolling == 0 ? "ROLLED OVER!" : "THE TRAILER ROLLED OVER!", false);
            WreckVehicle();
            return true;
        }
        return false;
    }

    // ---- the truck's own controls ---------------------------------------------------------------

    /// <summary>Keys that mean something only in a truck or a bus. True when this event was one of them.</summary>
    private bool HandleTruckInput(InputEvent e, Truck truck)
    {
        if (!e.IsPressed() || e.IsEcho()) return false;
        float u = _motion.Speed * Mathf.Cos(_motion.Slip);
        var box = truck.Box;
        if (e.IsActionPressed(PlayerInput.Couple)) { ToggleCouple(truck); return true; }
        if (e.IsActionPressed(PlayerInput.ShiftUp)) { box.ShiftUp(u); return true; }
        if (e.IsActionPressed(PlayerInput.ShiftDown)) { box.ShiftDown(u); return true; }
        for (int g = 0; g < PlayerInput.Gates.Length; g++)
            if (e.IsActionPressed(PlayerInput.Gates[g])) { box.SelectGate(g + 1, u); return true; }
        if (e.IsActionPressed(PlayerInput.GearReverse)) { box.SelectGate(-1, u); return true; }
        if (e.IsActionPressed(PlayerInput.GearNeutral)) { box.SelectGate(0, u); return true; }
        if (e.IsActionPressed(PlayerInput.RetarderUp))
        {
            box.RetarderLevel = Mathf.Min(box.RetarderLevel + 1, HeavyDriveline.RetarderLevels);
            return true;
        }
        if (e.IsActionPressed(PlayerInput.RetarderDown))
        {
            box.RetarderLevel = Mathf.Max(box.RetarderLevel - 1, 0);
            return true;
        }
        if (e.IsActionPressed(PlayerInput.LightsToggle)) { truck.Headlights = !truck.Headlights; return true; }
        if (!truck.IsBus) return false;
        bool stopped = GroundSpeed < 1f;
        if (e.IsActionPressed(PlayerInput.CarDoor) && truck.DoorCount > 0)
        {
            if (!stopped) { Announced?.Invoke("Stop to open the doors", false); return true; }
            bool open = truck.DoorsOpen == 0;
            truck.DoorsOpen = open ? (byte)((1 << truck.DoorCount) - 1) : (byte)0;
            // a city bus kneels for its passengers when the doors open, and rises when they shut
            if (truck.Spec.Class != HeavyClass.Coach) truck.Kneeling = open;
            return true;
        }
        if (e.IsActionPressed(PlayerInput.Kneel) && truck.Spec.Class != HeavyClass.Coach)
        {
            if (stopped) truck.Kneeling = !truck.Kneeling;
            return true;
        }
        if (e.IsActionPressed(PlayerInput.Destination))
        {
            truck.Destination = (truck.Destination + 1) % Mathf.Max(1, truck.Spec.Look.Destinations.Length);
            return true;
        }
        return false;
    }

    // ---- coupling ------------------------------------------------------------------------------

    /// <summary>The lone trailer whose pivot this truck's hitch is under, lined up well enough to couple, or null.</summary>
    public VehicleBody? CoupleCandidate(Truck truck)
    {
        if (truck.Trailer != null || truck.Spec.Takes == Coupling.None) return null;
        var hitch = ToGlobal(truck.HitchNode with { Y = 0 });
        // a fifth wheel's jaws take a kingpin at an angle; a drawbar eye swings on the hitch
        float tolerance = truck.Spec.Takes == Coupling.Drawbar ? 1.2f : 0.9f;
        return Vehicles?.NearestTrailer(hitch, CoupleReach, v =>
            truck.Accepts(v.Trailer!.Spec)
            && Mathf.Abs(Mathf.Wrap(v.Rotation.Y - Rotation.Y, -Mathf.Pi, Mathf.Pi)) < tolerance);
    }

    /// <summary>{couple}: drops the trailer where it stands, or backs onto the one whose pivot is over the hitch.</summary>
    private void ToggleCouple(Truck truck)
    {
        if (GroundSpeed > 1.5f) { Announced?.Invoke("Stop to couple", false); return; }
        if (truck.Trailer != null) { DropTrailer(truck); return; }
        if (truck.Spec.Takes == Coupling.None) return;
        if (CoupleCandidate(truck) is not { } target)
        {
            Announced?.Invoke(truck.Spec.Takes == Coupling.FifthWheel
                ? "Back the fifth wheel under a trailer's kingpin" : "Back the hitch up to a drawbar trailer's eye", false);
            return;
        }
        Vehicles!.Claim(target, state =>
        {
            if (_ride is not Truck t) { Vehicles?.Park(state); return; }
            float yaw = Mathf.Wrap(state.Yaw - Rotation.Y, -Mathf.Pi, Mathf.Pi);
            if (!t.Couple(state.Train, new Vector3(yaw, state.Angles.X, state.Angles.Y))) { Vehicles?.Park(state); return; }
            TrailerCode = t.TrailerCode;
            RefreshVisual(force: true);
            Announced?.Invoke("COUPLED", true);
        });
    }

    /// <summary>Uncouples: the trailer stays in the world where it stands, at its own angles.</summary>
    private void DropTrailer(Truck truck)
    {
        var (code, at, yaw, angles) = truck.Uncouple();
        if (code == 0) return;
        var pos = ToGlobal(new Vector3(-at.Y, 0f, -at.X));
        if (_sections.Count > 0) pos.Y = _sections[0].GlobalPosition.Y;
        Vehicles?.Park(new VehicleState(RideKind.Trailer, pos, Rotation.Y + yaw, Vector3.Zero, 400f, false, false, 0f,
            VehicleState.Now, Train: code, Angles: angles));
        TrailerCode = 0;
        RefreshVisual(force: true);
        Announced?.Invoke("UNCOUPLED", true);
    }

    /// <summary>
    /// The travel picker's trailer rows: coupled at once behind a stopped truck that takes it, or
    /// put in the world 14 m ahead to back onto. False when neither can be done here.
    /// </summary>
    public bool SpawnTrailer(int index, float load)
    {
        int code = TrailerCatalog.Code(index, load);
        if (TrailerCatalog.For(code) is not { } spec) return false;
        if (_ride is Truck truck && truck.Trailer == null && truck.Accepts(spec))
        {
            if (GroundSpeed > 1.5f) return false;
            truck.Couple(code);
            TrailerCode = truck.TrailerCode;
            RefreshVisual(force: true);
            return true;
        }
        if (_ride != null || Vehicles == null) return false;
        var ahead = -GlobalTransform.Basis.Z with { Y = 0 };
        var pos = GlobalPosition + ahead.Normalized() * 14f;
        if (Terrain != null && Terrain.TryGetHeight(pos, out float g)) pos.Y = g;
        Vehicles.Park(new VehicleState(RideKind.Trailer, pos, Rotation.Y, Vector3.Zero, 400f, false, false, 0f,
            VehicleState.Now, Train: code));
        return true;
    }

    /// <summary>Contacts the sections behind the cab have made since this player was made (for the probes).</summary>
    public int SectionHits { get; private set; }

    /// <summary>A picker's load choice for the next truck or bus: its cargo or its passengers, 0..1.</summary>
    public float NextLoad { get; set; } = 0.5f;
}
