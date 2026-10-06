using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.XR;

/// <summary>
/// A real hand in the headset (#648), in place of the controller marker: the player's own skin, gloves,
/// sleeve and hand size (<see cref="HumanMeshBuilder.HandsOf"/>), a palm and fifteen finger bones turned
/// at their joints, never rebuilt while the look holds.
///
/// <para>
/// It sits on the controller's grip pose, whose origin is the middle of the handle in the closed hand:
/// fingers along −Y (open), the wrist at +Y, the thumb's side −Z, the right palm facing −X (the left +X).
/// </para>
/// <para>
/// The fingers follow the real ones (<see cref="Drive"/>): every joint's bend from OpenXR hand
/// tracking when the runtime gives it (bare hands, or Meta's fingers inferred from the controllers);
/// otherwise from the controller's sensors as Meta's own hands do: the grip closes the last three
/// fingers, the trigger the index (half closed while touched, pointing when not), a thumb on a button,
/// the stick or the rest lies down, else it stands up.
/// </para>
/// </summary>
internal sealed partial class XrHand : Node3D
{
    /// <summary>Index, middle, ring, pinky, thumb, each three joints from the palm out.</summary>
    public const int Bones = 15;
    private const int Thumb = 4;

    // proximal, middle, distal lengths (m, before the build's hand scale); the thumb's are its metacarpal, proximal, distal
    private static readonly float[] Lengths =
    [
        0.040f, 0.024f, 0.021f,
        0.045f, 0.028f, 0.022f,
        0.042f, 0.027f, 0.021f,
        0.033f, 0.019f, 0.018f,
        0.034f, 0.031f, 0.026f,
    ];
    private static readonly float[] Radii = [0.0092f, 0.0098f, 0.0092f, 0.0080f, 0.0115f];

    // controller fallback poses: per joint bend, radians
    private static readonly float[] OpenBend = [0.06f, 0.08f, 0.05f, 0.16f, 0.2f, 0.1f, 0.2f, 0.24f, 0.12f, 0.24f, 0.26f, 0.14f, 0f, 0.1f, 0.12f];
    private static readonly float[] FistBend = [1.2f, 1.35f, 0.8f, 1.45f, 1.6f, 0.9f, 1.5f, 1.6f, 0.9f, 1.55f, 1.5f, 0.85f, 0f, 0.45f, 0.5f];
    private static readonly float[] TouchBend = [0.45f, 0.5f, 0.3f];

    private static readonly StringName GripAction = "grip", TriggerAction = "trigger", TriggerTouch = "trigger_touch",
        AxTouch = "ax_touch", ByTouch = "by_touch", StickTouch = "primary_touch", RestTouch = "thumbrest_touch";
    private static readonly StringName LeftTracker = "/user/hand_tracker/left", RightTracker = "/user/hand_tracker/right";

    private readonly bool _right;
    private readonly float _m;
    private readonly Node3D[] _joints = new Node3D[Bones];
    private readonly MeshInstance3D[] _bones = new MeshInstance3D[Bones];
    private readonly MeshInstance3D _palm, _cuff;
    private readonly Node3D _thumbBase;
    private Basis _thumbUp, _thumbDown;
    private HumanMeshBuilder.HandLook? _look;

    /// <summary>Each joint's bend toward the palm, radians (the order of <see cref="Bones"/>).</summary>
    public readonly float[] Bend = new float[Bones];
    /// <summary>0: the thumb stands up off the controller; 1: it lies on its buttons.</summary>
    public float ThumbDown;
    /// <summary>Whether the last <see cref="Drive"/> read the fingers from hand tracking.</summary>
    public bool Tracked { get; private set; }

    private readonly float[] _target = new float[Bones];

    public XrHand(bool right)
    {
        _right = right;
        _m = right ? 1f : -1f;
        Name = right ? "RightHand" : "LeftHand";
        _palm = Part(this);
        _cuff = Part(this);
        _thumbBase = new Node3D { Name = "Thumb" };
        AddChild(_thumbBase);
        for (int f = 0; f < 5; f++)
        {
            Node3D parent = f == Thumb ? _thumbBase : this;
            for (int j = 0; j < 3; j++)
            {
                int b = f * 3 + j;
                _joints[b] = new Node3D();
                parent.AddChild(_joints[b]);
                _bones[b] = Part(_joints[b]);
                parent = _joints[b];
            }
        }
        Array.Copy(OpenBend, Bend, Bones);
        SetLook(HumanMeshBuilder.HandsOf(HumanPalette.Default));
    }

