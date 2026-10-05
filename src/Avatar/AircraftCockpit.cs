using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>
/// Where an aircraft's flight deck is (#421), authored (+Z forward, +X the left, the captain's side):
/// the captain's hip, the floor, the panel's aft face and middle, the glareshield, the pedestal, the
/// overhead panel, sidesticks or yokes; the cabin's seatbelt signs. The numbers of each type's layout.
/// </summary>
public sealed record CockpitLayout(
    bool Glass, int Engines, Vector3 Hip, float Floor,
    float PanelZ, float PanelY, float PanelHalf, float GlareY,
    float PedestalZ0, float PedestalZ1, float PedestalTop, float PedestalHalf,
    float OverheadY, float OverheadZ, bool Sidestick, float ConsoleTop, Vector3[] Signs)
{
    public static CockpitLayout A320 { get; } = new(true, 2, A320Layout.CaptainHip, A320Layout.FloorY,
        16.49f, 3.98f, A320MeshBuilder.CockpitPanelHalf, 4.41f,
        15.6f, 16.3f, A320Layout.FloorY + 0.44f, 0.15f,
        5.3f, 15.75f, true, A320Layout.FloorY + 0.6f,
        new[] { 0, 1, 2, 3, 4, 5, 6 }.Select(i => new Vector3(0f, 5.18f, 12.5f - i * 4f)).ToArray());

    public static CockpitLayout Freighter { get; } = new(false, 4, FreighterLayout.CaptainHip, FreighterLayout.FlightDeckY,
        FreighterLayout.PanelZ - 0.05f, 2.52f, FreighterMeshBuilder.CockpitPanelHalf, 2.93f,
        9.3f, 10.2f, FreighterLayout.FlightDeckY + 0.6f, 0.19f,
        FreighterLayout.FlightDeckCeilingY - 0.14f, 9.3f, false, 0f,
        new[] { new Vector3(0f, FreighterLayout.HoldCeilingY - 0.08f, 4.5f), new Vector3(0f, FreighterLayout.HoldCeilingY - 0.08f, -1.5f) });

    public static CockpitLayout An124 { get; } = new(false, 4, An124Layout.PilotHip, An124Layout.UpperFloorY,
        An124Layout.PanelZ, An124Layout.UpperFloorY + 0.65f, An124MeshBuilder.CockpitPanelHalf, An124Layout.UpperFloorY + 1.06f,
        An124Layout.PilotHip.Z + 0.05f, An124Layout.PilotHip.Z + 0.95f, An124Layout.UpperFloorY + 0.6f, 0.2f,
        An124Layout.UpperCeilingY - 0.17f, An124Layout.PilotHip.Z + 0.3f, false, 0f,
        new[] { new Vector3(0f, An124Layout.UpperCeilingY - 0.05f, 17f), new Vector3(0f, An124Layout.UpperCeilingY - 0.05f, 21f) });

    /// <summary>The captain's eye, authored.</summary>
    public Vector3 Eye => HumanMeshBuilder.DriverEye(Hip, AirlinerRig.PilotRecline);
}

/// <summary>
/// An aircraft's live flight deck (#421), a child of its <see cref="AirlinerRig"/> on every peer: the
/// moving controls (sidesticks or yokes, the thrust levers with their reverse, flap lever, speedbrake,
/// gear lever, parking brake, tiller, rudder pedals, the overhead's switches), the pilot whose hands
/// and feet follow them, the warning and gear lamps, the seatbelt signs, and the screens or steam
/// gauges drawn by an <see cref="InstrumentCanvas"/>. Everything is driven by the replicated
/// <see cref="AirlinerLook"/> and by the drawn aircraft's own motion (speed, height, attitude, heading,
/// vertical speed come from where its frame goes), so every peer shows the same.
///
/// <para>The screens render only near a camera (30 m) and only when what they show changed, at most
/// 20 times a second; nothing here allocates per frame (the readout is a value, the pilot's body a
/// cached mesh per quantised pose).</para>
/// </summary>
public partial class AircraftCockpit : Node3D
{
    private readonly CockpitLayout _k;
    private readonly AirlinerSpec _spec;
    private readonly Node3D _model;

    // ---- the moving parts ----------------------------------------------------------------------
    private Node3D? _stick, _columnL, _columnR, _wheelL, _wheelR, _flapLever, _speedbrake, _gearLever, _park, _tiller;
    private readonly Node3D?[] _thrust = new Node3D?[4];
    private readonly Node3D?[] _pedals = new Node3D?[4];
    private readonly Vector3[] _pedalRest = new Vector3[4];
    private readonly Node3D?[] _switches = new Node3D?[Switches];
    private readonly MeshInstance3D?[] _lamps = new MeshInstance3D?[LampCount];
    private readonly int[] _lampState = new int[LampCount];
    private readonly MeshInstance3D[] _screens;
    private readonly (int Cell, Vector3 At, Vector2 Size)[] _screenSpots;
    private MeshInstance3D? _fcu;
    private readonly System.Collections.Generic.List<MeshInstance3D> _signs = new();
    private int _signsOn = -1;

