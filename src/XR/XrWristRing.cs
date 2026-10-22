using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.XR;

/// <summary>
/// The radial menu over the watch (#1097): twist the left wrist to read the watch and a ring of
/// actions opens just above it, facing you. The right hand's ray aims a wedge and the trigger picks
/// it; twisting the watch away shuts the ring. <i>More…</i> opens the full wrist list
/// (<see cref="XrWristMenu"/>), which has everything the ring has no room for.
///
/// <para>
/// The ring is <see cref="Radial"/>, as the quick menu and the emote wheel draw it, in a small
/// SubViewport redrawn only when the aimed wedge changes, on a quad in the headset only. Each wedge
/// presses the action it stands for (<see cref="XrPad.Tap"/>) or runs the wrist menu's own entry.
/// </para>
/// </summary>
internal sealed partial class XrWristRing : Node3D
{
    /// <summary>The watch turned to the eyes this long opens the ring, s.</summary>
    private const float Dwell = 0.35f;
    /// <summary>The watch counts as read within this angle of the view's centre, radians.</summary>
    private const float LookCone = 0.5f;
    /// <summary>And within this of the eyes, m.</summary>
    private const float Near = 0.65f;
    /// <summary>The face turned toward the eyes at least this much (cosine) opens, and below <see cref="CloseFacing"/> shuts.</summary>
    private const float OpenFacing = 0.55f, CloseFacing = 0.15f;
    /// <summary>The ring's width in the world, m, and how far over the watch its centre floats.</summary>
    private const float Size = 0.24f, Above = 0.12f;
    private const int Pixels = 512;
    /// <summary>In the picture: the ring's radii, as the quick menu's (<see cref="QuickMenu"/>).</summary>
    private const float Inner = 74f, Outer = 214f;

    /// <summary>One wedge: its name, why it cannot be used now (null: it can), what picking it does.</summary>
    private sealed record Wedge(string Name, Func<FootPlayer?, string?> Refusal, Action<FootPlayer?> Pick);

    private readonly XrRig _rig;
    private readonly XrWristMenu _list;
    private readonly Wedge[] _wedges;
    private SubViewport _view = null!;
    private Face _face = null!;
    private MeshInstance3D _quad = null!;
    private float _looked;
    private bool _triggerWas = true;
    private FootPlayer? _player;

    /// <summary>Open with the right hand's ray on it: the trigger picks here, it is not a use.</summary>
    public bool Pointing { get; private set; }

    public bool IsOpen => Visible;

    public XrWristRing(XrRig rig, XrWristMenu list)
    {
        _rig = rig;
        _list = list;
        static string? OnFootOnly(FootPlayer? p) => p is { Ride: RideKind.OnFoot, RidingWith: 0 } ? null : "On foot only";
        static string? Always(FootPlayer? p) => null;
        _wedges = new Wedge[]
        {
            new("Travel", p => OnFootOnly(p) ?? (Permissions.InMatch ? "Not in a match" : null), _ => Tap(PlayerInput.RideMenu)),
            new("Inventory", OnFootOnly, _ => Tap(PlayerInput.Inventory)),
            new("Map", Always, _ => Tap(PlayerInput.Teleport)),
            new("Phone", OnFootOnly, _ => Tap(PlayerInput.Phone)),
            new("Hand map", OnFootOnly, _ => _rig.ToggleHandMap()),
            new("Recentre", Always, _ => _rig.Recentre()),
            new("Controls", Always, _ => Tap(PlayerInput.Help)),
            new("More…", Always, p => _list.Open(p)),
        };
    }

    // after the ring has let the trigger go, so the screen the action opens takes the pointer
    private static void Tap(string action) => Callable.From(() => XrPad.Tap(action)).CallDeferred();

    public override void _Ready()
    {
        Name = "WristRing";
        TopLevel = true;
        Visible = false;
        _view = new SubViewport
        {
            Size = new Vector2I(Pixels, Pixels),
            TransparentBg = true,
            Disable3D = true,
            GuiDisableInput = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        AddChild(_view);
        _face = new Face(this) { Size = new Vector2(Pixels, Pixels) };
        _view.AddChild(_face);
        _quad = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(Size, Size) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = XrSession.HeadsetOnlyLayer,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = _view.GetTexture(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                // over the dashboard, the sleeve and the hands: it is a menu, not a thing in the car
                NoDepthTest = true,
                RenderPriority = (int)Material.RenderPriorityMax - 1,
            },
        };
        AddChild(_quad);
    }

    /// <summary>
    /// Once a frame, from the rig, after the hands are placed: opens on a read watch, follows it,
    /// aims with <paramref name="aim"/> (the right hand's aim pose) and picks on its trigger.
    /// </summary>
    /// <param name="watch">The watch face: +Z out of the dial, +Y toward 12.</param>
    /// <param name="inWorld">Not the title's backdrop, which has its own menus.</param>
    public void Update(Transform3D head, Transform3D watch, bool watchShown, XRController3D aim, FootPlayer? player, bool inWorld, float dt)
    {
        _player = player;
        var to = head.Origin - watch.Origin;
        float d = to.Length();
        float facing = d > 0.01f ? watch.Basis.Z.Normalized().Dot(to / d) : 0f;
        bool free = inWorld && watchShown && !_list.IsOpen && MouseCapture.Mode == Input.MouseModeEnum.Captured;

        if (IsOpen)
        {
            // twisted away, lowered or out of reach: shut, nothing picked
            if (!free || facing < CloseFacing || d > Near + 0.25f) Close();
        }
        else
        {
            bool read = free && d > 0.1f && d < Near && facing > OpenFacing && (-head.Basis.Z).AngleTo(-to) < LookCone;
            _looked = read ? _looked + dt : 0f;
            if (_looked >= Dwell)
            {
                _looked = 0f;
                Open(watch, head, aim);
            }
        }
        if (!IsOpen)
        {
            Pointing = false;
            return;
        }

        Place(watch, head, dt);
        AimAndPick(aim, player);
    }

