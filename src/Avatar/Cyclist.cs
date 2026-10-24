using System;
using System.Collections.Generic;
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
    /// <summary>The bike and the rider's fit to it (#1124): the hips, pedals and cranks all come from here; per bike since #1159.</summary>
    public Player.BikeFrame Frame { get; private init; } = Player.BikeFrame.Default;

    private MeshInstance3D _cranks = null!;
    private HumanPalette _palette = HumanPalette.Default;
    private BikePalette _bikePalette = BikePalette.Default;
    /// <summary>The rider and its legs: one skinned figure (#1202), posed from the position, the cranks and the wind.</summary>
    private SkinnedFigure _rider = null!;
    private readonly FigureWind _wind = new();
    // the fluttering rider is posed again when the wind moves by a cm/s (#221)
    private Vector3 _riderWindKey = new(float.NaN, 0, 0);
    private bool Flutters => HumanMeshBuilder.Flutters(_palette.Outfit);

    private float _crankAngle;
    private float _cadenceRpm;
    private MeshInstance3D? _front;
    private float _steer;

    // ---- the riding position (#1124) ----
    /// <summary>Everything that rocks under a standing rider: the bike, the rider, the cranks and legs.</summary>
    private Node3D? _sway;
    private Player.RidingPosition _position, _wantPosition;
    private float _wantFor, _swayAmp;

    /// <summary>The pose being left while <see cref="_blend"/> runs from 0 to 1 toward <see cref="_position"/>.</summary>
    private Player.RidingPosition _from;
    private float _blend = 1f;
    private Vector3 _windNow;
    /// <summary>The rider's upper body turned against the standing bike's rock, rad: posed when it changes.</summary>
    private float _counterSway;

    /// <summary>A change of position takes this long, s: a rider sinks onto the drops in about half a second.</summary>
    private const float BlendTime = 0.45f;

    /// <summary>A new position shows once it has been asked for this long, s: a rider does not flick between them.</summary>
    private const float PositionHold = 0.3f;

    /// <summary>
    /// How far the bike rocks under a standing rider, rad (~7°): toward the side away from the pushing
    /// pedal, once a crank turn, the rider's upper body staying more upright than the bike.
    /// </summary>
    private const float StandingSway = 0.12f, RiderCounterSway = 0.7f;

    /// <summary>
    /// The rider's position (#1124): hands on the hoods, on the drops, tucked on the tops, or standing.
    /// The pose is solved from the bike's contact points (<see cref="Player.BikeFrame.Fit"/>).
    /// </summary>
    public Player.RidingPosition RidingPose
    {
        get => _position;
        set => _wantPosition = value;
    }

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
    /// Steps of a crank revolution the cranks are drawn at (#221): they depend on the crank angle
    /// alone, so each step is built once and shared by every bike of the same colours, and the legs
    /// are posed on the same step. 128 steps = 2.8°.
    /// </summary>
    private const int Steps = 128;
    // ponytail: one entry set per distinct palette, never evicted; a few hundred tiny meshes per
    // colour scheme. Evict by palette if riders with unique colours ever come by the thousand.
    private static readonly Dictionary<(Player.BikeFrame, BikePalette, int), ArrayMesh> CrankSteps = new();
    private int _shownStep = -1;
    /// <summary>Whether the rider shown was built smooth (<see cref="HumanMeshBuilder.SmoothFigures"/>).</summary>
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

    /// <summary>A rider on their own bike, in <paramref name="outfit"/> (#251), on <paramref name="bike"/> (#1159; the road bike by default).</summary>
    public static Cyclist Create(int riderIndex = 0, Outfit outfit = default, Player.BicycleSpec? bike = null) => new()
    {
        Name = "Cyclist",
        _palette = HumanPalette.ForRider(riderIndex) with { Outfit = outfit },
        _bikePalette = bike?.Look ?? BikePalette.ForRider(riderIndex),
        Frame = bike == null ? Player.BikeFrame.Default : Player.BikeFrame.For(bike),
    };

    /// <summary>The rider in each position, pedalling (#1124): the model viewer's way to see them side by side.</summary>
    [Core.Showcase("Rides", "Road bike riding position")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> ShowcasePositions()
    {
        foreach (var position in new[] { Player.RidingPosition.Hoods, Player.RidingPosition.Drops,
                     Player.RidingPosition.Tuck, Player.RidingPosition.Standing })
            yield return (position.ToString(), () =>
            {
                var c = Create(4);
                c._position = c._wantPosition = c._from = position;
                c.CadenceRpm = position == Player.RidingPosition.Standing ? 60f : 85f;
                return c;
            });
    }

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

        _sway = new Node3D { Name = "Sway" };
        AddChild(_sway);
        _sway.AddChild(new MeshInstance3D
        {
            Name = "Bike",
            Mesh = BikeMeshBuilder.Build(bikePalette, includeCranks: false, frame: Frame, parts: BikeMeshBuilder.Parts.Rear),
            MaterialOverride = material,
        });
        // the fork, front wheel and bars turn about the steering axis (#1124)
        _front = new MeshInstance3D
        {
            Name = "Front",
            Mesh = BikeMeshBuilder.Build(bikePalette, includeCranks: false, frame: Frame, parts: BikeMeshBuilder.Parts.Front),
            MaterialOverride = material,
        };
        _sway.AddChild(_front);
        ApplySteer();

        // the rider and its legs, built once and posed by its bones: the legs from the cranks
        _rider = new SkinnedFigure { Name = "Rider", MaterialOverride = material };
        _riderSmooth = HumanMeshBuilder.SmoothFigures;
        _rider.Show(Look);
        _sway.AddChild(_rider);

        _cranks = new MeshInstance3D { Name = "Cranks", MaterialOverride = material };
        _sway.AddChild(_cranks);

        UpdateLegs();
        PoseRider();
    }

    public override void _Process(double delta)
    {
        // a restyle to or from a lit style (#311): the rider is a new look, parked or not
        if (HumanMeshBuilder.SmoothFigures != _riderSmooth)
        {
            _riderSmooth = HumanMeshBuilder.SmoothFigures;
            _rider.Show(Look);
            PoseRider();
        }
        UpdatePosition((float)delta);
        // a skirt streams back in the wind of the ride (#251): measured from the bike's own motion
        if (Flutters)
        {
            var wind = _wind.Update(_rider, (float)delta);
            var key = (wind * 100f).Round();
            if (key != _riderWindKey)
            {
                _riderWindKey = key;
                _windNow = wind;
                PoseRider();
            }
        }
        if (_cadenceRpm <= 0.01f) return;

        _crankAngle = Mathf.Wrap(_crankAngle + (float)(_cadenceRpm / 60.0 * Mathf.Tau * delta), 0f, Mathf.Tau);
        UpdateLegs();
    }

    /// <summary>
    /// The riding position's pose, once it has been asked for <see cref="PositionHold"/>: the rider is
    /// built again (only then, not per frame), and the legs from the new hips. Standing, the bike rocks
    /// with the cranks.
    /// </summary>
    private void UpdatePosition(float dt)
    {
        if (_blend < 1f)
        {
            // mid-change: every joint eases toward the new pose; a newer request waits for it to land
            _blend = Mathf.Min(1f, _blend + dt / BlendTime);
            if (_blend >= 1f) _from = _position;
            PoseRider();
        }
        else
        {
            _wantFor = _wantPosition == _position ? 0f : _wantFor + dt;
            if (_wantFor >= PositionHold)
            {
                _from = _position;
                _position = _wantPosition;
                _wantFor = 0f;
                _blend = 0f;
            }
        }
        if (_sway == null) return;
        float target = _position == Player.RidingPosition.Standing && _cadenceRpm > 1f ? StandingSway : 0f;
        _swayAmp += (target - _swayAmp) * (1f - Mathf.Exp(-dt / 0.3f));
        // the +X pedal pushes down from its front (0) to the bottom (π): the bike leans to −X under it,
        // which is a positive turn about the forward axis
        float sway = _swayAmp * Mathf.Sin(_crankAngle);
        _sway.Rotation = new Vector3(0, 0, sway);
        // the upper body stays more upright than the bike: its bones turn back, the legs stay on the pedals
        float counter = -RiderCounterSway * sway;
        if (Mathf.Abs(counter - _counterSway) > 1e-4f || (counter == 0f) != (_counterSway == 0f))
        {
            _counterSway = Mathf.Abs(counter) < 1e-4f ? 0f : counter;
            PoseRider();
        }
    }

    /// <summary>How far through the change of pose, eased in and out: 1 when settled.</summary>
    private float BlendShown => _blend >= 1f ? 1f : Mathf.SmoothStep(0f, 1f, _blend);

    /// <summary>The rider's look: its colours and clothes, the helmet, its legs pedalling.</summary>
    private FigureLook Look => new(_palette, Helmet: true, Pedalling: true);

    /// <summary>
    /// The rider in the pose shown now (settled, or part way through a change), its legs on the
    /// cranks' step, in the wind of the ride when the clothes flutter: bones only (#1202), and only
    /// on a change, never per frame while settled.
    /// </summary>
    private void PoseRider()
    {
        if (_rider == null) return;
        float stepAngle = Mathf.Max(_shownStep, 0) * Mathf.Tau / Steps;
        HumanMeshBuilder.PoseCyclist(_rider, Frame, _from, _position, BlendShown, stepAngle, _counterSway, Flutters ? _windNow : Vector3.Zero);
    }

    /// <summary>
    /// Puts the cranks at the current crank angle's <see cref="Steps"/> step, building that step's
    /// mesh the first time any bike of these colours reaches it, and the legs on its pedals.
    /// </summary>
    private void UpdateLegs()
    {
        int step = Mathf.RoundToInt(_crankAngle / Mathf.Tau * Steps) % Steps;
        if (step == _shownStep || _cranks == null) return;
        _shownStep = step;
        float stepAngle = step * Mathf.Tau / Steps;
        if (!CrankSteps.TryGetValue((Frame, _bikePalette, step), out var cranks))
            CrankSteps[(Frame, _bikePalette, step)] = cranks = BikeMeshBuilder.BuildCranks(_bikePalette, stepAngle, Frame);
        _cranks.Mesh = cranks;
        PoseRider();
    }

}