    /// <summary>The overhead's toggles: battery, APU, seatbelts, landing lights, then one master per engine.</summary>
    private const int SwBattery = 0, SwApu = 1, SwSeatbelt = 2, SwLanding = 3, SwEngine = 4, Switches = 8;
    /// <summary>Lamps: master warning and caution each side, three gear greens, the autopilot.</summary>
    private const int MasterWarnL = 0, MasterWarnR = 1, MasterCautL = 2, MasterCautR = 3, GearLamp = 4, ApLamp = 7, LampCount = 8;

    // ---- the pilot ---------------------------------------------------------------------------------
    /// <summary>Someone is at the controls: their figure is drawn in the captain's seat.</summary>
    public bool PilotShown;
    public CockpitView View = CockpitView.Outside;
    public HumanPalette Palette = HumanPalette.Default;
    /// <summary>What <see cref="Palette"/> was made from (the owner's outfit, rider): made again only when it changes.</summary>
    public (long Outfit, int Rider) PaletteKey = (long.MinValue, 0);
    private MeshInstance3D? _pilotBody, _pilotHead;
    private HumanPalette? _builtFor;
    private (int, int, int, bool) _pose = (int.MinValue, 0, 0, false);
    private readonly System.Collections.Generic.Dictionary<(int, int, int, bool), ArrayMesh> _bodies = new();

    // ---- the instruments ---------------------------------------------------------------------------
    private SubViewport? _viewport;
    private InstrumentCanvas? _canvas;
    private OmniLight3D? _flood;
    private bool _active;
    private float _scan, _sinceDraw, _clock;
    private Readout _shown;
    private bool _dirty = true;
    private Vector3 _lastPos, _vel;
    private bool _hasLast;
    private float _vs;

    /// <summary>The checks (#421): read the instruments even with no camera near, to compare peers.</summary>
    public static bool ReadAlways;

    /// <summary>What the screens show now (or would, with no camera near and <see cref="ReadAlways"/>).</summary>
    public Readout Shown => _shown;

    /// <summary>The thrust levers' and the gear lever's travel as drawn, radians authored (forward, up +): for the checks.</summary>
    public float ThrustDrawn => _thrust[0] is { } t ? -t.Basis.GetEuler().X : float.NaN;
    public float GearLeverDrawn => _gearLever is { } g ? -g.Basis.GetEuler().X : float.NaN;

    /// <summary>Screen <paramref name="i"/>'s frame in the world (the captain's PFD first, then the ND; it faces +Z): for close-up pictures.</summary>
    public Transform3D? ScreenFrame(int i) => i >= 0 && i < _screens.Length && _screens[i].IsInsideTree() ? _screens[i].GlobalTransform : null;

    /// <summary>The world's height over the sea at y = 0 (the floating origin's), set by the local player: altitudes read in feet AMSL.</summary>
    public static float WorldAltitude;

    private const float Deg = Mathf.Pi / 180f;

    public AircraftCockpit(Node3D model, CockpitLayout layout, AirlinerSpec spec)
    {
        Name = "Cockpit";
        _model = model;
        _k = layout;
        _spec = spec;
        _screenSpots = ScreenSpots(layout);
        _screens = new MeshInstance3D[_screenSpots.Length];
        Furnish();
    }

    // ---- building ------------------------------------------------------------------------------------

    private static readonly Color Dark = new(0.07f, 0.075f, 0.085f), Grip = new(0.13f, 0.13f, 0.14f), Metal = new(0.55f, 0.57f, 0.6f),
        LeverKnob = new(0.85f, 0.85f, 0.82f), Plate = new(0.2f, 0.21f, 0.24f), PedalGrey = new(0.3f, 0.31f, 0.33f);

    /// <summary>Where the screens or gauges go on the panel: their atlas cell, centre (authored, on the panel's face) and size.</summary>
    private static (int, Vector3, Vector2)[] ScreenSpots(CockpitLayout k)
    {
        float z = k.PanelZ - 0.012f, hw = k.PanelHalf;
        var l = new System.Collections.Generic.List<(int, Vector3, Vector2)>();
        if (k.Glass)
        {
            var du = new Vector2(0.22f, 0.22f);
            foreach (float s in new[] { 1f, -1f })
            {
                l.Add((InstrumentCanvas.Pfd, new Vector3(s * (hw - 0.14f), k.PanelY + 0.15f, z), du));
                l.Add((InstrumentCanvas.Nd, new Vector3(s * (hw - 0.38f), k.PanelY + 0.15f, z), du));
            }
            l.Add((InstrumentCanvas.Ewd, new Vector3(0f, k.PanelY + 0.15f, z), du));
            l.Add((InstrumentCanvas.Sd, new Vector3(0f, k.PanelY - 0.1f, z), du));
            l.Add((InstrumentCanvas.Isis, new Vector3(hw - 0.38f, k.PanelY - 0.06f, z), new Vector2(0.11f, 0.11f)));
        }
        else
        {
            var g = new Vector2(0.14f, 0.14f);
            foreach (float s in new[] { 1f, -1f })
            {
                float c = s * k.Hip.X;
                float top = k.PanelY + 0.17f, low = k.PanelY + 0.015f;
                l.Add((InstrumentCanvas.Asi, new Vector3(c + 0.15f, top, z), g));
                l.Add((InstrumentCanvas.Adi, new Vector3(c, top, z), g));
                l.Add((InstrumentCanvas.Alt, new Vector3(c - 0.15f, top, z), g));
                l.Add((InstrumentCanvas.Hsi, new Vector3(c, low, z), g));
                l.Add((InstrumentCanvas.Vsi, new Vector3(c - 0.15f, low, z), g));
            }
            l.Add((InstrumentCanvas.N1Cell, new Vector3(0f, k.PanelY + 0.13f, z), new Vector2(0.24f, 0.24f)));
            l.Add((InstrumentCanvas.Misc, new Vector3(0f, k.PanelY - 0.11f, z), new Vector2(0.18f, 0.18f)));
        }
        return l.ToArray();
    }

