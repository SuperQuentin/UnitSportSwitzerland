using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A rider on a road bike, with cranks and legs that turn together.
///
/// <para>
/// Split into three meshes rather than one, because the pedalling is the whole point: the frame
/// and the rider's upper body never move, so they are baked once, while the cranks and each leg
/// are their own nodes that rotate. Rebuilding a single mesh every frame to animate a crank
/// would be the obvious approach and about the most expensive way to do it.
/// </para>
///
/// <para>
/// The legs are driven from the crank rather than keyframed. Given where the pedal is, the knee
/// follows from the two bone lengths — the same two-bone solve a rig would use, and it means
/// cadence, crank position and foot position can never drift out of agreement.
/// </para>
/// </summary>
public partial class Cyclist : Node3D
{
    /// <summary>The bike and the rider's fit to it (#1124): the hips, pedals and cranks all come from here.</summary>
    private static Player.BikeFrame Frame => Player.BikeFrame.Default;

    private MeshInstance3D _cranks = null!;
    private readonly MeshInstance3D[] _legs = new MeshInstance3D[2];
    private HumanPalette _palette = HumanPalette.Default;
    private BikePalette _bikePalette = BikePalette.Default;
    private MeshInstance3D _rider = null!;
    private readonly FigureWind _wind = new();
    // the fluttering rider keeps one mesh, rebuilt in place when the wind moves by a cm/s (#221)
    private ArrayMesh? _riderMesh;
    private Vector3 _riderWindKey = new(float.NaN, 0, 0);
    private bool Flutters => HumanMeshBuilder.Flutters(_palette.Outfit);

    private float _crankAngle;
    private float _cadenceRpm;
    private MeshInstance3D? _front;
    private float _steer;

    /// <summary>
    /// The bars' angle, rad, + to the rider's right (#1124): the fork, front wheel and bars turn
    /// about the steering axis. The hands stay where the rig put them, so this is for the few degrees
    /// a bike is steered at speed, not a walking-pace full lock.
    /// </summary>
    public float SteerAngle
    {
        get => _steer;
        set
        {
            if (value == _steer) return;
            _steer = value;
            ApplySteer();
        }
    }

    private void ApplySteer()
    {
        if (_front == null) return;
        // facing +Z the rider's right is −X, and a positive turn about the steerer (up) swings the
        // front toward +X: steering right is a negative angle
        var pivot = Frame.HeadTop;
        var turn = new Basis(Frame.SteererUp, -_steer);
        _front.Transform = new Transform3D(turn, pivot - turn * pivot);
    }

    /// <summary>
    /// Steps of a crank revolution the cranks and legs are drawn at (#221): both depend on the crank
    /// angle alone, so each step is built once and shared by every rider of the same colours and clothes,
    /// instead of three new meshes per rider per frame. 128 steps = 2.8°, a foot at most 4 mm off
    /// the pedal angle it would have been solved for.
    /// </summary>
    private const int Steps = 128;
    // ponytail: one entry set per distinct palette, never evicted; a few hundred tiny meshes per
    // colour scheme. Evict by palette if riders with unique colours ever come by the thousand.
    private static readonly Dictionary<(BikePalette, int), ArrayMesh> CrankSteps = new();
    private static readonly Dictionary<(HumanPalette, int, int, bool), ArrayMesh> LegSteps = new();
    private int _shownStep = -1;
    /// <summary>Whether the rider and legs shown were built smooth (<see cref="HumanMeshBuilder.SmoothFigures"/>).</summary>
    private bool _riderSmooth;

    /// <summary>Live cadence. Drives the crank; set it from the router and the legs follow.</summary>
    public float CadenceRpm
    {
        get => _cadenceRpm;
        set => _cadenceRpm = Mathf.Max(0f, value);
    }

    /// <summary>Where the cranks are, radians — replicated, so a remote rider's legs match the owner's.</summary>
    public float CrankAngle => _crankAngle;

    /// <summary>
    /// Puts the cranks at an angle: a remote rider taking the owner's replicated one, or the
    /// preview parking them (which way a crank turns cannot be judged from a single frame).
    /// </summary>
    public void SetCrankAngle(float radians)
    {
        _crankAngle = Mathf.Wrap(radians, 0f, Mathf.Tau);
        UpdateLegs();
    }

    /// <summary>A rider on their own bike, in <paramref name="outfit"/> (#251).</summary>
    public static Cyclist Create(int riderIndex = 0, Outfit outfit = default) => new()
    {
        Name = "Cyclist",
        _palette = HumanPalette.ForRider(riderIndex) with { Outfit = outfit },
        _bikePalette = BikePalette.ForRider(riderIndex),
    };

    /// <summary>
    /// A rider whose colour comes from an existing tint rather than a rider index.
    ///
    /// <para>
    /// GPX playback already colours each runner from a fixed six-entry palette so the
    /// leaderboard and the avatar agree — <see cref="Runner"/>'s human avatar is built the same
    /// way, jersey and helmet set from the same <c>Color</c>. Rebuilding that tint through
    /// <c>HumanPalette.ForRider(index)</c>'s hue formula would pick a different, unrelated
    /// colour, so a bike ghost's leaderboard row and its rider would disagree.
    /// </para>
    /// </summary>
    public static Cyclist CreateWithTint(Color tint) => new()
    {
        Name = "Cyclist",
        _palette = (HumanPalette.Default with { Jersey = tint, Helmet = tint }).With(Appearance.ForSeed((int)tint.ToRgba32())),
        _bikePalette = BikePalette.Default with { Accent = tint },
    };

