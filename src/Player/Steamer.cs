using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// The CGN Belle Époque paddle steamer (#303, <c>RideKind</c> 123): a <see cref="Boat"/> on the same
/// model (<see cref="BoatCatalog.Steamer"/>: 518 t, a paddle shaft reversing through stop) that you
/// walk about on (<see cref="Decks"/>: the main deck, the saloon, the upper deck, the wheelhouse) and
/// drive from the wheelhouse: the telegraph steps from FULL ASTERN through STOP to FULL AHEAD
/// ({move_forward} / {move_back}, one step a press), the wheel ({move_left} / {move_right}), the
/// whistle ({horn}, held), the gangways ({car_door}, stopped). The deck is the posed visual's, so it
/// pitches and rolls with the hull (see <c>docs/notes/player/steamer.md</c>).
/// </summary>
public sealed class Steamer : Boat
{
    public Steamer() : base(RideKind.Steamer, BoatCatalog.Steamer) { }

    /// <summary>The telegraph's order, −<see cref="Telegraph.Max"/> (full astern) .. +<see cref="Telegraph.Max"/> (full ahead).</summary>
    public int Order { get; set; }

    /// <summary>The gangways' gates, open: bit 0 port, bit 1 starboard (the deck's doors 0 and 1).</summary>
    public byte DoorsOpen { get; set; }

    /// <summary>The whistle is blowing (held).</summary>
    public bool Whistling { get; set; }

    /// <summary>For probes: the whistle held as if {horn} were.</summary>
    public bool WhistleHeld { get; set; }

    public const int GangwayCount = 2;

    public override string Label => "Paddle steamer";
    public override string Blurb =>
        "{move_forward}/{move_back} ring the telegraph ahead/astern, {move_left}/{move_right} the wheel, {horn} the whistle; walk aboard by a gangway";
    public override float MaxHealth => 3000f;
    /// <summary>The capsule that carries it, its origin on the keel: it meets the lake bed where the hull does.</summary>
    public override float BodyRadius => 3f;
    public override float BodyHeight => 5f;
    /// <summary>The hull boxes start under the waterline (1.64 m).</summary>
    public override float HullLift => 1.2f;
    /// <summary>Behind and above the whole ship (a train's rule: 5 m + 0.95 of its length back).</summary>
    public override float ChaseDistance => 5f + 0.95f * SteamerLines.Length;
    public override float ChaseHeight => 24f;
    public override float ChasePitch => -0.2f;
    public override float EyeHeight => 7f;
    public override float BaseFov => 62f;
    public override float MaxFov => 66f;
    public override float FovSpeed => 8f;
    public override float DismountSpeed => 1.5f;

    /// <summary>The port gangway, node space: where a player gets in from outside.</summary>
    public override Vector3 EntryPoint => BoatMeshBuilder.Flip(new Vector3(SteamerMeshBuilder.DeckHalf(SteamerMeshBuilder.Z(SteamerMeshBuilder.GangFrom + 1f)) + 0.6f,
        SteamerMeshBuilder.DeckY, SteamerMeshBuilder.Z(SteamerMeshBuilder.GangFrom + 1f)));

    /// <summary>The hull to the upper deck, and the paddle boxes across it, node space.</summary>
    public override (Aabb Lower, Aabb Upper)? HullBoxes => (
        new Aabb(new Vector3(-4.35f, 0f, -SteamerLines.SternZ), new Vector3(8.7f, SteamerMeshBuilder.UpperY, 2f * SteamerLines.SternZ)),
        new Aabb(new Vector3(-SteamerMeshBuilder.BoxOut, SteamerMeshBuilder.BoxBottom, -SteamerLines.WheelZ - 4.75f),
            new Vector3(2f * SteamerMeshBuilder.BoxOut, SteamerMeshBuilder.BoxTop - SteamerMeshBuilder.BoxBottom, 9.5f)));

    /// <summary>
    /// Parked: the hull from under the waterline to the upper deck, not the bounding box (a 16 m
    /// wide wall the length of the ship would stop a swimmer metres off its bow).
    /// </summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox => (new Vector3(0, 3.4f, 0), new Vector3(8.7f, 4.4f, 2f * SteamerLines.SternZ));

