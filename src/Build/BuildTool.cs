using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>What the hammer would do right now: build a piece (valid or not, and why), or take one back.</summary>
public readonly record struct BuildAim(
    bool Valid, string Reason, long Structure, Transform3D NewAt, Piece Piece, Transform3D World, bool PickUp, Slot PickSlot);

/// <summary>
/// The hammer in hand (#274). Every frame it works out the piece the view points at
/// (<see cref="Aim"/>): on the structure looked at or one close by, else a new structure whose first
/// cell sits where you look; a green or red ghost shows it, with a line saying what it costs or why
/// not. Use builds it (the materials leave the pack at once and come back if the server refuses);
/// Aim + Use takes back your own piece. Aim + next item (wheel, D-pad right) picks the piece, build turn
/// (R, D-pad up) turns it, Aim + build turn the
/// material. <see cref="TryHit"/> is the weapons' side: a shot that meets a piece first damages it.
/// Docs: <c>docs/notes/build/building.md</c>.
/// </summary>
public partial class BuildTool : Node
{
    /// <summary>How far from the body a piece may be built (the server allows a little more).</summary>
    public const float Reach = 6.5f;
    /// <summary>A structure this close (metres from any of its pieces) takes the new piece onto its grid.</summary>
    private const float JoinDistance = 4f;

    private readonly ItemController _items;
    private MeshInstance3D _ghost = null!;
    private StandardMaterial3D _material = null!;
    private Label _hint = null!;
    private int _kind;
    private BuildMaterial _mat = BuildMaterial.Wood;
    private int _turn;
    private bool _busy;
    private ulong _nextMs;

    public BuildTool(ItemController items) => _items = items;
    public BuildTool() : this(null!) { }