    /// <summary>The gear lever's pivot on the panel, right of the centre, and its three lamps above it.</summary>
    private Vector3 GearLeverAt => new(-0.27f, _k.PanelY - (_k.Glass ? 0.12f : 0.2f), _k.PanelZ - 0.01f);

    /// <summary>The thrust levers' pivot row and the pedestal's lever station.</summary>
    private float LeverZ => Mathf.Max(_k.PedestalZ0 + 0.05f, _k.Hip.Z + 0.04f);

    private Vector3 ThrustPivot(int i) => new(((_k.Engines - 1) * 0.5f - i) * 0.065f, _k.PedestalTop, LeverZ);

    private Vector3 StickPivot => new(_k.Hip.X + 0.4f, _k.ConsoleTop, _k.Hip.Z + 0.2f);
    private Vector3 ColumnPivot(float side) => new(side * _k.Hip.X, _k.Floor, _k.Hip.Z + 0.62f);
    private Vector3 ColumnTop(float side) => new(side * _k.Hip.X, _k.Hip.Y + 0.36f, _k.Hip.Z + 0.25f);
    /// <summary>The stem of a thrust lever, pivot to knob.</summary>
    private const float Stem = 0.24f;
    private Vector3 PedalAt(int i) => new((i < 2 ? 1f : -1f) * _k.Hip.X + (i % 2 == 0 ? 0.11f : -0.11f), _k.Floor + 0.12f, _k.Hip.Z + 0.86f);
    private Vector3 SwitchAt(int i) => new(0.3f - i * 0.085f, _k.OverheadY - 0.03f, _k.OverheadZ);