    private static MeshInstance3D Part(Node3D parent)
    {
        var part = new MeshInstance3D
        {
            MaterialOverride = HumanMeshBuilder.FigureMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the figure has its own hands in the mirrors and the monitor's view: these are the eyes' (#648)
            Layers = XrSession.HeadsetOnlyLayer,
        };
        parent.AddChild(part);
        return part;
    }

    /// <summary>Rebuilds the hand for a new look (skin, gloves, sleeve, build); nothing when it is the same.</summary>
    public void SetLook(HumanMeshBuilder.HandLook look)
    {
        if (_look == look) return;
        _look = look;
        float k = look.Scale;
        var s = new MeshScratch();

        // the palm, between the knuckles and the wrist, on the far side of the handle from the fingers' wrap
        var palmCentre = new Vector3(_m * 0.032f, 0.03f, 0f) * k;
        _palm.Position = palmCentre;
        s.Box(Vector3.Zero, new Vector3(0.026f, 0.08f, 0.078f) * k, look.Palm);
        _palm.Mesh = s.Build();
        s.Clear();

        // the forearm's end: glove, sleeve or bare skin
        _cuff.Position = palmCentre + new Vector3(0f, 0.04f, 0.004f) * k;
        s.Tube(Vector3.Zero, new Vector3(0f, 0.09f, 0f) * k, 0.027f * k, 0.031f * k, look.Cuff, 7);
        _cuff.Mesh = s.Build();
        s.Clear();

        for (int f = 0; f < 5; f++)
        {
            float r = Radii[f] * k;
            for (int j = 0; j < 3; j++)
            {
                int b = f * 3 + j;
                float len = Lengths[b] * k;
                // the tube tapers to the tip; the finger's own colour (a fingerless glove leaves it bare)
                float r0 = r * (1f - 0.1f * j), r1 = r * (j == 2 ? 0.78f : 0.92f - 0.1f * j);
                s.Tube(Vector3.Zero, new Vector3(0f, -len, 0f), r0, r1, f == Thumb && j == 0 ? look.Palm : look.Fingers, 6);
                _bones[b].Mesh = s.Build();
                s.Clear();
                if (j > 0) _joints[b].Position = new Vector3(0f, -Lengths[b - 1] * k, 0f);
            }
        }
        // knuckles across the palm's end, the index on the thumb's side and the pinky's a little higher
        for (int f = 0; f < 4; f++)
            _joints[f * 3].Position = palmCentre + new Vector3(0f, -0.038f + 0.0035f * f, (-1.5f + f) * 0.019f) * k;

        // the thumb from the heel of the palm; up: off the controller, down: lying on its face
        _thumbBase.Position = new Vector3(_m * 0.024f, 0.045f, -0.03f) * k;
        _thumbUp = Aim(new Vector3(-_m * 0.15f, -0.45f, -0.88f));
        _thumbDown = Aim(new Vector3(-_m * 0.6f, -0.62f, -0.5f));
        Apply();
    }

    /// <summary>A bone's frame pointing along <paramref name="dir"/> (its −Y), bending about +Z toward the palm.</summary>
    private Basis Aim(Vector3 dir)
    {
        var y = -dir.Normalized();
        var toPalm = new Vector3(-_m, 0f, 0f);
        var x = (toPalm - y * toPalm.Dot(y)).Normalized();
        // turning −Y about +Z moves it toward +X: x is the way the bone bends
        return new Basis(x, y, x.Cross(y));
    }

    /// <summary>Turns the joints to <see cref="Bend"/> and <see cref="ThumbDown"/>. Allocates nothing.</summary>
    public void Apply()
    {
        for (int f = 0; f < 4; f++)
        {
            // a little spread, closing as the finger curls
            float splay = (1.5f - f) * 0.06f * (1f - Mathf.Clamp(Bend[f * 3] / 1.2f, 0f, 1f));
            for (int j = 0; j < 3; j++)
            {
                int b = f * 3 + j;
                // −Y bends toward the palm (−X right, +X left): about Z, the left hand the other way
                var bend = new Basis(Vector3.Back, -_m * Bend[b]);
                _joints[b].Basis = j == 0 ? new Basis(Vector3.Right, -splay) * bend : bend;
            }
        }
        _thumbBase.Basis = new Basis(new Quaternion(_thumbUp).Slerp(new Quaternion(_thumbDown), Mathf.Clamp(ThumbDown, 0f, 1f)));
        for (int j = 0; j < 3; j++)
            _joints[Thumb * 3 + j].Basis = new Basis(Vector3.Back, Bend[Thumb * 3 + j]);
    }

    /// <summary>
    /// The fingers from the hand tracker of this hand's side, or else from <paramref name="ctl"/>'s
    /// sensors, eased toward over a few frames. The tracker is this hand's side: left-handed play
    /// swaps the controller nodes, not the hands (#439).
    /// </summary>
    public void Drive(XRController3D ctl, float dt)
    {
        float ease;
        Tracked = XRServer.GetTracker(_right ? RightTracker : LeftTracker) is XRHandTracker { HasTrackingData: true } tracker
            && FromTracker(tracker);
        if (Tracked) ease = MathX.Damp(40f, dt);
        else
        {
            FromController(ctl);
            ease = MathX.Damp(22f, dt);
        }
        for (int b = 0; b < Bones; b++) Bend[b] += (_target[b] - Bend[b]) * ease;
        ThumbDown += (_thumbTarget - ThumbDown) * ease;
        Apply();
    }

    private float _thumbTarget;

    private void FromController(XRController3D ctl)
    {
        float grip = ctl.GetFloat(GripAction), trigger = ctl.GetFloat(TriggerAction);
        bool onTrigger = trigger > 0.05f || ctl.IsButtonPressed(TriggerTouch);
        bool thumbOn = ctl.IsButtonPressed(AxTouch) || ctl.IsButtonPressed(ByTouch)
            || ctl.IsButtonPressed(StickTouch) || ctl.IsButtonPressed(RestTouch);
        for (int j = 0; j < 3; j++)
        {
            // the index: pointing, resting on the trigger, pulling it
            float rest = onTrigger ? TouchBend[j] : OpenBend[j];
            _target[j] = Mathf.Lerp(rest, FistBend[j], trigger);
            for (int f = 1; f < 4; f++)
                _target[f * 3 + j] = Mathf.Lerp(OpenBend[f * 3 + j], FistBend[f * 3 + j], grip);
            // the thumb curls over the fist when it is up, lies flat on the buttons when down
            _target[Thumb * 3 + j] = thumbOn ? OpenBend[Thumb * 3 + j] : Mathf.Lerp(OpenBend[Thumb * 3 + j], FistBend[Thumb * 3 + j], grip * 0.4f);
        }
        _thumbTarget = thumbOn ? 1f : 0f;
    }

    /// <summary>Sets the fingers at once as the controller would: for a still picture (the viewer, <c>--xrhands</c>).</summary>
    public void Hold(float grip, float trigger, float thumb)
    {
        for (int j = 0; j < 3; j++)
        {
            Bend[j] = Mathf.Lerp(trigger > 0f ? TouchBend[j] : OpenBend[j], FistBend[j], trigger);
            for (int f = 1; f < 5; f++)
                Bend[f * 3 + j] = Mathf.Lerp(OpenBend[f * 3 + j], FistBend[f * 3 + j], f == Thumb ? grip * 0.4f * (1f - thumb) : grip);
        }
        ThumbDown = thumb;
        Apply();
    }

    private static readonly XRHandTracker.HandJoint[][] Chains =
    [
        [XRHandTracker.HandJoint.IndexFingerMetacarpal, XRHandTracker.HandJoint.IndexFingerPhalanxProximal,
            XRHandTracker.HandJoint.IndexFingerPhalanxIntermediate, XRHandTracker.HandJoint.IndexFingerPhalanxDistal],
        [XRHandTracker.HandJoint.MiddleFingerMetacarpal, XRHandTracker.HandJoint.MiddleFingerPhalanxProximal,
            XRHandTracker.HandJoint.MiddleFingerPhalanxIntermediate, XRHandTracker.HandJoint.MiddleFingerPhalanxDistal],
        [XRHandTracker.HandJoint.RingFingerMetacarpal, XRHandTracker.HandJoint.RingFingerPhalanxProximal,
            XRHandTracker.HandJoint.RingFingerPhalanxIntermediate, XRHandTracker.HandJoint.RingFingerPhalanxDistal],
        [XRHandTracker.HandJoint.PinkyFingerMetacarpal, XRHandTracker.HandJoint.PinkyFingerPhalanxProximal,
            XRHandTracker.HandJoint.PinkyFingerPhalanxIntermediate, XRHandTracker.HandJoint.PinkyFingerPhalanxDistal],
        // the thumb: the palm, then metacarpal, proximal, distal
        [XRHandTracker.HandJoint.Palm, XRHandTracker.HandJoint.ThumbMetacarpal,
            XRHandTracker.HandJoint.ThumbPhalanxProximal, XRHandTracker.HandJoint.ThumbPhalanxDistal],
    ];

    /// <summary>
    /// Each joint's real bend: the next bone's direction (−Z) seen from the one before, toward the
    /// palm (−Y, OpenXR's joints have +Y out of the back of the hand). False when a joint is not known.
    /// </summary>
    private bool FromTracker(XRHandTracker tracker)
    {
        for (int f = 0; f < 5; f++)
        {
            var chain = Chains[f];
            for (int i = 0; i < 4; i++)
                if ((tracker.GetHandJointFlags(chain[i]) & XRHandTracker.HandJointFlags.OrientationValid) == 0) return false;
            for (int j = 0; j < 3; j++)
            {
                var a = tracker.GetHandJointTransform(chain[j]).Basis.Orthonormalized();
                var b = tracker.GetHandJointTransform(chain[j + 1]).Basis.Orthonormalized();
                var along = a.Inverse() * (b * Vector3.Forward);
                float bend = Mathf.Atan2(-along.Y, -along.Z);
                _target[f * 3 + j] = Mathf.Clamp(bend, -0.35f, 1.9f);
            }
        }
        // the thumb's own frame is the up / down blend: how far its metacarpal has swung from the
        // palm's side (up, about 70°) toward the index (down, about 35°)
        var palm = tracker.GetHandJointTransform(XRHandTracker.HandJoint.Palm).Basis.Orthonormalized();
        var meta = tracker.GetHandJointTransform(XRHandTracker.HandJoint.ThumbMetacarpal).Basis.Orthonormalized();
        float swing = (palm * Vector3.Forward).AngleTo(meta * Vector3.Forward);
        _thumbTarget = Mathf.Clamp((1.22f - swing) / 0.6f, 0f, 1f);
        _target[Thumb * 3] = 0f;
        return true;
    }

    [Showcase("Figures", "VR hand")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseHands()
    {
        (string Name, float Grip, float Trigger, float Thumb)[] poses =
            [("open", 0f, 0f, 0f), ("fist", 1f, 1f, 1f), ("point", 1f, 0f, 1f), ("thumbs up", 1f, 1f, 0f)];
        var gloved = HumanPalette.ForRider(3) with { Outfit = Outfit.Of([Items.ItemId.FingerlessGloves]) };
        foreach (var right in new[] { true, false })
            foreach (var (name, grip, trigger, thumb) in poses)
                yield return ($"{(right ? "right" : "left")} {name}", () => Posed(right, grip, trigger, thumb, HumanPalette.Default));
        yield return ("fingerless gloves", () => Posed(true, 0.4f, 0.2f, 1f, gloved));
    }

    /// <summary>A hand held as the controller fallback would hold it, scaled up to be seen.</summary>
    private static Node3D Posed(bool right, float grip, float trigger, float thumb, HumanPalette palette)
    {
        var hand = new XrHand(right) { Scale = Vector3.One * 6f };
        hand.SetLook(HumanMeshBuilder.HandsOf(palette));
        hand.Hold(grip, trigger, thumb);
        // shown as the headset would: the viewer draws every layer
        foreach (var part in hand.FindChildren("*", nameof(MeshInstance3D), true, false))
            ((MeshInstance3D)part).Layers = 1;
        return hand;
    }
}
