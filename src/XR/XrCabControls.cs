using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// The cab's and the cockpit's controls by hand (#438): knobs placed round the driver's eye that a
/// VR hand grips, pushes or pokes, each pressing the action its key does, so the game learns no
/// second way in.
///
/// <para>
/// Kinds: a <b>spring</b> lever taps an action a notch at a time as it is pushed or pulled along
/// its axis and springs back when let go (flaps, speedbrake, retarder, a sequential gear lever,
/// the landing gear); a <b>hold</b> lever holds its action while pushed (trim, the whistle cord); a
/// <b>gate</b> is an H-pattern gear lever, the gate it sits in being the gear; a <b>poke</b> is a
/// button the controller's tip presses (bus kneel and sign, autopilot, parking brake, radio).
/// </para>
///
/// <para>
/// Places are in the driver's eye frame (x right, y up, −z ahead), the frame the rig puts the
/// calibrated head at, so they sit at hand whatever the cab: an approximation of each dash, not
/// its drawn switches. The knobs are drawn in the headset only.
/// </para>
/// </summary>
internal sealed partial class XrCabControls : Node3D
{
    private enum Kind { Spring, Hold, Gate, Poke }

    private sealed class Control
    {
        public required Kind Kind;
        public required Vector3 At;
        public Vector3 Axis = Vector3.Back;
        /// <summary>Spring / hold: pushed along +Axis. Poke: the press. Gate: unused.</summary>
        public string? Plus;
        /// <summary>Spring / hold: pulled along −Axis.</summary>
        public string? Minus;
        /// <summary>Spring: one notch, m.</summary>
        public float Step = 0.05f;
        public MeshInstance3D Knob = null!;
        public bool Armed = true;
    }

    private sealed class Grab
    {
        public Control? Held;
        public Vector3 Start, Now;
        public int Notch;
        public string? Holding;
        public int Gate = -2;
        public bool Closed;
    }

    private const float GripReach = 0.09f, PokeReach = 0.035f, PokeRearm = 0.07f;
    /// <summary>The gate pitch across an H-pattern, and how far forward or back is in a gear, m.</summary>
    private const float GateAcross = 0.06f, GateThrow = 0.05f;

    private readonly XRController3D _left, _right;
    private readonly Grab _l = new(), _r = new();
    private readonly List<Control> _controls = new();
    private string _context = "";
    private StandardMaterial3D? _knobMat, _pokeMat;

    public XrCabControls(XRController3D left, XRController3D right)
    {
        _left = left;
        _right = right;
        Name = "CabControls";
        TopLevel = true;
    }

    /// <summary>A grip that holds a cab control: not also a shoulder, nor a hand grab.</summary>
    public bool LeftHeld => _l.Held != null;
    public bool RightHeld => _r.Held != null;

    /// <param name="eye">The driver's eye in the world, the vehicle's frame (<see cref="XrRig"/>'s anchor before the head is written back).</param>
    public void Update(FootPlayer? player, Transform3D eye)
    {
        // --xrcab truck-h-bus|truck-seq|car|airliner|steamer: that cab's controls round any view, for checks
        string context = Forced ?? ContextOf(player);
        if (context != _context) Build(context, player);
        if (_controls.Count == 0) return;

        Hand(_left, _l, eye);
        Hand(_right, _r, eye);
        foreach (var c in _controls)
        {
            // a held lever shows where the hand has it; anything else sits at rest
            var at = _l.Held == c ? Moved(c, _l) : _r.Held == c ? Moved(c, _r) : c.At;
            c.Knob.GlobalTransform = new Transform3D(eye.Basis, eye * at);
        }
    }

    // ---- which controls ----------------------------------------------------------------------

    private static readonly string? Forced = CmdArgs.Value("--xrcab", notFlag: true);

    private static string ContextOf(FootPlayer? p)
    {
        if (p == null || p.RidingWith != 0 || !XrSession.Active) return "";
        return p.Vehicle switch
        {
            Truck t when p.InCockpit => (t.EffectiveMode is HeavyShift.HPattern or HeavyShift.HPatternSplitter ? "truck-h" : "truck-seq")
                                        // a farm machine (#494) has the bus's two pokes: kneel lowers its implement, destination its auger or delivery
                                        + (t.IsBus || t.Spec.Farm ? "-bus" : ""),
            Car when p.InCockpit => "car",
            Airliner => "airliner",
            _ when p.Ride == RideKind.Steamer => "steamer",
            _ => "",
        };
    }