    public override SeatAnchor[] Seats => SteamerMeshBuilder.Parts().Seats;

    private static VehicleDeck[]? _decks;
    public override VehicleDeck[] Decks => _decks ??= new[] { SteamerMeshBuilder.Parts().Deck };

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        SteamerRig.Create(HumanPalette.ForRider(riderIndex) with { Outfit = outfit });

    public override Node3D BuildParkedVisual(int riderIndex) => SteamerRig.Create(null);

    /// <summary>
    /// The steam engine, heard through <see cref="EngineSynth"/>: two double-acting cylinders, four
    /// exhaust beats a turn of the shaft (46 rpm: three a second), up a tall funnel.
    /// </summary>
    public override EngineProfile Sound => _steam ??= new EngineProfile
    {
        Cylinders = 2, IdleRpm = 20f, MaxRpm = 4f * BoatCatalog.Steamer.MaxRpm, PipeM = 3.2f, Unevenness = 0.4f,
    };
    private EngineProfile? _steam;

    // ---- the telegraph ----------------------------------------------------------------------

    private bool _ahead, _astern;
    private float _held;

    /// <summary>
    /// The lever pushed (<paramref name="ahead"/>, <paramref name="astern"/>: 0..1): a step each
    /// press, another every 0.35 s while it is held past 0.6 s.
    /// </summary>
    public void StepTelegraph(float ahead, float astern, float dt)
    {
        bool up = ahead > 0.5f, down = astern > 0.5f && !up;
        if (up && !_ahead || down && !_astern)
        {
            Order = Telegraph.Clamp(Order + (up ? 1 : -1));
            _held = 0f;
        }
        else if (up || down)
        {
            _held += dt;
            if (_held > 0.6f)
            {
                _held -= 0.35f;
                Order = Telegraph.Clamp(Order + (up ? 1 : -1));
            }
        }
        _ahead = up;
        _astern = down;
    }

    /// <summary>What the bridge asks of the model: the telegraph's order on the shaft, the wheel.</summary>
    public BoatControls Helm(float steer)
    {
        float lever = Telegraph.Lever(Order);
        return new BoatControls(Mathf.Max(0f, lever), Mathf.Max(0f, -lever), steer);
    }

    // ---- what others see -----------------------------------------------------------------------

    /// <summary>
    /// The shaft (signed, the wheels turn either way), the thrust share, <see cref="Boat.Heave"/>, and
    /// in W: wet (0..1) + 2 airborne + 4 whistling + 8 × the gangways' bits.
    /// </summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(State.Shaft, Mathf.Clamp(Mathf.Abs(State.Thrust) / Mathf.Max(1f, Spec.StaticThrust), 0f, 1f), Heave,
            Mathf.Clamp(State.Wet, 0f, 1f) * 0.99f + (State.Airborne > 0.1f ? 2f : 0f) + (Whistling ? 4f : 0f) + (DoorsOpen & 3) * 8f);

    /// <summary>The gangways' bits in a published pose's W.</summary>
    public static byte DoorsOf(Vector4 pose) => (byte)(Mathf.FloorToInt(pose.W / 8f) & 3);
    public static bool WhistleOf(Vector4 pose) => (Mathf.FloorToInt(pose.W / 4f) & 1) != 0;
    public static bool AfloatOf(Vector4 pose) => Mathf.PosMod(pose.W, 4f) < 2f;

    private Vector3 _lastAt;
    private bool _hasLast;
    private float _speed;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        var at = visual.GlobalPosition;
        if (_hasLast && dt > 0f)
            _speed = Mathf.Lerp(_speed, new Vector2(at.X - _lastAt.X, at.Z - _lastAt.Z).Length() / dt, 1f - Mathf.Exp(-6f * dt));
        _lastAt = at;
        _hasLast = true;
        if (visual is SteamerRig rig)
            rig.Animate(pose.X, _speed, AfloatOf(pose), WhistleOf(pose), pose.X * Telegraph.Max, DoorsOf(pose), dt);
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is SteamerRig rig)
            rig.Animate(State.Shaft, Mathf.Abs(State.WaterSpeed), State.Airborne <= 0.1f && State.Wet > 0.05f, Whistling, Order, DoorsOpen, dt);
    }
}
