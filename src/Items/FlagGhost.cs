using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

public enum FlagAimKind { None, Plant, PickUp }

/// <summary>
/// What the view meets, as far as a placeable is concerned: a spot to place it (valid or not, and why
/// not) or a placed one to take back (<see cref="Target"/>: its kind).
/// </summary>
public readonly record struct FlagAim(FlagAimKind Kind, bool Valid, string Reason, Vector3 Point, float Yaw, long Id,
    PlacedKind Target = PlacedKind.None);

/// <summary>
/// Holding a placeable (<see cref="Placeables"/>: the flag, a campfire, a field workbench): a
/// translucent ghost of it where Use would place it (green: fine, red: too far / too steep), a yellow
/// halo on the placed one Use would take back, and a one-line hint under the crosshair. With an empty
/// hand, the halo on a campfire or field workbench in reach (#272). The same <see cref="Aim"/> decides
/// what Use does, so the ghost never lies.
/// </summary>
public partial class FlagGhost : Node
{
    private MeshInstance3D _ghost = null!;
    private StandardMaterial3D _material = null!;
    private Label _hint = null!;
    private CanvasLayer _layer = null!;

    /// <summary>The last aim shown (for probes).</summary>
    public FlagAim Last { get; private set; }
    public bool Showing => _ghost.Visible;

    /// <summary>Raycasts from the camera for the flag (<see cref="Aim(FootPlayer, Placeable?)"/>).</summary>
    public static FlagAim Aim(FootPlayer player) => Aim(player, Placeables.ForItem(ItemId.SwissFlag));

    /// <summary>
    /// Raycasts from the camera. Third person looks from behind the shoulder, so reach is measured from
    /// the body. <paramref name="spec"/> null: an empty hand, which only takes back what
    /// <see cref="Placeables.TakenByHand"/>.
    /// </summary>
    public static FlagAim Aim(FootPlayer player, Placeable? spec)
    {
        var placed = PlacedObjects.Instance;
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        float reach = ItemController.PlaceReach + from.DistanceTo(player.GlobalPosition + Vector3.Up * 1.6f);
        var query = PhysicsRayQueryParameters3D.Create(from, from + forward * reach,
            uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() });
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return new FlagAim(FlagAimKind.None, false, "Nothing in reach to put it on.", Vector3.Zero, 0f, 0);

        if (placed != null && PlacedObjects.IdOf(hit["collider"].AsGodotObject() as Node) is long id
            && placed.All.TryGetValue(id, out var existing)
            && (spec != null ? existing.Kind == spec.Kind : Placeables.TakenByHand(existing.Kind)))
        {
            var at = existing.WorldTransform(placed.Origin);
            return new FlagAim(FlagAimKind.PickUp, true, "", at.Origin, at.Basis.GetEuler().Y, id, existing.Kind);
        }
        if (spec == null) return new FlagAim(FlagAimKind.None, false, "", Vector3.Zero, 0f, 0);

        var point = hit["position"].AsVector3();
        var normal = hit["normal"].AsVector3();
        var toPlayer = (player.GlobalPosition - point) with { Y = 0 };
        float yaw = toPlayer.LengthSquared() > 1e-4f ? Mathf.Atan2(toPlayer.X, toPlayer.Z) : 0f;   // the cloth faces the planter
        string reason = normal.Y < spec.MinNormalY ? spec.TooSteep
            : point.DistanceTo(player.GlobalPosition) > ItemController.PlaceReach ? "Too far away." : "";
        return new FlagAim(FlagAimKind.Plant, reason.Length == 0, reason, point, yaw, 0, spec.Kind);
    }

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.3f, 1f, 0.4f, 0.4f),
        };
        _ghost = new MeshInstance3D
        {
            Name = "FlagGhost", Mesh = ItemDefs.PlantedFlagMesh(), MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false,
        };
        AddChild(_ghost);
        _layer = new CanvasLayer { Layer = 4 };
        AddChild(_layer);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AnchorLeft = 0f; _hint.AnchorRight = 1f; _hint.AnchorTop = 0.58f; _hint.AnchorBottom = 0.58f;
        _hint.AddThemeFontSizeOverride("font_size", 20);
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _hint.AddThemeConstantOverride("outline_size", 5);
        _layer.AddChild(_hint);
    }

    /// <summary>Called every frame by the item controller with the held placeable (null: an empty hand); a null player or not active hides everything.</summary>
    public void Step(FootPlayer? player, bool active, Placeable? spec = null)
    {
        if (player == null || !active || Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            _ghost.Visible = false;
            _hint.Visible = false;
            Last = default;
            return;
        }
        var aim = Aim(player, spec);
        Last = aim;
        switch (aim.Kind)
        {
            case FlagAimKind.Plant:
                _ghost.Mesh = spec!.Mesh();
                _ghost.GlobalTransform = new Transform3D(new Basis(Vector3.Up, aim.Yaw), aim.Point);
                _ghost.Visible = true;
                _material.AlbedoColor = aim.Valid ? new Color(0.3f, 1f, 0.4f, 0.4f) : new Color(1f, 0.25f, 0.2f, 0.4f);
                _hint.Text = aim.Valid ? InputHints.Format("{use_item}: " + spec.Verb) : aim.Reason;
                break;
            case FlagAimKind.PickUp:
                // a halo: the same thing a little fatter, over the real one (a flag: fatter round its thin pole)
                _ghost.Mesh = Placeables.ForKind(aim.Target)?.Mesh() ?? ItemDefs.PlantedFlagMesh();
                var fat = aim.Target == PlacedKind.Flag ? new Vector3(1.6f, 1.02f, 1.6f) : new Vector3(1.06f, 1.06f, 1.06f);
                _ghost.GlobalTransform = new Transform3D(new Basis(Vector3.Up, aim.Yaw).Scaled(fat), aim.Point);
                _ghost.Visible = true;
                _material.AlbedoColor = new Color(1f, 0.9f, 0.3f, 0.35f);
                string verb = PlacedObjects.Instance?.All.GetValueOrDefault(aim.Id) is { } o ? Placeables.TakeVerb(o) : "pick up";
                _hint.Text = InputHints.Format("{use_item}: " + verb);
                break;
            default:
                _ghost.Visible = false;
                _hint.Text = "";
                break;
        }
        _hint.Visible = _hint.Text.Length > 0;
    }
}