    public override void _Ready()
    {
        var bikePalette = _bikePalette;
        // clothes may carry a finish, and the face is drawn, only by the figure shader (#251, #394)
        Material material = HumanMeshBuilder.FigureMaterial();

        AddChild(new MeshInstance3D
        {
            Name = "Bike",
            Mesh = BikeMeshBuilder.Build(bikePalette, includeCranks: false, parts: BikeMeshBuilder.Parts.Rear),
            MaterialOverride = material,
        });
        // the fork, front wheel and bars turn about the steering axis (#1124)
        _front = new MeshInstance3D
        {
            Name = "Front",
            Mesh = BikeMeshBuilder.Build(bikePalette, includeCranks: false, parts: BikeMeshBuilder.Parts.Front),
            MaterialOverride = material,
        };
        AddChild(_front);
        ApplySteer();

        // rider without legs: those are separate so they can be driven by the cranks
        _rider = new MeshInstance3D
        {
            Name = "Rider",
            Mesh = HumanMeshBuilder.Build(_palette, HumanPose.Cycling, includeLegs: false, helmet: true),
            MaterialOverride = material,
        };
        _riderSmooth = HumanMeshBuilder.SmoothFigures;
        AddChild(_rider);

        _cranks = new MeshInstance3D { Name = "Cranks", MaterialOverride = material };
        AddChild(_cranks);

        for (int i = 0; i < 2; i++)
        {
            _legs[i] = new MeshInstance3D { Name = i == 0 ? "LegR" : "LegL", MaterialOverride = material };
            AddChild(_legs[i]);
        }

        UpdateLegs();
    }

    public override void _Process(double delta)
    {
        // a restyle to or from a lit style (#311): the rider and legs are built again, parked or not
        if (HumanMeshBuilder.SmoothFigures != _riderSmooth)
        {
            _riderSmooth = HumanMeshBuilder.SmoothFigures;
            _rider.Mesh = HumanMeshBuilder.Build(_palette, HumanPose.Cycling, includeLegs: false, helmet: true);
            _shownStep = -1;
            UpdateLegs();
        }
        // a skirt streams back in the wind of the ride (#251): measured from the bike's own motion
        if (Flutters)
        {
            var wind = _wind.Update(_rider, (float)delta);
            var key = (wind * 100f).Round();
            if (key != _riderWindKey)
            {
                _riderWindKey = key;
                _rider.Mesh = HumanMeshBuilder.Build(_palette with { Wind = wind }, HumanPose.Cycling,
                    includeLegs: false, helmet: true, into: _riderMesh ??= new ArrayMesh());
            }
        }
        if (_cadenceRpm <= 0.01f) return;

        _crankAngle = Mathf.Wrap(_crankAngle + (float)(_cadenceRpm / 60.0 * Mathf.Tau * delta), 0f, Mathf.Tau);
        UpdateLegs();
    }

    /// <summary>
    /// Puts the cranks and both legs at the current crank angle's <see cref="Steps"/> step,
    /// building that step's meshes the first time any rider of these colours reaches it.
    /// </summary>
    private void UpdateLegs()
    {
        int step = Mathf.RoundToInt(_crankAngle / Mathf.Tau * Steps) % Steps;
        if (step == _shownStep || _cranks == null) return;
        _shownStep = step;
        float stepAngle = step * Mathf.Tau / Steps;
        if (!CrankSteps.TryGetValue((_bikePalette, step), out var cranks))
            CrankSteps[(_bikePalette, step)] = cranks = BikeMeshBuilder.BuildCranks(_bikePalette, stepAngle);
        _cranks.Mesh = cranks;
        for (int i = 0; i < 2; i++)
        {
            var key = (_palette, i, step, HumanMeshBuilder.SmoothFigures);
            if (!LegSteps.TryGetValue(key, out var leg)) LegSteps[key] = leg = BuildLeg(i, stepAngle);
            _legs[i].Mesh = leg;
        }
    }

    /// <summary>
    /// One leg, solved from where its pedal is: the knee stays exactly on the circle the pedal
    /// describes.
    /// </summary>
    private ArrayMesh BuildLeg(int i, float crankAngle)
    {
        int side = i == 0 ? 1 : -1;
        var hip = Frame.LegHip(side);
        // BikeFrame.Pedal carries the sign: a crank at the front travels downward next, because the
        // bike faces +Z, and the cranks are drawn from the same call, so the two cannot disagree
        var pedal = Frame.Pedal(crankAngle, side);

        // the knee leads the hip on a bicycle; +Z is forward in author space
        var knee = Limb.Solve(hip, pedal, Player.BikeFrame.ThighLength, Player.BikeFrame.ShinLength, new Vector3(0, 0, 1));

        var scratch = new MeshScratch();
        using var smoothing = scratch.Smoothing(HumanMeshBuilder.SmoothFigures);
        if (!_palette.Outfit.IsEmpty)
        {
            // dressed (#251): stockings, boots and trousers on the pedalling leg, the foot along the pedal
            HumanMeshBuilder.AppendLeg(scratch, _palette, hip, knee, pedal + new Vector3(0, 0.03f, -0.03f), pedal + new Vector3(0, 0, 0.09f));
            return scratch.Build();
        }
        scratch.Tube(hip, knee, 0.088f, 0.062f, _palette.Shorts, 6);
        scratch.Tube(knee, pedal, 0.062f, 0.042f, _palette.Skin, 6);
        scratch.Box(pedal, new Vector3(0.058f, 0.045f, 0.115f), _palette.Shoes);

        return scratch.Build();
    }

}
