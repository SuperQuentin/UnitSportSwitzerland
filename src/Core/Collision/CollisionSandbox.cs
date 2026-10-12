using Godot;
using UnitSport.Player;

namespace UnitSport.Core.Collision;

/// <summary>
/// <c>--collidesandbox [car|walk|bike|truck|...] [--targets a,b] [--sandboxshot out.png] --world flat</c>
/// (#699): every <see cref="CollisionTargets"/> entry laid out on the flat world to run or drive
/// into by hand, one row per category, each with its name over it. The player starts at the near
/// end of the rows, mounted on the ride named (a car by default). Windowed; it never quits on its
/// own, unless <c>--sandboxshot</c> asks for one picture of the rows from above.
/// </summary>
public partial class CollisionSandbox : Node3D
{
    public static bool Requested() => CmdArgs.Has("--collidesandbox");

    private const float Gap = 6f, RowGap = 30f;

    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private RideKind _ride;
    private bool _mounted;
    private double _clock;
    private readonly string? _shot = CmdArgs.Value("--sandboxshot");

    public CollisionSandbox(WorldOrigin origin) => _origin = origin;

    public override void _Ready()
    {
        MouseCapture.Disabled = false;
        // vehicles are parked under it, as in the game: E gets into one
        if (Vehicles.VehicleManager.Instance == null) Vehicles.VehicleManager.Create(this, null, _origin);
        var word = CmdArgs.Value("--collidesandbox", notFlag: true);
        _ride = word is null or "" ? RideProbe.KindNamed("car")
            : word is "walk" or "foot" ? RideKind.OnFoot : RideProbe.KindNamed(word);
        var filters = CmdArgs.Value("--targets")?.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);

        float z = 0f;
        foreach (var row in CollisionTargets.All()
                     .Where(t => filters == null || filters.Any(f => t.Key.ToLowerInvariant().Contains(f)))
                     .GroupBy(t => t.Category))
        {
            float x = 0f, depth = 0f;
            foreach (var t in row)
            {
                // measured off the tree first, so the next one stands clear of it
                var probe = t.Drawn?.Invoke() ?? t.Build(_origin, Transform3D.Identity);
                var box = Avatar.MeshBounds.Of(probe);
                probe.Free();
                float width = Mathf.Max(box.Size.X, 1f);
                depth = Mathf.Max(depth, box.Size.Z);
                var at = new Vector3(x - box.Position.X, TestWorld.GroundY, -z - box.End.Z);
                CollisionTargets.Spawn(t, this, _origin, new Transform3D(Basis.Identity, at));
                AddChild(new Label3D
                {
                    Text = t.Name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 64, PixelSize = 0.01f,
                    Position = at + new Vector3(box.GetCenter().X, Mathf.Max(box.End.Y, 1f) + 1.2f, box.GetCenter().Z),
                    NoDepthTest = true,
                });
                x += width + Gap;
            }
            AddChild(new Label3D
            {
                Text = row.Key, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 160, PixelSize = 0.02f,
                Position = new Vector3(-12f, 6f, -z - depth * 0.5f), Modulate = new Color(1f, 0.85f, 0.3f),
            });
            z += depth + RowGap;
        }
        GD.Print($"[collidesandbox] laid out {GetChildren().Count} nodes; riding {_ride}");

        _player = new FootPlayer { Name = "Player" };
        // facing the rows (north, -Z), from beside the first one
        AddChild(_player);
        _player.GlobalPosition = new Vector3(-20f, TestWorld.GroundY + 1.2f, 20f);
        _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_shot != null && (_clock += delta) > 4.0) { Shoot(_shot); return; }
        if (_mounted || _player == null || !_player.IsOnFloor()) return;
        _mounted = true;
        if (_ride != RideKind.OnFoot && !_player.SetRide(_ride)) GD.PushWarning($"[collidesandbox] could not mount {_ride}");
    }

    /// <summary>--sandboxshot: the rows from above and behind the player, written, then quit.</summary>
    private void Shoot(string path)
    {
        var cam = new Camera3D { Fov = 60f, Far = 4000f };
        AddChild(cam);
        cam.GlobalPosition = new Vector3(-30f, 45f, 45f);
        cam.LookAt(new Vector3(40f, 0f, -60f), Vector3.Up);
        cam.MakeCurrent();
        RenderingServer.FramePostDraw += Save;

        void Save()
        {
            RenderingServer.FramePostDraw -= Save;
            GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok
                ? $"[collidesandbox] wrote {path}" : $"[collidesandbox] FAILED to write {path}");
            GetTree().Quit();
        }
        SetPhysicsProcess(false);
    }
}
