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
    private static readonly float ThighLength = 0.42f;
    private static readonly float ShinLength = 0.40f;

    /// <summary>Hip joint, matching the cycling rig and the saddle in <see cref="BikeMeshBuilder"/>.</summary>
    private static readonly Vector3 HipCentre = new(0, 0.905f, -0.050f);

    private static readonly Vector3 BottomBracket = new(0, 0.270f, -0.020f);
    private const float CrankLength = 0.170f;
    private const float PedalOffset = 0.070f;

    private MeshInstance3D _cranks = null!;
    private readonly MeshInstance3D[] _legs = new MeshInstance3D[2];
    private HumanPalette _palette = HumanPalette.Default;

    private float _crankAngle;
    private float _cadenceRpm;

    /// <summary>Live cadence. Drives the crank; set it from the router and the legs follow.</summary>
    public float CadenceRpm
    {
        get => _cadenceRpm;
        set => _cadenceRpm = Mathf.Max(0f, value);
    }

    /// <summary>
    /// Parks the cranks at a fixed angle. Only for the preview: which way a crank turns cannot
    /// be judged from a single frame, so checking it needs two chosen ones.
    /// </summary>
    public void SetCrankAngle(float radians)
    {
        _crankAngle = Mathf.Wrap(radians, 0f, Mathf.Tau);
        _cranks.Mesh = BikeMeshBuilder.BuildCranks(BikePalette.ForRider(_riderIndex), _crankAngle);
        UpdateLegs();
    }

    public static Cyclist Create(int riderIndex = 0) => new()
    {
        Name = "Cyclist",
        _palette = HumanPalette.ForRider(riderIndex),
        _riderIndex = riderIndex,
    };

    private int _riderIndex;

    public override void _Ready()
    {
        var bikePalette = BikePalette.ForRider(_riderIndex);
        var material = HumanMeshBuilder.Material();

        AddChild(new MeshInstance3D
        {
            Name = "Bike",
            Mesh = BikeMeshBuilder.Build(bikePalette, includeCranks: false),
            MaterialOverride = material,
        });

        // rider without legs: those are separate so they can be driven by the cranks
        AddChild(new MeshInstance3D
        {
            Name = "Rider",
            Mesh = HumanMeshBuilder.Build(_palette, HumanPose.Cycling, includeLegs: false, helmet: true),
            MaterialOverride = material,
        });

        _cranks = new MeshInstance3D
        {
            Name = "Cranks",
            Mesh = BikeMeshBuilder.BuildCranks(bikePalette),
            MaterialOverride = material,
        };
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
        if (_cadenceRpm <= 0.01f) return;

        _crankAngle = Mathf.Wrap(_crankAngle + (float)(_cadenceRpm / 60.0 * Mathf.Tau * delta), 0f, Mathf.Tau);
        _cranks.Mesh = BikeMeshBuilder.BuildCranks(BikePalette.ForRider(_riderIndex), _crankAngle);
        UpdateLegs();
    }

    /// <summary>
    /// Rebuilds both legs for the current crank angle.
    ///
    /// <para>
    /// Only the legs are rebuilt, and only while pedalling — two short tube pairs, which is far
    /// less work than it sounds and keeps the knee exactly on the circle the pedal describes.
    /// </para>
    /// </summary>
    private void UpdateLegs()
    {
        for (int i = 0; i < 2; i++)
        {
            float angle = _crankAngle + i * Mathf.Pi;
            float side = i == 0 ? PedalOffset : -PedalOffset;

            var hip = HipCentre + new Vector3(side * 1.28f, 0, 0);

            // Sign matches BikeMeshBuilder.Cranks: a crank at the front travels downward next,
            // because the bike faces +Z. The leg is solved from wherever the pedal is, so the
            // two can only disagree if this expression does — hence the duplicated minus.
            var pedal = BottomBracket + new Vector3(
                side, -Mathf.Sin(angle) * CrankLength, Mathf.Cos(angle) * CrankLength);

            // the knee leads the hip on a bicycle; +Z is forward in author space
            var knee = Limb.Solve(hip, pedal, ThighLength, ShinLength, new Vector3(0, 0, 1));

            var scratch = new MeshScratch();
            scratch.Tube(hip, knee, 0.088f, 0.062f, _palette.Shorts, 6);
            scratch.Tube(knee, pedal, 0.062f, 0.042f, _palette.Skin, 6);
            scratch.Box(pedal, new Vector3(0.058f, 0.045f, 0.115f), _palette.Shoes);

            _legs[i].Mesh = scratch.Build();
        }
    }

}
