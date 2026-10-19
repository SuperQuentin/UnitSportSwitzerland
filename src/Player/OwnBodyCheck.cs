using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--ownbodycheck[,dir] --world flat</c> (#833): in first person the player's own figure is still
/// the third-person one, built and posed, only kept out of the eye's cameras, so it casts its shadow
/// and the mirrors show it. Headless it checks the setup: the figure and its hand exist, it is on
/// <see cref="Avatar.OwnBody.Layer"/>, the eye's camera leaves that layer out, and a shot still
/// leaves from the eye. In a window it also measures the pictures: the screen with the figure
/// against without it (its shadow on the ground, nothing else), and a wall mirror's view the same
/// way (the figure in the glass). With a dir, the pictures are written there. Last, in a car's
/// cockpit: the head, and in a bare cockpit the whole driver, on the own-body layer the same way.
/// </summary>
public partial class OwnBodyCheck : Node3D
{
    public static bool Requested => CmdArgs.FlagWithShot("--ownbodycheck").Requested;

    /// <summary>Pixels that must change, in a picture, for the figure or its shadow to count as drawn.</summary>
    private const float MinChanged = 0.002f;
    /// <summary>Frames to let a change reach the picture: a mirror renders every other frame.</summary>
    private const int Settle = 12;

    private readonly string? _dir = CmdArgs.FlagWithShot("--ownbodycheck").Shot;
    private readonly bool _windowed = DisplayServer.GetName() != "headless";
    private FootPlayer? _player;
    private Interiors.WallMirror? _mirror;
    private Image? _with;
    private int _step, _wait;
    private int _failures;

    public OwnBodyCheck()
    {
        // first person for this run only: the player's view is taken from it as it is made
        GameSettings.Current.ThirdPerson = false;
    }

    private void Check(bool ok, string what)
    {
        if (!ok) _failures++;
        GD.Print($"[ownbody] {(ok ? "ok  " : "FAIL")} {what}");
    }

    public override void _Process(double delta)
    {
        if (_wait > 0) { _wait--; return; }
        switch (_step++)
        {
            case 0:
                Spawn();
                _wait = 30;
                break;
            case 1:
                CheckSetup();
                if (!_windowed) { _step = 6; return; }
                ShadowView();
                _wait = Settle;
                break;
            case 2:
                _with = Picture();
                Save(_with, "screen_with.png");
                Body().Visible = false;
                _wait = Settle;
                break;
            case 3:
            {
                var without = Picture();
                Save(without, "screen_without.png");
                Check(Changed(_with!, without) >= MinChanged,
                    $"its shadow is on the ground: {Changed(_with!, without):P2} of the screen changes with the figure");
                Body().Visible = true;
                MirrorView();
                _wait = Settle;
                break;
            }
            case 4:
                _with = MirrorPicture();
                if (_with != null) Save(_with, "mirror_with.png");
                Body().Visible = false;
                _wait = Settle;
                break;
            case 5:
            {
                var without = MirrorPicture();
                if (without != null) Save(without, "mirror_without.png");
                Check(_with != null && without != null && Changed(_with, without) >= MinChanged,
                    $"the mirror shows it: {(_with != null && without != null ? Changed(_with, without) : 0f):P2} of the glass changes with the figure");
                Body().Visible = true;
                break;
            }
            case 6:
                // the cockpit with your body first, whatever the saved setting says (it is restored at the end)
                _savedCockpitBody = GameSettings.Current.CockpitBody;
                GameSettings.Current.CockpitBody = true;
                Check(_player!.SetRide((RideKind)CarCatalog.First), "mounted a car, in its cockpit with your body");
                _wait = Settle;
                break;
            case 7:
                CheckCockpit(bare: false);
                // the cockpit without your body: V's third step
                GameSettings.Current.CockpitBody = false;
                _wait = Settle;
                break;
            case 8:
                CheckCockpit(bare: true);
                Finish();
                break;
        }
    }

    /// <summary>
    /// In the seat's eye the head is still drawn, on the own-body layer with its shadow twin; so is the
    /// whole driver in a bare cockpit, while with a body the arms and legs are for the eye too.
    /// </summary>
    private void CheckCockpit(bool bare)
    {
        string view = bare ? "bare cockpit" : "cockpit";
        // not by name: the rig is added while the walker it replaces is still queued, so it is not "Body"
        Node? rig = null;
        foreach (var child in _player!.GetChildren())
            if (child is Avatar.CarRig) rig = child;
        var head = rig?.FindChild("DriverHead", true, false) as MeshInstance3D;
        var driver = rig?.FindChild("Driver", true, false) as MeshInstance3D;
        Check(_player.InCockpit && head != null && driver != null, $"{view}: the driver's head and body are on the rig");
        if (head == null || driver == null) return;
        Check(head.Visible && head.Layers == Avatar.OwnBody.Layer && TwinCasts(head),
            $"{view}: the head is drawn for the mirrors only, its shadow cast by its twin (layers {head.Layers:X})");
        uint want = bare ? Avatar.OwnBody.Layer : Avatar.OwnBody.World;
        Check(driver.Visible && driver.Layers == want && TwinCasts(driver) == bare,
            $"{view}: the body is {(bare ? "drawn for the mirrors only" : "seen by the eye")} (layers {driver.Layers:X})");
    }

    private static bool TwinCasts(MeshInstance3D figure) =>
        figure.GetNodeOrNull<MeshInstance3D>("Shadow") is { Visible: true, Layers: Avatar.OwnBody.World } twin
        && twin.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly && twin.Mesh == figure.Mesh;

    private void Spawn()
    {
        _player = new FootPlayer { Name = "Probe" };
        AddChild(_player);
        _player.GlobalPosition = new Vector3(0, TestWorld.GroundY + 0.05f, 0);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
    }

    private MeshInstance3D Body() => _player!.GetNode<MeshInstance3D>("Body");

    private void CheckSetup()
    {
        var p = _player!;
        Check(p.IsFirstPerson, "the player is in first person");
        var body = p.GetNodeOrNull<MeshInstance3D>("Body");
        Check(body != null, "first person still builds the on-foot figure");
        if (body == null) return;
        Check(p.BodyFromEye && body.Layers == Avatar.OwnBody.Layer, $"the figure is on the own-body layer only (layers {body.Layers:X})");
        // Godot casts no shadow from a layer the camera culls: a shadow-only twin on the world's layer does
        Check(TwinCasts(body), "a shadow-only twin of the figure, on the world's layer, casts its shadow");
        // by a doorway the figure is copied across (DoorwayGhosts); a copy of the twin is drawn, and the portal showed the head
        var ghosted = new List<MeshInstance3D>();
        Interiors.DoorwayGhosts.Collect(p, ghosted);
        Check(ghosted.Contains(body) && !ghosted.Any(m => m.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly),
            "a doorway's ghost copies the figure, never its shadow-only twin");
        Check((p.Camera.CullMask & Avatar.OwnBody.Layer) == 0, "the eye's camera leaves the own-body layer out");
        Check((Interiors.WallMirror.Cull & Avatar.OwnBody.Layer) != 0, "a wall mirror draws the own-body layer");
        Check(p.HandLocal != null, "the figure is posed: its hand is placed");
        var aim = -p.Camera.GlobalBasis.Z;
        var muzzle = Items.ItemEvents.MuzzleOf(p, aim);
        float off = (muzzle - p.Camera.GlobalPosition).Cross(aim).Length();
        Check(off < 0.25f, $"a first-person shot leaves from the eye, not the hand ({off:F2} m off the line of sight)");
    }

    /// <summary>The sun behind, the view down along its light: the figure's shadow lies ahead.</summary>
    private void ShadowView()
    {
        if (GetParent().GetNodeOrNull<DirectionalLight3D>("Sun") is { } sun) sun.ShadowEnabled = true;
        var light = -(GetParent().GetNodeOrNull<DirectionalLight3D>("Sun")?.GlobalBasis.Z ?? new Vector3(0, -1, -1));
        _player!.LookYaw = Mathf.Atan2(-light.X, -light.Z);
        _player.LookPitch = -0.9f;
    }

    /// <summary>A wall mirror 1.2 m ahead at its washbasin height, facing the player, who looks at it.</summary>
    private void MirrorView()
    {
        var p = _player!;
        p.LookYaw = 0f;
        p.LookPitch = -0.1f;
        _mirror = new Interiors.WallMirror { Name = "Mirror" };
        AddChild(_mirror);
        _mirror.GlobalPosition = p.GlobalPosition + new Vector3(0, 1.45f, -1.2f);
    }

    private Image Picture() => GetViewport().GetTexture().GetImage();

    private Image? MirrorPicture() => _mirror?.GetNodeOrNull<SubViewport>("View")?.GetTexture().GetImage();

    /// <summary>The share of pixels whose brightness differs by more than a PS1 dither step.</summary>
    private static float Changed(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize()) return 1f;
        int changed = 0, w = a.GetWidth(), h = a.GetHeight();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Mathf.Abs(a.GetPixel(x, y).Luminance - b.GetPixel(x, y).Luminance) > 0.06f) changed++;
        return changed / (float)(w * h);
    }

    private void Save(Image image, string name)
    {
        if (_dir == null) return;
        DirAccess.MakeDirRecursiveAbsolute(_dir);
        string path = $"{_dir}/{name}";
        GD.Print(image.SavePng(path) == Error.Ok ? $"[ownbody] wrote {path}" : $"[ownbody] FAILED to write {path}");
    }

    private bool? _savedCockpitBody;

    private void Finish()
    {
        if (_savedCockpitBody is { } saved) GameSettings.Current.CockpitBody = saved;
        GD.Print(_failures == 0 ? "[ownbody] RESULT: ok" : $"[ownbody] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