    private void Build(string context, FootPlayer? player)
    {
        foreach (var g in new[] { _l, _r }) Release(g);
        foreach (var c in _controls) c.Knob.QueueFree();
        _controls.Clear();
        _context = context;
        if (context.StartsWith("truck"))
        {
            if (context.StartsWith("truck-h")) Add(Kind.Gate, new(0.32f, -0.5f, -0.25f));
            // a sequential lever: pull back up a gear, push forward down one
            else Add(Kind.Spring, new(0.32f, -0.5f, -0.25f), Vector3.Back, PlayerInput.ShiftUp, PlayerInput.ShiftDown, 0.06f);
            // the retarder stalk right of the wheel: down for more, up for less
            Add(Kind.Spring, new(0.3f, -0.2f, -0.45f), Vector3.Down, PlayerInput.RetarderUp, PlayerInput.RetarderDown, 0.04f);
            if (context.EndsWith("-bus"))
            {
                Add(Kind.Poke, new(-0.28f, -0.32f, -0.5f), plus: PlayerInput.Kneel);
                Add(Kind.Poke, new(-0.18f, -0.32f, -0.5f), plus: PlayerInput.Destination);
            }
        }
        else if (context == "car")
        {
            Add(Kind.Poke, new(0.17f, -0.33f, -0.5f), plus: PlayerInput.RadioPrev);
            Add(Kind.Poke, new(0.27f, -0.33f, -0.5f), plus: PlayerInput.RadioNext);
            Add(Kind.Poke, new(0.22f, -0.27f, -0.5f), plus: PlayerInput.RadioPanel);
        }
        else if (context == "airliner")
        {
            // the flap lever on the pedestal: back a notch for more flap, forward for less
            Add(Kind.Spring, new(0.26f, -0.42f, -0.3f), Vector3.Back, PlayerInput.FlapsDown, PlayerInput.FlapsUp, 0.05f);
            // the speedbrake lever left of it: each pull back a step (retracted, half, full)
            Add(Kind.Spring, new(-0.1f, -0.42f, -0.3f), Vector3.Back, PlayerInput.Speedbrake, null, 0.06f);
            // the gear lever on the panel: up or down, either way works it (the game knows which)
            Add(Kind.Spring, new(0.05f, -0.14f, -0.6f), Vector3.Up, PlayerInput.CarDoor, PlayerInput.CarDoor, 0.06f);
            Add(Kind.Poke, new(0.14f, -0.4f, -0.22f), plus: PlayerInput.ParkingBrake);
            Add(Kind.Poke, new(0f, -0.03f, -0.6f), plus: PlayerInput.Autopilot);
            // the trim wheel beside the seat: rolled forward, nose down; back, nose up
            Add(Kind.Hold, new(0.22f, -0.52f, -0.12f), Vector3.Forward, PlayerInput.TrimNoseDown, PlayerInput.TrimNoseUp);
        }
        else if (context == "steamer")
            // the whistle cord overhead: pulled down, it blows
            Add(Kind.Hold, new(0.15f, 0.3f, -0.25f), Vector3.Down, PlayerInput.Horn, null);
        if (context != "") GD.Print($"[xr] cab controls: {context}, {_controls.Count}");
    }