    public PieceKind Kind => BuildGrid.Kinds[_kind];
    public BuildMaterial Material => _mat;
    /// <summary>The last aim shown (for probes).</summary>
    public BuildAim Last { get; private set; }
    /// <summary>Probes: show the ghost even with the pointer free (headless has no captured mouse).</summary>
    public bool AlwaysShow { get; set; }

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(0.3f, 1f, 0.4f, 0.35f),
        };
        _ghost = new MeshInstance3D
        {
            Name = "BuildGhost", MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true, Visible = false,
        };
        AddChild(_ghost);
        var layer = new CanvasLayer { Layer = 4 };
        AddChild(layer);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AnchorLeft = 0f; _hint.AnchorRight = 1f; _hint.AnchorTop = 0.58f; _hint.AnchorBottom = 0.58f;
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _hint.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hint);
    }

    // ---- selection ------------------------------------------------------------------------------

    public void CyclePiece(int step)
    {
        _kind = (_kind + step + BuildGrid.Kinds.Length) % BuildGrid.Kinds.Length;
        if (!BuildGrid.Allowed(Kind, _mat)) _mat = BuildMaterial.Wood;
    }

    public void CycleMaterial()
    {
        do _mat = (BuildMaterial)(((int)_mat + 1) % BuildGrid.Materials.Length);
        while (!BuildGrid.Allowed(Kind, _mat));
    }

    public void Turn() => _turn = (_turn + 1) & 3;

    /// <summary>For probes: picks a piece and material outright.</summary>
    public void Select(PieceKind kind, BuildMaterial material, int turn = 0)
    {
        _kind = Array.IndexOf(BuildGrid.Kinds, kind);
        _mat = material;
        _turn = turn & 3;
    }

    // ---- aiming ---------------------------------------------------------------------------------

    /// <summary>The direction (0 −Z, 1 +X, 2 +Z, 3 −X) a structure-local vector mostly points.</summary>
    private static int DirOf(Vector3 v) =>
        Mathf.Abs(v.X) > Mathf.Abs(v.Z) ? (v.X > 0 ? 1 : 3) : (v.Z > 0 ? 2 : 0);

    public BuildAim Aim(FootPlayer player, bool pickUp)
    {
        var structures = Structures.Instance;
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var fwd = -camera.GlobalTransform.Basis.Z;
        float reach = Reach + from.DistanceTo(player.GlobalPosition + Vector3.Up * 1.6f);
        var query = PhysicsRayQueryParameters3D.Create(from, from + fwd * reach, uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() });
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        var hitNode = hit.Count > 0 ? hit["collider"].AsGodotObject() as Node : null;
        var onPiece = Structures.PieceOf(hitNode);
        BuildAim Fail(string why) => new(false, why, 0, default, default, default, false, default);

        if (structures == null) return Fail("");
        if (pickUp)
            return onPiece is { } op && structures.All.ContainsKey(op.Structure)
                ? new BuildAim(true, "", op.Structure, default, default, default, true, op.Slot)
                : Fail("Aim at a piece you built to take it back.");

        if (InteriorManagerOpen()) return Fail("Not indoors.");
        var point = hit.Count > 0 ? hit["position"].AsVector3() : from + fwd * reach;
        point -= fwd * 0.05f;   // just on this side of the surface looked at

        // which grid: the piece looked at, else a structure close by, else a new one here
        Structure? s = onPiece is { } on && structures.All.TryGetValue(on.Structure, out var sOn) ? sOn : null;
        if (s == null)
        {
            float best = JoinDistance;
            foreach (var cand in structures.All.Values)
                foreach (var p in cand.Pieces.Values)
                {
                    float d = structures.Centre(cand, p.Piece).DistanceTo(point);
                    if (d < best) { best = d; s = cand; }
                }
        }
        if (s == null && hit.Count == 0) return Fail("Look at the ground, or at something you built.");

        const float S = BuildGrid.Cell, H = BuildGrid.Storey;
        Transform3D frame, newAt = default;
        Vector3 q;
        if (s != null)
        {
            frame = s.WorldTransform(structures.Origin);
            q = frame.AffineInverse() * point;
        }
        else
        {
            // a new grid squared to the view: the first cell in front, centred on the spot (an edge piece on it)
            var flat = (fwd with { Y = 0 }).Normalized();
            float yaw = Mathf.Atan2(-flat.X, -flat.Z);
            var basis = new Basis(Vector3.Up, yaw);
            bool edge = BuildGrid.ClassOf(Kind) == SlotClass.Edge;
            var corner = point - basis * new Vector3(S / 2, 0, edge ? 0 : S / 2);
            frame = newAt = new Transform3D(basis, corner);
            q = frame.AffineInverse() * point;
        }

        var fLocal = frame.Basis.Inverse() * fwd;
        int cx = Mathf.FloorToInt(q.X / S), cz = Mathf.FloorToInt(q.Z / S);
        int level = Mathf.FloorToInt((q.Y + 0.3f) / H);
        Slot slot;
        int dir = 0;
        switch (BuildGrid.ClassOf(Kind))
        {
            case SlotClass.Floor:
                slot = Slot.Floor(cx, Mathf.RoundToInt(q.Y / H), cz);
                break;
            case SlotClass.Edge:
            {
                // the cell's edge nearest the spot, then turned by R
                float fx = q.X - cx * S, fz = q.Z - cz * S;
                var edges = new[] { fz, S - fx, S - fz, fx };   // distance to sides 0, 1, 2, 3
                int side = Array.IndexOf(edges, edges.Min());
                slot = Slot.Edge(cx, level, cz, side + _turn);
                break;
            }
            default:
                slot = Slot.Volume(cx, level, cz);
                dir = (DirOf(fLocal) + _turn) & 3;
                break;
        }

        var piece = new Piece(slot, Kind, dir, _mat, false);
        var world = frame * Structures.LocalTransform(piece);
        var (grounded, groundWhy) = Ground(structures, world, Kind);
        piece = piece with { Grounded = grounded };

        string reason = groundWhy
            ?? (world.Origin.DistanceTo(player.GlobalPosition) > Reach + BuildGrid.Cell ? "Too far away." : null)
            ?? (s != null ? BuildGrid.CannotPlace(s.Grid, piece) : BuildGrid.CannotPlace(Array.Empty<Piece>(), piece))
            ?? Missing();
        return new BuildAim(reason.Length == 0, reason, s?.Id ?? 0, newAt, piece, world, false, default);
    }

    private static bool InteriorManagerOpen() => Interiors.InteriorManager.Instance?.Current != null;

    /// <summary>
    /// Whether a piece stands on the terrain: its foot no more than a storey above the highest ground
    /// under it (lower down another storey would fit), not buried, and its legs no longer than
    /// <see cref="BuildGrid.StiltMax"/>. Null reason when fine; a reason when it goes into the ground.
    /// </summary>
    private static (bool Grounded, string? Why) Ground(Structures structures, Transform3D world, PieceKind kind)
    {
        var feet = StructureMeshes.Feet(kind);
        if (feet.Length == 0 || structures.GroundAt == null) return (false, null);
        float gMin = float.MaxValue, gMax = float.MinValue;
        foreach (var f in feet)
        {
            var at = world * f;
            if (structures.GroundAt(at) is not { } h) return (false, null);
            gMin = Mathf.Min(gMin, at.Y - h);
            gMax = Mathf.Max(gMax, at.Y - h);
        }
        if (gMin < -1.2f) return (false, "Into the ground: aim higher.");
        return (gMin < BuildGrid.Storey - 0.3f && gMax <= BuildGrid.StiltMax, null);
    }

    private string? Missing()
    {
        var inv = _items.Inventory;
        foreach (var (id, n) in BuildGrid.Cost(Kind, _mat))
            if (inv.CountPlain(id) < n) return $"Needs {n} {ItemDefs.Get(id)?.Name} (you have {inv.CountPlain(id)}).";
        return "";
    }

    private string CostText() => string.Join(" + ", BuildGrid.Cost(Kind, _mat).Select(c => $"{c.Count} {ItemDefs.Get(c.Id)?.Name}"));

    // ---- every frame ----------------------------------------------------------------------------

    /// <summary>Called every frame by the item controller; a null player or no hammer in hand hides everything.</summary>
    public void Step(FootPlayer? player, bool active, bool aiming)
    {
        if (player == null || !active || (Input.MouseMode != Input.MouseModeEnum.Captured && !AlwaysShow))
        {
            _ghost.Visible = false;
            _hint.Visible = false;
            Last = default;
            return;
        }
        var aim = Aim(player, pickUp: false);
        Last = aim;
        string what = $"{BuildGrid.Name(Kind)} · {BuildGrid.Spec(_mat).Name}";
        if (aim.World == default)
        {
            _ghost.Visible = false;
            _hint.Text = $"{what}\n{aim.Reason}";
        }
        else
        {
            _ghost.Mesh = StructureMeshes.Mesh(Kind, _mat);
            _ghost.GlobalTransform = aim.World;
            _ghost.Visible = true;
            _material.AlbedoColor = aim.Valid ? new Color(0.3f, 1f, 0.4f, 0.35f) : new Color(1f, 0.25f, 0.2f, 0.35f);
            _hint.Text = aiming
                ? $"{what}\n" + InputHints.Format("{next_item}: piece · {build_turn}: material · {use_item}: take back your piece")
                : aim.Valid ? $"{what} — {CostText()}\n" + InputHints.Format("{use_item}: build · {build_turn}: turn · {aim_item} + {next_item}: piece")
                : $"{what}\n{aim.Reason}";
        }
        _hint.Visible = _hint.Text.Length > 0;
    }

    // ---- using it -------------------------------------------------------------------------------

    /// <summary>Use (or Aim + Use: take back). The materials leave the pack now and come back on a refusal.</summary>
    public void Use(FootPlayer player, bool aiming)
    {
        if (_busy || Time.GetTicksMsec() < _nextMs || Structures.Instance is not { } structures) return;
        _nextMs = Time.GetTicksMsec() + 250;
        var aim = Aim(player, aiming);
        if (!aim.Valid)
        {
            if (aim.Reason.Length > 0) _items.Toast(aim.Reason);
            return;
        }

        var inv = _items.Inventory;
        if (aim.PickUp)
        {
            if (!structures.All.TryGetValue(aim.Structure, out var s) || !s.Pieces.TryGetValue(aim.PickSlot, out var p)) return;
            var refund = BuildGrid.Cost(p.Piece.Kind, p.Piece.Material).ToList();
            _busy = true;
            structures.RequestRemove(aim.Structure, aim.PickSlot, refused =>
            {
                _busy = false;
                if (refused != null)
                {
                    _items.Toast($"Cannot take it: {refused}");
                    return;
                }
                foreach (var (id, n) in refund) _items.Give(new ItemStack(id, n));
            });
            return;
        }

        var cost = BuildGrid.Cost(aim.Piece.Kind, aim.Piece.Material).ToList();
        using (inv.Batch())
            foreach (var (id, n) in cost) inv.TakePlain(id, n);
        _busy = true;
        Kick(player);
        structures.RequestBuild(aim.Structure, aim.NewAt, aim.Piece, refused =>
        {
            _busy = false;
            if (refused == null) return;
            foreach (var (id, n) in cost) _items.Give(new ItemStack(id, n));
            _items.Toast($"Cannot build it: {refused}");
        });
    }

    private static void Kick(FootPlayer player)
    {
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v) v.Kick = 1f;
    }

    /// <summary>
    /// A shot or a stab from <paramref name="eye"/> along <paramref name="aim"/>: if the first thing
    /// it meets within the weapon's range is a piece, the piece takes the damage (sent to the
    /// server). A shotgun's pellets mostly land on a wall that close, so most of them count.
    /// </summary>
    public static bool TryHit(FootPlayer shooter, Vector3 eye, Vector3 aim, WeaponDef weapon)
    {
        if (Structures.Instance is not { } structures) return false;
        var query = PhysicsRayQueryParameters3D.Create(eye, eye + aim.Normalized() * weapon.Range, uint.MaxValue,
            new Godot.Collections.Array<Rid> { shooter.GetRid() });
        var hit = shooter.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0 || Structures.PieceOf(hit["collider"].AsGodotObject() as Node) is not { } piece) return false;
        float t = eye.DistanceTo(hit["position"].AsVector3());
        float damage = Mathf.Min(weapon.DamageAt(t) * weapon.Pellets * (weapon.Pellets > 1 ? 0.8f : 1f), weapon.MaxHit);
        structures.SendHit(piece.Structure, piece.Slot, damage, weapon.Id);
        return true;
    }
}
