using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.World;

namespace UnitSport.Items;

public enum TrafficAimKind { None, Place, Edit }

/// <summary>
/// Where the traffic tool points: a spot on a road to place a generator (valid or not, and why not: its point and the way it would
/// face, along the road the way the camera looks), or a placed one to open (<see cref="Id"/>).
/// </summary>
public readonly record struct TrafficAim(TrafficAimKind Kind, bool Valid, string Reason, Vector3 Point, Vector3 Heading, int Id);

/// <summary>
/// The traffic tool (#971, the user: placing generators from the debug menu was impractical), an admin's held item handed out by the
/// debug menu. Held, every placed generator and destination shows (<see cref="Traffic.EndpointsHeld"/>), and a ghost of one where
/// Use would put it: on the road the crosshair meets, facing along it the way the camera looks (green, or red where there is no
/// road), or a yellow halo on a placed one Use would open. Use opens <see cref="TrafficGeneratorUi"/>: a new one's settings,
/// placed on OK, or a placed one's, saved or removed. The same <see cref="Aim"/> decides what Use does, so the ghost never lies.
/// Keyboard and mouse, pad and VR all through <c>use_item</c> (click, RB, the right trigger); the window through the UI's own
/// keys, pad and laser.
/// </summary>
public partial class TrafficTool : Node
{
    /// <summary>How far the crosshair reaches for a road (m): a junction is placed from across it.</summary>
    public const float Reach = 400f;
    /// <summary>A placed one within this of where the crosshair meets the world is the one aimed at (m).</summary>
    public const float PickReach = 8f;
    /// <summary>A road within this of where the crosshair meets the world takes a new one (m).</summary>
    public const float RoadReach = 8f;

    private static readonly Color Valid = new(0.3f, 1f, 0.4f, 0.55f), Invalid = new(1f, 0.25f, 0.2f, 0.55f), Halo = new(1f, 0.9f, 0.3f, 0.6f);

    private readonly WorldOrigin _origin;
    private ImmediateMesh _mesh = null!;
    private MeshInstance3D _ghost = null!;
    private Label _hint = null!;
    private TrafficGeneratorUi _ui = null!;

    /// <summary>The last aim shown (for probes).</summary>
    public TrafficAim Last { get; private set; }
    public bool WindowOpen => _ui.IsOpen;

    public TrafficTool(WorldOrigin origin) => _origin = origin;
    public TrafficTool() : this(WorldOrigin.SwissDefault()) { }