    private void Furnish()
    {
        var body = HumanMeshBuilder.FigureMaterial();
        var glass = CarRig.GlassMaterial();
        var fit = new MeshScratch();
        // bezels behind the screens and gauges
        foreach (var (_, at, size) in _screenSpots)
            fit.Box(at + new Vector3(0, 0, 0.008f), new Vector3(size.X + 0.025f, size.Y + 0.025f, 0.012f), Dark);
        // the FCU (glass) or the autopilot panel (steam) hung under the glareshield's edge
        fit.Box(new Vector3(0f, _k.GlareY - 0.07f, _k.PanelZ - 0.03f), new Vector3(0.82f, 0.075f, 0.06f), Plate);
        // the overhead panel and its switches' plate
        fit.Box(new Vector3(0f, _k.OverheadY, _k.OverheadZ), new Vector3(0.8f, 0.05f, 0.5f), Plate);
        // side consoles for the sidesticks
        if (_k.Sidestick)
            foreach (float s in new[] { 1f, -1f })
                fit.Box(new Vector3(s * (_k.Hip.X + 0.4f), _k.ConsoleTop - 0.01f, _k.Hip.Z + 0.2f), new Vector3(0.14f, 0.02f, 0.45f), Plate);
        // the pedestal's lever quadrant
        fit.Box(new Vector3(0f, _k.PedestalTop + 0.01f, LeverZ), new Vector3(_k.PedestalHalf * 2f - 0.02f, 0.02f, 0.3f), Plate);
        var fitNode = new MeshInstance3D { Name = "CockpitFit", Mesh = fit.Build() };
        MeshScratch.Paint(fitNode, body, glass);
        _model.AddChild(fitNode);

        Node3D Add(AircraftPart part, string name)
        {
            var n = part.ToNode(name, body, glass);
            _model.AddChild(n);
            return n;
        }

        // the captain's sidestick (the first officer's stays put: they are not coupled), or both yokes (they are)
        if (_k.Sidestick)
        {
            foreach (float s in new[] { 1f, -1f })
            {
                var piv = StickPivot with { X = s * (_k.Hip.X + 0.4f) };
                var p = new AircraftPart(piv, Basis.Identity);
                p.Box(piv + new Vector3(0, 0.015f, 0), new Vector3(0.06f, 0.03f, 0.06f), Dark);
                p.Tube(piv, piv + new Vector3(0, 0.1f, -0.01f), 0.014f, 0.012f, Dark, 6);
                p.Box(piv + new Vector3(0, 0.13f, -0.015f), new Vector3(0.035f, 0.07f, 0.045f), Grip);
                var n = Add(p, s > 0 ? "StickL" : "StickR");
                if (s > 0) _stick = n;
            }
        }
        else
        {
            foreach (float s in new[] { 1f, -1f })
            {
                var piv = ColumnPivot(s);
                var top = ColumnTop(s);
                var col = new AircraftPart(piv, Basis.Identity);
                col.Tube(piv, top, 0.035f, 0.03f, Dark, 6);
                var column = Add(col, s > 0 ? "ColumnL" : "ColumnR");
                var w = new AircraftPart(top, Basis.Identity);
                w.Box(top + new Vector3(0, 0.02f, -0.02f), new Vector3(0.3f, 0.045f, 0.04f), Grip);
                foreach (float e in new[] { -1f, 1f })
                    w.Box(top + new Vector3(e * 0.15f, 0.05f, -0.02f), new Vector3(0.035f, 0.11f, 0.04f), Grip);
                var wheel = w.ToNode(s > 0 ? "WheelL" : "WheelR", body, glass);
                wheel.Position -= column.Position;
                column.AddChild(wheel);
                if (s > 0) { _columnL = column; _wheelL = wheel; }
                else { _columnR = column; _wheelR = wheel; }
            }
        }

        // thrust levers: a stem and a crossbar knob each
        for (int i = 0; i < _k.Engines && i < _thrust.Length; i++)
        {
            var piv = ThrustPivot(i);
            var p = new AircraftPart(piv, Basis.Identity);
            p.Tube(piv, piv + new Vector3(0, Stem, 0), 0.012f, 0.01f, Metal, 5);
            p.Box(piv + new Vector3(0, Stem + 0.01f, 0), new Vector3(0.05f, 0.025f, 0.03f), Dark);
            _thrust[i] = Add(p, $"Thrust{i}");
        }
        {
            var piv = new Vector3(-_k.PedestalHalf + 0.03f, _k.PedestalTop, LeverZ + 0.12f);
            var p = new AircraftPart(piv, Basis.Identity);
            p.Tube(piv, piv + new Vector3(0, 0.13f, 0), 0.01f, 0.009f, Metal, 5);
            p.Box(piv + new Vector3(0, 0.14f, 0), new Vector3(0.035f, 0.03f, 0.05f), LeverKnob);
            _flapLever = Add(p, "FlapLever");
        }
        {
            var piv = new Vector3(_k.PedestalHalf - 0.03f, _k.PedestalTop, LeverZ + 0.12f);
            var p = new AircraftPart(piv, Basis.Identity);
            p.Tube(piv, piv + new Vector3(0, 0.12f, 0), 0.01f, 0.009f, Metal, 5);
            p.Box(piv + new Vector3(0, 0.13f, 0), new Vector3(0.03f, 0.025f, 0.04f), Dark);
            _speedbrake = Add(p, "Speedbrake");
        }
        {
            var piv = new Vector3(0f, _k.PedestalTop + 0.02f, _k.PedestalZ1 - 0.12f);
            var p = new AircraftPart(piv, Basis.Identity);
            p.Box(piv + new Vector3(0, 0.02f, 0), new Vector3(0.07f, 0.03f, 0.025f), new Color(0.8f, 0.1f, 0.08f));
            _park = Add(p, "ParkBrake");
        }
        {
            var piv = GearLeverAt;
            var p = new AircraftPart(piv, Basis.Identity);
            p.Tube(piv, piv + new Vector3(0, 0, -0.09f), 0.008f, 0.008f, Metal, 5);
            p.Box(piv + new Vector3(0, 0, -0.1f), new Vector3(0.035f, 0.035f, 0.025f), LeverKnob);
            _gearLever = Add(p, "GearLever");
        }
        {
            // the tiller: on the captain's console (A320), on the wall beside the captain (yokes)
            var piv = _k.Sidestick ? new Vector3(_k.Hip.X + 0.4f, _k.ConsoleTop + 0.01f, _k.Hip.Z - 0.02f)
                : new Vector3(_k.Hip.X + 0.4f, _k.Hip.Y + 0.12f, _k.Hip.Z + 0.25f);
            var p = new AircraftPart(piv, Basis.Identity);
            p.Box(piv + new Vector3(0, 0.01f, 0), new Vector3(0.09f, 0.02f, 0.09f), Dark);
            p.Tube(piv + new Vector3(0.03f, 0.02f, 0), piv + new Vector3(0.03f, 0.06f, 0), 0.008f, 0.008f, Grip, 4);
            _tiller = Add(p, "Tiller");
        }
        for (int i = 0; i < 4; i++)
        {
            var at = PedalAt(i);
            var p = new AircraftPart(at, Basis.Identity);
            p.Box(at, new Vector3(0.08f, 0.14f, 0.025f), PedalGrey);
            p.Tube(at + new Vector3(0, 0.07f, 0.01f), at + new Vector3(0, 0.2f, 0.12f), 0.01f, 0.01f, Dark, 4);
            _pedals[i] = Add(p, $"Pedal{i}");
            _pedalRest[i] = _pedals[i]!.Position;
        }
        for (int i = 0; i < Switches; i++)
        {
            if (i >= SwEngine + _k.Engines) break;
            var at = SwitchAt(i);
            var p = new AircraftPart(at, Basis.Identity);
            p.Tube(at, at + new Vector3(0, -0.045f, 0), 0.006f, 0.005f, Metal, 4);
            p.Box(at + new Vector3(0, -0.05f, 0), new Vector3(0.014f, 0.014f, 0.014f), LeverKnob);
            _switches[i] = Add(p, $"Switch{i}");
        }

        // lamps
        float gz = _k.PanelZ - 0.035f, gy = _k.GlareY - 0.07f;
        Lamp(MasterWarnL, new Vector3(_k.Hip.X - 0.05f, gy, gz), new Vector3(0.035f, 0.03f, 0.015f));
        Lamp(MasterCautL, new Vector3(_k.Hip.X - 0.1f, gy, gz), new Vector3(0.035f, 0.03f, 0.015f));
        Lamp(MasterWarnR, new Vector3(-_k.Hip.X + 0.05f, gy, gz), new Vector3(0.035f, 0.03f, 0.015f));
        Lamp(MasterCautR, new Vector3(-_k.Hip.X + 0.1f, gy, gz), new Vector3(0.035f, 0.03f, 0.015f));
        for (int i = 0; i < 3; i++)
            Lamp(GearLamp + i, GearLeverAt + new Vector3((i - 1) * 0.035f, 0.07f, -0.005f), new Vector3(0.025f, 0.025f, 0.01f));
        Lamp(ApLamp, new Vector3(0.18f, gy, gz), new Vector3(0.04f, 0.022f, 0.015f));
        foreach (var at in _k.Signs)
        {
            var sign = new MeshInstance3D { Name = "Seatbelt", Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.07f, 0.04f) }, Position = AircraftMeshBuilder.Flip(at), MaterialOverride = LampOff };
            _model.AddChild(sign);
            _signs.Add(sign);
        }

        // the screens: dark until the instruments come up near a camera
        for (int i = 0; i < _screenSpots.Length; i++)
        {
            var (_, at, size) = _screenSpots[i];
            _screens[i] = new MeshInstance3D { Name = $"Screen{i}", Mesh = new QuadMesh { Size = size }, Position = AircraftMeshBuilder.Flip(at), MaterialOverride = LampOff };
            _model.AddChild(_screens[i]);
        }
        if (_k.Glass)
        {
            _fcu = new MeshInstance3D
            {
                Name = "Fcu", Mesh = new QuadMesh { Size = new Vector2(0.72f, 0.036f) },
                Position = AircraftMeshBuilder.Flip(new Vector3(0f, _k.GlareY - 0.07f, _k.PanelZ - 0.061f)), MaterialOverride = LampOff,
            };
            _model.AddChild(_fcu);
        }
    }

    private void Lamp(int i, Vector3 at, Vector3 size)
    {
        var lamp = new MeshInstance3D { Name = $"Lamp{i}", Mesh = new BoxMesh { Size = size }, Position = AircraftMeshBuilder.Flip(at), MaterialOverride = LampOff };
        _model.AddChild(lamp);
        _lamps[i] = lamp;
    }

    private static StandardMaterial3D Lit(Color c) => new() { AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
    private static readonly StandardMaterial3D LampOff = new() { AlbedoColor = new Color(0.05f, 0.05f, 0.055f), Roughness = 0.4f };
    private static readonly StandardMaterial3D LampRed = Lit(new Color(1f, 0.12f, 0.08f)), LampAmber = Lit(new Color(1f, 0.6f, 0.05f)),
        LampGreen = Lit(new Color(0.15f, 1f, 0.3f)), LampSign = Lit(new Color(0.95f, 0.9f, 0.7f));

    private void SetLamp(int i, int state)
    {
        if (_lamps[i] is not { } lamp || _lampState[i] == state) return;
        _lampState[i] = state;
        lamp.MaterialOverride = state switch { 1 => LampRed, 2 => LampAmber, 3 => LampGreen, _ => LampOff };
    }

    // ---- every frame ---------------------------------------------------------------------------------

    /// <summary>Author-space turn about X then Z (a lever pitched, then rolled), and the same turn in node space (flipped about Y).</summary>
    private static Basis AuthorTurn(float ax, float az) => new Basis(Vector3.Back, az) * new Basis(Vector3.Right, ax);
    private static Basis NodeTurn(float ax, float az) => new Basis(Vector3.Back, -az) * new Basis(Vector3.Right, -ax);

    /// <summary>
    /// Moves the controls and lamps to <paramref name="look"/> and the rig's eased parts (flaps in settings,
    /// gear 0..1, the stick), and updates the screens from the aircraft's motion.
    /// </summary>
    public void Show(in AirlinerLook look, float flaps, float gear, Vector2 stick, float dt)
    {
        _clock += dt;
        // the stick or yokes: pulled back pitches the top aft, a right roll tilts it right / turns the wheel clockwise
        float ax = -stick.Y * (_k.Sidestick ? 0.3f : 0.12f), az = stick.X * (_k.Sidestick ? 0.3f : 1.1f);
        if (_stick != null) _stick.Basis = NodeTurn(ax, az);
        if (_columnL != null) _columnL.Basis = NodeTurn(ax, 0f);
        if (_columnR != null) _columnR.Basis = NodeTurn(ax, 0f);
        if (_wheelL != null) _wheelL.Basis = NodeTurn(0f, az);
        if (_wheelR != null) _wheelR.Basis = NodeTurn(0f, az);

        float lever = CockpitInstruments.LeverAngle(look.Lever, look.Reverse);
        for (int i = 0; i < _thrust.Length; i++)
            if (_thrust[i] is { } t) t.Basis = NodeTurn(lever, 0f);
        int last = Mathf.Max(1, _spec.FlapSettings - 1);
        if (_flapLever != null) _flapLever.Basis = NodeTurn(Mathf.Lerp(0.35f, -0.45f, (float)look.FlapLever / last), 0f);
        if (_speedbrake != null) _speedbrake.Basis = NodeTurn(-0.25f * look.SpeedbrakeLever, 0f);
        if (_gearLever != null) _gearLever.Basis = NodeTurn(look.GearLever ? -0.5f : 0.5f, 0f);
        if (_park != null) _park.Basis = new Basis(Vector3.Up, look.ParkingBrake ? Mathf.Pi * 0.5f : 0f);
        if (_tiller != null) _tiller.Basis = new Basis(Vector3.Up, look.Airborne ? 0f : -stick.X * 1.2f);
        // the rudder pedals with the turn: the right one forward for a right turn
        for (int i = 0; i < 4; i++)
            if (_pedals[i] is { } pedal)
            {
                float side = i % 2 == 0 ? 1f : -1f;
                pedal.Position = _pedalRest[i] + new Vector3(0, 0, side * stick.X * 0.05f);
            }
        for (int i = 0; i < Switches; i++)
        {
            if (_switches[i] is not { } sw) continue;
            bool on = i switch
            {
                SwBattery => look.Power,
                SwApu => look.Power && look.Lit < _k.Engines,
                SwSeatbelt => look.Power,
                SwLanding => (look.Lights & AirlinerLights.Landing) != 0,
                _ => i - SwEngine < look.Lit,
            };
            sw.Basis = NodeTurn(on ? -0.5f : 0.5f, 0f);
        }

        // lamps
        bool flash = Mathf.PosMod(_clock, 0.5f) < 0.3f;
        var warn = CockpitWarnings(look, gear);
        bool red = look.Power && CockpitInstruments.MasterWarning(warn), amber = look.Power && CockpitInstruments.MasterCaution(warn);
        SetLamp(MasterWarnL, red && flash ? 1 : 0);
        SetLamp(MasterWarnR, red && flash ? 1 : 0);
        SetLamp(MasterCautL, amber ? 2 : 0);
        SetLamp(MasterCautR, amber ? 2 : 0);
        for (int i = 0; i < 3; i++)
            SetLamp(GearLamp + i, !look.Power ? 0 : look.GearBroken ? 1 : gear >= 0.99f ? 3 : gear > 0.01f ? 1 : 0);
        SetLamp(ApLamp, look.Power && look.Autopilot ? 3 : 0);
        int signs = look.Power ? 1 : 0;
        if (signs != _signsOn)
        {
            _signsOn = signs;
            foreach (var s in _signs) s.MaterialOverride = signs == 1 ? LampSign : LampOff;
        }

        UpdatePilot(look, stick);
        UpdateScreens(look, flaps, gear, warn, flash, dt);
    }

    /// <summary>The warnings from the replicated look: the red one the owner's model raised, and the cautions any peer can tell.</summary>
    private CockpitWarning CockpitWarnings(in AirlinerLook look, float gear)
    {
        var w = look.Warning switch { 1 => CockpitWarning.Stall, 2 => CockpitWarning.Overspeed, 3 => CockpitWarning.Gear, _ => CockpitWarning.None };
        return w | CockpitInstruments.Warnings(false, false, look.Airborne, 999f, 0f, 0f, gear, look.Fuel, look.ParkingBrake, look.Lever,
            look.Power, look.Lit < _k.Engines ? 0f : 1f);
    }

    // ---- the pilot -------------------------------------------------------------------------------------

    private DriverSeat Seat => new(_k.Hip, AirlinerRig.PilotRecline, _k.Hip + new Vector3(0, 0.3f, 0.5f), new Vector3(0, 0.94f, -0.34f).Normalized(), 0.15f,
        PedalAt(0), PedalAt(0), PedalAt(1));

    /// <summary>Where the pilot's hands and feet go for a stick, lever and rudder (authored): the outer hand on the stick or the yoke's horn, the inner on the levers.</summary>
    public (Vector3 Outer, Vector3 Inner, Vector3 FootOut, Vector3 FootIn) Holds(Vector2 stick, float lever, bool reverse)
    {
        float ax = -stick.Y * (_k.Sidestick ? 0.3f : 0.12f), az = stick.X * (_k.Sidestick ? 0.3f : 1.1f);
        Vector3 outer;
        if (_k.Sidestick) outer = StickPivot + AuthorTurn(ax, az) * new Vector3(0.0f, 0.12f, -0.015f);
        else
        {
            var piv = ColumnPivot(1f);
            outer = piv + AuthorTurn(ax, 0f) * (ColumnTop(1f) - piv + AuthorTurn(0f, az) * new Vector3(0.15f, 0.06f, -0.02f));
        }
        float la = CockpitInstruments.LeverAngle(lever, reverse);
        // the inner hand over the levers, on the pilot's side of them
        var inner = ThrustPivot(0) with { X = Mathf.Max(ThrustPivot(0).X, 0.05f) } + AuthorTurn(la, 0f) * new Vector3(0, Stem + 0.02f, -0.02f);
        float r = stick.X * 0.05f;
        // a foot's ball on its pad (the pad is pushed forward, +Z authored, by the turn on its side)
        var footOut = PedalAt(0) + new Vector3(0, 0.03f, -0.03f - r);
        var footIn = PedalAt(1) + new Vector3(0, 0.03f, -0.03f + r);
        return (outer, inner, footOut, footIn);
    }

    /// <summary>How far the pilot's hands and feet fall short over the controls' travel, m (<c>--cockpitcheck</c>).</summary>
    public (float Stick, float Levers, float Feet) PilotReach()
    {
        float stickShort = 0f, leverShort = 0f, feetShort = 0f;
        foreach (var st in new[] { Vector2.Zero, new Vector2(1, 1), new Vector2(-1, -1), new Vector2(1, -1), new Vector2(-1, 1) })
            foreach (float lever in new[] { 0f, 1f })
            {
                var (o, i, fo, fi) = Holds(st, lever, false);
                var (a, b, c, d) = HumanMeshBuilder.PilotReach(Seat, o, i, fo, fi);
                stickShort = Mathf.Max(stickShort, a);
                leverShort = Mathf.Max(leverShort, b);
                feetShort = Mathf.Max(feetShort, Mathf.Max(c, d));
            }
        return (stickShort, leverShort, feetShort);
    }

    private void UpdatePilot(in AirlinerLook look, Vector2 stick)
    {
        bool body = PilotShown && View != CockpitView.Bare, head = PilotShown && View == CockpitView.Outside;
        if (_pilotHead == null && !PilotShown) return;
        if (!Equals(_builtFor, Palette))
        {
            _builtFor = Palette;
            _bodies.Clear();
            _pose = (int.MinValue, 0, 0, false);
            _pilotHead?.QueueFree();
            _pilotHead = null;
        }
        if (_pilotHead == null)
        {
            var hs = new MeshScratch();
            var (o, i, fo, fi) = Holds(Vector2.Zero, 0f, false);
            HumanMeshBuilder.AppendPilot(hs, Palette, Seat, o, i, fo, fi, body: false, head: true);
            _pilotHead = new MeshInstance3D { Name = "PilotHead", Mesh = hs.Build(), MaterialOverride = HumanMeshBuilder.FigureMaterial() };
            _pilotBody ??= new MeshInstance3D { Name = "Pilot", MaterialOverride = HumanMeshBuilder.FigureMaterial() };
            AddChild(_pilotHead);
            if (_pilotBody.GetParent() == null) AddChild(_pilotBody);
        }
        if (_pilotHead.Visible != head) _pilotHead.Visible = head;
        if (_pilotBody!.Visible != body) _pilotBody.Visible = body;
        if (!body) return;
        // the pose quantised to what shows (a tenth of the stick, an eighth of the levers)
        var key = (Mathf.RoundToInt(stick.X * 10f), Mathf.RoundToInt(stick.Y * 10f), Mathf.RoundToInt(look.Lever * 8f), look.Reverse);
        if (key == _pose) return;
        _pose = key;
        if (!_bodies.TryGetValue(key, out var mesh))
        {
            if (_bodies.Count >= 256) _bodies.Clear();
            var s = new MeshScratch();
            var (o, i, fo, fi) = Holds(new Vector2(key.Item1 / 10f, key.Item2 / 10f), key.Item3 / 8f, key.Item4);
            HumanMeshBuilder.AppendPilot(s, Palette, Seat, o, i, fo, fi, body: true, head: false);
            mesh = _bodies[key] = s.Build();
        }
        _pilotBody.Mesh = mesh;
    }

    // ---- the screens -----------------------------------------------------------------------------------

    private void UpdateScreens(in AirlinerLook look, float flaps, float gear, CockpitWarning warn, bool flash, float dt)
    {
        if (!IsInsideTree()) return;
        var pos = GlobalPosition;
        if (_hasLast && dt > 0f)
        {
            var v = (pos - _lastPos) / dt;
            // a jump (the floating origin moving, a teleport) is not a speed
            if (v.LengthSquared() < 400f * 400f)
            {
                _vel = _vel.Lerp(v, MathX.Damp(5f, dt));
                _vs = Mathf.Lerp(_vs, v.Y, MathX.Damp(2f, dt));
            }
        }
        _lastPos = pos;
        _hasLast = true;

        // near a camera only: 4 times a second, look for one
        _scan -= dt;
        if (_scan <= 0f)
        {
            _scan = 0.25f;
            var cam = GetViewport()?.GetCamera3D();
            bool near = cam != null && cam.GlobalPosition.DistanceSquaredTo(GlobalTransform * AircraftMeshBuilder.Flip(_k.Eye)) < 30f * 30f;
            if (near != _active) Activate(near);
        }
        if (!_active && !ReadAlways) return;

        var g = GlobalTransform.Basis.Orthonormalized();
        var fwd = -g.Z;
        float hdg = Mathf.RadToDeg(Mathf.Atan2(fwd.X, -fwd.Z));
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(fwd.Y, -1f, 1f)));
        float roll = Mathf.RadToDeg(Mathf.Atan2(-g.X.Y, g.Y.Y));
        var flat = _vel with { Y = 0 };
        float altitude = pos.Y + WorldAltitude;
        float kt = CockpitInstruments.Ias(_vel.Length(), altitude) / CockpitInstruments.Knot;
        float n1 = look.Spool * 100f;
        float ffToga = _spec.StaticThrust * _spec.FuelPerNewton * 3600f;
        var r = new Readout(
            Power: look.Power,
            Ias: Mathf.RoundToInt(kt < 30f ? 0f : kt),
            Alt: Mathf.RoundToInt(altitude / CockpitInstruments.Foot / 10f) * 10,
            Vs: Mathf.RoundToInt(_vs / CockpitInstruments.Foot * 60f / 50f) * 50,
            Hdg: CockpitInstruments.Wrap360(hdg),
            Track: flat.Length() > 3f ? CockpitInstruments.Wrap360(Mathf.RadToDeg(Mathf.Atan2(flat.X, -flat.Z))) : -1,
            Pitch: Mathf.RoundToInt(pitch * 2f),
            Roll: Mathf.RoundToInt(roll),
            Gs: Mathf.RoundToInt(flat.Length() / CockpitInstruments.Knot),
            N1a: Engine(0, look, n1), N1b: Engine(1, look, n1), N1c: Engine(2, look, n1), N1d: Engine(3, look, n1),
            Egt: Mathf.RoundToInt(CockpitInstruments.Egt(look.Spool) / 5f) * 5,
            Ff: Mathf.RoundToInt(CockpitInstruments.FuelFlow(look.Spool, _spec.IdleSpool, ffToga) / 20f) * 20,
            Fuel: Mathf.RoundToInt(look.Fuel * _spec.FuelCapacity / 100f) * 100,
            FlapLever: look.FlapLever,
            Flaps: Mathf.RoundToInt(flaps * 10f),
            Gear: look.GearBroken ? 3 : gear >= 0.99f ? 2 : gear > 0.01f ? 1 : 0,
            Speedbrake: look.SpeedbrakeLever,
            Park: look.ParkingBrake,
            Autopilot: look.Autopilot,
            Warn: warn,
            Flash: flash && CockpitInstruments.MasterWarning(warn));
        if (!_active || _canvas == null || _viewport == null)
        {
            _shown = r;
            return;
        }
        string mode = !look.Power ? "" : look.Autopilot ? "SPEED  HDG  ALT" : CockpitInstruments.LeverDetent(look.Lever, look.Reverse);
        int posE = Mathf.RoundToInt(pos.X / 100f), posN = Mathf.RoundToInt(-pos.Z / 100f);
        if (!r.Equals(_shown) || !ReferenceEquals(mode, _canvas.Mode) || posE != _canvas.PosE || posN != _canvas.PosN)
        {
            _shown = r;
            _canvas.Mode = mode;
            _canvas.PosE = posE;
            _canvas.PosN = posN;
            _canvas.FuelPct = look.Fuel * 100f;
            _dirty = true;
        }
        _sinceDraw += dt;
        if (_dirty && _sinceDraw >= 0.05f)
        {
            _dirty = false;
            _sinceDraw = 0f;
            _canvas.R = _shown;
            _canvas.QueueRedraw();
            _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            if (_flood != null && _flood.Visible != _shown.Power) _flood.Visible = _shown.Power;
        }
    }

    /// <summary>Engine <paramref name="i"/>'s N1: the spool for those running, nothing for the rest.</summary>
    private int Engine(int i, in AirlinerLook look, float n1) => i < _k.Engines && i < look.Lit ? Mathf.RoundToInt(n1) : 0;

    /// <summary>Brings the instruments up (a camera came near) or lets them sleep.</summary>
    private void Activate(bool on)
    {
        _active = on;
        if (!on)
        {
            if (_viewport != null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            if (_flood != null) _flood.Visible = false;
            return;
        }
        if (_viewport == null)
        {
            var size = InstrumentCanvas.AtlasSize(_k.Glass);
            _viewport = new SubViewport
            {
                Name = "Instruments", Size = size, Disable3D = true, TransparentBg = false,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once, CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            };
            _canvas = new InstrumentCanvas
            {
                Glass = _k.Glass, Engines = _k.Engines, Vmo = Mathf.RoundToInt(_spec.Vmo / CockpitInstruments.Knot), FlapNames = _spec.FlapNames,
                Size = size,
            };
            _viewport.AddChild(_canvas);
            AddChild(_viewport);
            var tex = _viewport.GetTexture();
            for (int i = 0; i < _screens.Length; i++)
            {
                var (cell, _, _) = _screenSpots[i];
                _screens[i].MaterialOverride = ScreenMaterial(tex, size, new Rect2(cell * InstrumentCanvas.Cell, 0, InstrumentCanvas.Cell, InstrumentCanvas.Cell));
            }
            if (_fcu != null)
                _fcu.MaterialOverride = ScreenMaterial(tex, size, new Rect2(0, InstrumentCanvas.Cell, InstrumentCanvas.Cell * InstrumentCanvas.GlassCells, InstrumentCanvas.Strip));
            // a dim flood over the panel, warm, for the night
            _flood = new OmniLight3D
            {
                Name = "Flood", LightColor = new Color(1f, 0.85f, 0.65f), LightEnergy = 0.5f, OmniRange = 1.6f, ShadowEnabled = false,
                Position = AircraftMeshBuilder.Flip(new Vector3(0f, _k.GlareY + 0.35f, _k.PanelZ - 0.6f)),
            };
            _model.AddChild(_flood);
        }
        _dirty = true;
        _sinceDraw = 1f;
    }

    private static StandardMaterial3D ScreenMaterial(Texture2D tex, Vector2I atlas, Rect2 cell) => new()
    {
        AlbedoTexture = tex,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        Uv1Scale = new Vector3(cell.Size.X / atlas.X, cell.Size.Y / atlas.Y, 1f),
        Uv1Offset = new Vector3(cell.Position.X / atlas.X, cell.Position.Y / atlas.Y, 0f),
    };
}