    private void Add(Kind kind, Vector3 at, Vector3? axis = null, string? plus = null, string? minus = null, float step = 0.05f)
    {
        _knobMat ??= new StandardMaterial3D { AlbedoColor = new Color(1f, 0.72f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _pokeMat ??= new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.85f, 1f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var c = new Control { Kind = kind, At = at, Axis = axis ?? Vector3.Back, Plus = plus, Minus = minus, Step = step };
        c.Knob = new MeshInstance3D
        {
            Mesh = kind == Kind.Poke ? new BoxMesh { Size = new Vector3(0.035f, 0.035f, 0.015f) } : new SphereMesh { Radius = 0.022f, Height = 0.044f },
            MaterialOverride = kind == Kind.Poke ? _pokeMat : _knobMat,
            Layers = XrSession.HeadsetOnlyLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(c.Knob);
        _controls.Add(c);
    }

    // ---- one hand ------------------------------------------------------------------------------

    private void Hand(XRController3D ctl, Grab g, Transform3D eye)
    {
        if (!ctl.GetHasTrackingData())
        {
            Release(g);
            return;
        }
        var local = eye.AffineInverse() * ctl.GlobalPosition;
        g.Now = local;

        // pokes: the tip in, then out again before the next
        foreach (var c in _controls)
        {
            if (c.Kind != Kind.Poke) continue;
            float d = local.DistanceTo(c.At);
            if (c.Armed && d < PokeReach && c.Plus != null)
            {
                c.Armed = false;
                XrPad.Tap(c.Plus);
                ctl.TriggerHapticPulse("haptic", 0.0, 0.35, 0.03, 0.0);
            }
            else if (d > PokeRearm) c.Armed = true;
        }

        float grip = ctl.GetFloat("grip");
        if (g.Closed && grip < 0.35f)
        {
            g.Closed = false;
            Release(g);
        }
        else if (!g.Closed && grip > 0.7f)
        {
            g.Closed = true;
            foreach (var c in _controls)
                if (c.Kind != Kind.Poke && local.DistanceTo(c.At) < GripReach && _l.Held != c && _r.Held != c)
                {
                    g.Held = c;
                    g.Start = local;
                    g.Notch = 0;
                    g.Gate = -2;
                    ctl.TriggerHapticPulse("haptic", 0.0, 0.4, 0.04, 0.0);
                    break;
                }
        }
        if (g.Held is not { } held) return;

        var delta = local - g.Start;
        switch (held.Kind)
        {
            case Kind.Spring:
            {
                int notch = (int)Mathf.Round(delta.Dot(held.Axis) / held.Step);
                while (notch != g.Notch)
                {
                    int dir = Math.Sign(notch - g.Notch);
                    g.Notch += dir;
                    if ((dir > 0 ? held.Plus : held.Minus) is { } action) XrPad.Tap(action);
                    ctl.TriggerHapticPulse("haptic", 0.0, 0.25, 0.02, 0.0);
                }
                break;
            }
            case Kind.Hold:
            {
                float along = delta.Dot(held.Axis);
                string? want = along > 0.03f ? held.Plus : along < -0.03f ? held.Minus : null;
                if (want != g.Holding)
                {
                    if (g.Holding != null) Press(g.Holding, false);
                    if (want != null) Press(want, true);
                    g.Holding = want;
                }
                break;
            }
            case Kind.Gate:
            {
                int gate = XrControlNames.GateOf(delta.X, -delta.Z, GateAcross, GateThrow);
                if (gate != g.Gate)
                {
                    g.Gate = gate;
                    XrPad.Tap(gate switch
                    {
                        0 => PlayerInput.GearNeutral,
                        -1 => PlayerInput.GearReverse,
                        _ => PlayerInput.Gates[gate - 1],
                    });
                    ctl.TriggerHapticPulse("haptic", 0.0, 0.4, 0.03, 0.0);
                }
                break;
            }
        }
    }


    /// <summary>Where a held knob is drawn: its rest plus the hand's move, kept to its track.</summary>
    private static Vector3 Moved(Control c, Grab g)
    {
        var delta = g.Now - g.Start;
        return c.Kind switch
        {
            Kind.Gate => c.At + new Vector3(Mathf.Clamp(delta.X, -2.4f * GateAcross, 1.4f * GateAcross), 0f, Mathf.Clamp(delta.Z, -1.6f * GateThrow, 1.6f * GateThrow)),
            _ => c.At + c.Axis * Mathf.Clamp(delta.Dot(c.Axis), -0.12f, 0.12f),
        };
    }

    private void Release(Grab g)
    {
        if (g.Holding != null) Press(g.Holding, false);
        g.Holding = null;
        g.Held = null;
    }

    private static void Press(string action, bool on) =>
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = on, Strength = on ? 1f : 0f });
}
