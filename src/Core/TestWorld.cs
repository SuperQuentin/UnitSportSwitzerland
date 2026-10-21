using Godot;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// <c>--world flat</c> (#221, <c>docs/notes/general/testing.md</c>): a flat collision plane at
/// height 0, a fixed sun, and the one probe asked for. No <see cref="ChunkManager"/>, no map, no
/// generated world, no traffic, birds, interiors, occasions or network: what the probe needs and
/// nothing else. A probe runs here once it takes a null <see cref="ChunkManager"/> and asks for
/// the ground through <see cref="TryGround"/>.
/// </summary>
public partial class TestWorld : Node3D
{
    /// <summary>Height of the plane, in world metres.</summary>
    public const float GroundY = 0f;

    /// <summary>The ground under <paramref name="at"/>: the terrain's, or the flat plane's with no terrain.</summary>
    public static bool TryGround(ChunkManager? chunks, Vector3 at, out float y)
    {
        y = GroundY;
        return chunks == null || chunks.TryGetHeight(at, out y);
    }

    public override void _Ready()
    {
        GameSettings.Load();
        Audio.SfxBus.Ensure();
        PlayerInput.Install(GetParent());
        MouseCapture.Disabled = true;
        var (e, n) = SpawnPoint.ParseTarget();
        var origin = new WorldOrigin(e, n);

        // 20 km square, its top at GroundY: a box, which every physics engine collides with alike
        var ground = new StaticBody3D { Name = "Ground", Position = new Vector3(0, GroundY - 0.5f, 0) };
        ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(20000, 1, 20000) } });
        ground.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(20000, 1, 20000) } });
        AddChild(ground);
        AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-50, -30, 0) });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.6f, 0.6f),
            },
        });

        Node? probe = HitboxProbe.Requested() ? new HitboxProbe(null, origin)
            : SyncProbe.Requested() ? new SyncProbe(null, origin)
            : Movie.MovieProbe.Requested() ? new Movie.MovieProbe(origin)
            : Movie.MovieWorldProbe.Requested() ? new Movie.MovieWorldProbe(origin)
            : Movie.MovieShotProbe.Requested() ? new Movie.MovieShotProbe(origin)
            : ForkliftCheck.Requested ? new ForkliftCheck(origin)
            : ExcavatorCheck.Requested ? new ExcavatorCheck(origin)
            : LoaderCheck.Requested ? new LoaderCheck(origin)
            : RollerCheck.Requested ? new RollerCheck(origin)
            : TelehandlerCheck.Requested ? new TelehandlerCheck(origin)
            : TipperCheck.Requested ? new TipperCheck(origin)
            : DumperCheck.Requested ? new DumperCheck(origin)
            : Items.BedCheck.Requested ? new Items.BedCheck(origin)
            : Items.PalletCheck.Requested ? new Items.PalletCheck()
            : Items.ForkCheck.Requested ? new Items.ForkCheck(origin)
            : RideProbe.ParseArgs() is { } ride ? new RideProbe(null, origin, ride.Kind, ride.Seconds, ride.Shot)
            : FlightCheckProbe.ParseArgs() is { } fly ? new FlightCheckProbe(null, origin, fly.Kind, fly.Shot)
            : BeamCheck.Requested ? new BeamCheck(origin)
            : Terrain.Construction.ShellWalkProbe.Requested() ? new Terrain.Construction.ShellWalkProbe()
            : Terrain.Construction.CraneCheck.Requested ? new Terrain.Construction.CraneCheck(origin)
            : Collision.CollisionMatrixProbe.Requested() ? new Collision.CollisionMatrixProbe(origin)
            : Collision.CollisionSandbox.Requested() ? new Collision.CollisionSandbox(origin)
            : OwnBodyCheck.Requested ? new OwnBodyCheck()
            : CreativeCheck.Requested ? new CreativeCheck()
            : Concert.ConcertWalkCheck.Requested ? new Concert.ConcertWalkCheck()
            : null;
        if (probe == null)
        {
            GD.PushError("[testworld] no probe here runs on --world flat (--hitboxcheck, --synccheck, --moviecheck, --ride, --flycheck, --beamcheck, --forkliftcheck, --palletcheck, --shellwalkcheck, --cranecheck, --collidecheck, --collidesandbox, --ownbodycheck, --concertwalkcheck, --creativecheck)");
            GetTree().Quit(2);
            return;
        }
        GD.Print($"[testworld] flat world at LV95 {e:F0}/{n:F0}: {probe.GetType().Name}");
        // the UFOs' tractor beams (#861): a saucer flown here lifts what is under it
        Player.TractorBeams.Create(this);
        AddChild(probe);
    }
}