    public override void _Ready()
    {
        _mesh = new ImmediateMesh();
        _ghost = new MeshInstance3D
        {
            Name = "TrafficGhost", Mesh = _mesh, TopLevel = true, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled, RenderPriority = 11,
            },
        };
        AddChild(_ghost);
        var layer = new CanvasLayer { Layer = 4 };
        AddChild(layer);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AnchorLeft = 0f; _hint.AnchorRight = 1f; _hint.AnchorTop = 0.58f; _hint.AnchorBottom = 0.58f;
        _hint.AddThemeFontSizeOverride("font_size", 20);
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _hint.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hint);
        _ui = new TrafficGeneratorUi { Name = "TrafficGeneratorUi" };
        AddChild(_ui);
    }

    /// <summary>Raycasts from the camera: a placed one near where it meets the world, else the road there.</summary>
    public TrafficAim Aim(FootPlayer player)
    {
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + forward * Reach,
            uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() }));
        if (hit.Count == 0) return new TrafficAim(TrafficAimKind.None, false, "Point at a road.", Vector3.Zero, Vector3.Zero, 0);
        var at = hit["position"].AsVector3();
        var traffic = Traffic.Current;

        // a placed one: the nearest within reach of the point
        if (TrafficGenerators.Instance is { } list)
        {
            var (e, n) = _origin.ToLv95(at);
            TrafficEndpoint? nearest = null;
            double best = PickReach * PickReach;
            foreach (var ep in list.All)
            {
                double d = (ep.E - e) * (ep.E - e) + (ep.N - n) * (ep.N - n);
                if (d < best) (best, nearest) = (d, ep);
            }
            if (nearest is not null)
            {
                var p = _origin.ToWorld(nearest.E, nearest.N, 0) with { Y = at.Y };
                if (traffic?.OnRoad(p, PickReach) is var (edge, pos, _)) (p, _) = edge.Sample(pos);
                return new TrafficAim(TrafficAimKind.Edit, true, "", p, HeadingOf(nearest.Heading), nearest.Id);
            }
        }

        if (traffic?.OnRoad(at, RoadReach) is not var (road, along, tangent))
            return new TrafficAim(TrafficAimKind.Place, false, "No road here.", at, Vector3.Zero, 0);
        var (point, _) = road.Sample(along);
        var heading = tangent.Dot(forward) >= 0f ? tangent : -tangent;
        return new TrafficAim(TrafficAimKind.Place, true, "", point, heading, 0);
    }

    /// <summary>LV95 heading (east 0, north π/2) as a world direction, and back.</summary>
    public static Vector3 HeadingOf(float lv95) => new(Mathf.Cos(lv95), 0f, -Mathf.Sin(lv95));
    public static float Lv95Heading(Vector3 world) => Mathf.Atan2(-world.Z, world.X);

    /// <summary>
    /// Called every frame by the item controller: <paramref name="active"/> while the tool is held and usable. Held, the placed
    /// ones show; the ghost and the hint only while the view is the player's (the mouse captured) and no window is open, except
    /// the new one the window is about to place, which stays where it was put.
    /// </summary>
    public void Step(FootPlayer? player, bool active)
    {
        if (Traffic.Current is { } traffic) traffic.EndpointsHeld = player != null && (active || _ui.IsOpen);
        if (player != null && _ui.IsOpen && _ui.Pending is { } pending)
        {
            Draw(_origin.ToWorld(pending.E, pending.N, 0) with { Y = _ui.PendingY }, HeadingOf(pending.Heading), Valid, halo: false);
            _hint.Visible = false;
            return;
        }
        if (player == null || !active || _ui.IsOpen || Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            _ghost.Visible = false;
            _hint.Visible = false;
            Last = default;
            return;
        }
        var aim = Aim(player);
        Last = aim;
        switch (aim.Kind)
        {
            case TrafficAimKind.Place when aim.Valid:
                Draw(aim.Point, aim.Heading, Valid, halo: false);
                _hint.Text = InputHints.Format("{use_item}: place a traffic generator");
                break;
            case TrafficAimKind.Place:
                Draw(aim.Point, Vector3.Zero, Invalid, halo: false);
                _hint.Text = aim.Reason;
                break;
            case TrafficAimKind.Edit:
                Draw(aim.Point, aim.Heading, Halo, halo: true);
                _hint.Text = InputHints.Format($"{{use_item}}: open generator #{aim.Id}");
                break;
            default:
                _ghost.Visible = false;
                _hint.Text = aim.Reason;
                break;
        }
        _hint.Visible = _hint.Text.Length > 0;
    }

    /// <summary>Use: a placed one's window, or a new one's at the aimed spot; an admin's only.</summary>
    public void Use(FootPlayer player, Action<string> say)
    {
        if (!Permissions.AdminTools) { say("Only an admin places traffic generators."); return; }
        if (TrafficGenerators.Instance is not { } list) { say("No traffic here."); return; }
        var aim = Aim(player);
        switch (aim.Kind)
        {
            case TrafficAimKind.Edit when list.All.FirstOrDefault(x => x.Id == aim.Id) is { } existing:
                _ui.Open(existing, isNew: false, aim.Point.Y);
                break;
            case TrafficAimKind.Place when aim.Valid:
                var (e, n) = _origin.ToLv95(aim.Point);
                _ui.Open(new TrafficEndpoint(0, e, n, Lv95Heading(aim.Heading), true, true, TrafficGenerators.DefaultPerMinute), isNew: true,
                    aim.Point.Y);
                break;
            default:
                say(aim.Reason.Length > 0 ? aim.Reason : "Point at a road.");
                break;
        }
    }

    /// <summary>A post with an arrow along <paramref name="heading"/> (none: the post alone), as the placed ones are drawn; a halo fatter.</summary>
    private void Draw(Vector3 at, Vector3 heading, Color colour, bool halo)
    {
        _mesh.ClearSurfaces();
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        float w = halo ? 1.6f : 1f;
        var top = at + Vector3.Up * 8f;
        Traffic.Ribbon(_mesh, at, top, colour, w);
        if (heading != Vector3.Zero)
        {
            var side = new Vector3(-heading.Z, 0f, heading.X);
            var tip = top + heading * 5f;
            Traffic.Ribbon(_mesh, top, tip, colour, w);
            Traffic.Ribbon(_mesh, tip, tip - heading * 1.5f + side, colour, w);
            Traffic.Ribbon(_mesh, tip, tip - heading * 1.5f - side, colour, w);
        }
        for (int i = 0; i < 16; i++)
        {
            float a = i * Mathf.Tau / 16f, b = (i + 1) * Mathf.Tau / 16f;
            Traffic.Ribbon(_mesh, at + new Vector3(Mathf.Cos(a), 0.3f, Mathf.Sin(a)) * 3f, at + new Vector3(Mathf.Cos(b), 0.3f, Mathf.Sin(b)) * 3f,
                colour, 0.5f * w);
        }
        _mesh.SurfaceEnd();
        _ghost.Visible = true;
    }
}