    private void Open(Transform3D watch, Transform3D head, XRController3D aim)
    {
        Visible = true;
        _placed = false;
        Place(watch, head, 1f);
        _triggerWas = true;   // a trigger already held does not pick the moment it opens
        _face.Highlight = -1;
        Redraw();
        aim.TriggerHapticPulse("haptic", 0.0, 0.3, 0.04, 0.0);
        GD.Print("[xr] wrist ring open");
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visible = false;
        Pointing = false;
        _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    private bool _placed;
    private Vector3 _at;

    /// <summary>Over the watch, square to the eyes, eased so the wrist's tremble does not shake it.</summary>
    private void Place(Transform3D watch, Transform3D head, float dt)
    {
        var want = watch.Origin + Vector3.Up * Above;
        _at = _placed ? _at.Lerp(want, MathX.Damp(14f, dt)) : want;
        _placed = true;
        var toEyes = head.Origin - _at;
        if (toEyes.LengthSquared() < 1e-4f) return;
        // the quad's front is +Z: face the eyes, upright in the world
        GlobalTransform = Transform3D.Identity.Translated(_at).LookingAt(_at - toEyes, Vector3.Up);
    }

    private void AimAndPick(XRController3D aim, FootPlayer? player)
    {
        int highlight = -1;
        Pointing = false;
        if (aim.GetHasTrackingData())
        {
            var plane = new Godot.Plane(GlobalBasis.Z, GlobalPosition);
            var from = aim.GlobalPosition;
            var hit = plane.IntersectsRay(from, -aim.GlobalBasis.Z);
            if (hit is { } at)
            {
                // the hit in the picture's terms: x right, y down, the ring's outer edge 1
                var local = GlobalTransform.AffineInverse() * at;
                var p = new Vector2(local.X, -local.Y) / (Size * 0.5f) * (Pixels * 0.5f / Outer);
                if (p.Length() < 1.15f)
                {
                    Pointing = true;
                    highlight = Radial.Aimed(p, _wedges.Length, Inner / Outer);
                }
            }
        }
        if (highlight != _face.Highlight)
        {
            _face.Highlight = highlight;
            Redraw();
            if (highlight >= 0) aim.TriggerHapticPulse("haptic", 0.0, 0.1, 0.015, 0.0);
        }

        bool trigger = aim.GetFloat("trigger") > 0.6f;
        if (trigger && !_triggerWas && Pointing && highlight >= 0 && _wedges[highlight].Refusal(player) == null)
        {
            var wedge = _wedges[highlight];
            GD.Print($"[xr] wrist ring: {wedge.Name}");
            Close();
            wedge.Pick(player);
        }
        _triggerWas = trigger;
    }

    /// <summary>"--xrring" with <c>--xrsim</c>: the ring opened in front of the head with a wedge lit, for a headset shot.</summary>
    public void ShowForShot(Transform3D head, int highlight)
    {
        Visible = true;
        var at = head * new Vector3(0f, -0.05f, -0.4f);
        GlobalTransform = Transform3D.Identity.Translated(at).LookingAt(at - (head.Origin - at), Vector3.Up);
        _face.Highlight = highlight;
        Redraw();
    }

    private void Redraw()
    {
        _face.QueueRedraw();
        _view.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    /// <summary>The ring's picture: the wedges, faint when they cannot be used now, the aimed one's name in the middle.</summary>
    private sealed partial class Face : Control
    {
        private readonly XrWristRing _ring;
        public int Highlight = -1;

        public Face(XrWristRing ring) => _ring = ring;
        public Face() : this(null!) { }

        public override void _Draw()
        {
            var wedges = _ring._wedges;
            var player = _ring._player;
            var centre = Size * 0.5f;
            Radial.Draw(this, centre, Inner, Outer, wedges.Length, Highlight, i => wedges[i].Name,
                i => wedges[i].Refusal(player) != null ? UiTheme.TextFaint : i == Highlight ? UiTheme.Amber : UiTheme.Text);
            string middle = "Point, pull", small = "";
            var colour = UiTheme.TextDim;
            if (Highlight >= 0)
            {
                middle = wedges[Highlight].Name;
                small = wedges[Highlight].Refusal(player) ?? "";
                colour = small.Length == 0 ? UiTheme.Amber : UiTheme.TextFaint;
            }
            DrawString(UiTheme.Bold, centre + new Vector2(-Inner, small.Length > 0 ? 0f : 6f), middle, HorizontalAlignment.Center,
                Inner * 2f, UiTheme.FontBody, colour);
            if (small.Length > 0)
                DrawString(UiTheme.Font, centre + new Vector2(-Inner, 18f), small, HorizontalAlignment.Center, Inner * 2f,
                    UiTheme.FontTiny, UiTheme.TextFaint);
        }
    }
}
