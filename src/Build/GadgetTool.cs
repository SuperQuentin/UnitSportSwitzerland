using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>
/// Gadgets in hand and in use (#275). Held: a ghost where Use would set it down (green or red, a
/// line saying why not); Use places it through <see cref="PlacedObjects"/> (the item leaves the pack
/// and comes back on a refusal), Aim + Use takes your own back. A zipline takes two Uses: its start
/// at the top, then its end at the bottom. In use, for the local player only (the body's position
/// is what others see): <see cref="InteractMount"/> by the zipline's high post rides it, at a ladder's
/// foot climbs it, on a launch pad fires you up into a wingsuit glide; walking or landing onto a
/// trampoline bounces you. Rides hold the body through <see cref="FootPlayer.Carrier"/> and let go
/// with <see cref="FootPlayer.Release"/> / <see cref="FootPlayer.Leap"/>. Docs: <c>docs/notes/build/gadgets.md</c>.
/// </summary>
public partial class GadgetTool : Node
{
    public const float Reach = 6f;
    private const float ZipSpeedMax = 16f, ClimbSpeed = 2.2f, LaunchHeight = 80f, LaunchSeconds = 2.6f, BounceSpeed = 15.5f;

    private readonly ItemController _items;
    private MeshInstance3D _ghost = null!, _cable = null!;
    private StandardMaterial3D _material = null!;
    private Label _hint = null!;
    private Vector3? _zipStart;   // world foot of the high post, once set
    private bool _busy;

    private enum Mode { None, Zip, Ladder, Launch }
    private Mode _mode;
    private PlacedObject? _riding;
    private float _along, _speed, _height, _t;
    private ulong _nextBounceMs;

    public GadgetTool(ItemController items) => _items = items;
    public GadgetTool() : this(null!) { }

    /// <summary>Probes: show the ghost with the pointer free, and drive the climb (+1 up, -1 down).</summary>
    public bool AlwaysShow { get; set; }
    public float? ForceClimb { get; set; }
    /// <summary>Probes: what the ghost last said.</summary>
    public (bool Valid, string Reason) Last { get; private set; }
    public bool Riding => _mode != Mode.None;
    public int Bounces { get; private set; }

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled, AlbedoColor = new Color(0.3f, 1f, 0.4f, 0.35f),
        };
        _ghost = new MeshInstance3D { Name = "GadgetGhost", MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false };
        AddChild(_ghost);
        _cable = new MeshInstance3D { Name = "CableGhost", Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 1f) }, MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, Visible = false };
        AddChild(_cable);
        var layer = new CanvasLayer { Layer = 4 };
        AddChild(layer);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _hint.AnchorLeft = 0f; _hint.AnchorRight = 1f; _hint.AnchorTop = 0.58f; _hint.AnchorBottom = 0.58f;
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _hint.AddThemeConstantOverride("outline_size", 5);
        layer.AddChild(_hint);
    }

    // ---- placing ---------------------------------------------------------------------------------

    private readonly record struct Spot(bool Valid, string Reason, Transform3D At, string Payload, ArrayMesh? Mesh);

    /// <summary>Where the held gadget would go: on the ground in front, facing you (a ladder: hung from a ledge's top).</summary>
    private Spot Aim(FootPlayer player, PlacedKind kind)
    {
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var fwd = -camera.GlobalTransform.Basis.Z;
        float reach = Reach + from.DistanceTo(player.GlobalPosition + Vector3.Up * 1.6f);
        var space = player.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + fwd * reach, uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() }));
        if (hit.Count == 0) return new Spot(false, "Nothing in reach to set it on.", default, "", null);
        var point = hit["position"].AsVector3();
        var normal = hit["normal"].AsVector3();
        var toPlayer = (player.GlobalPosition - point) with { Y = 0 };
        float yaw = toPlayer.LengthSquared() > 1e-4f ? Mathf.Atan2(toPlayer.X, toPlayer.Z) : 0f;
        var at = new Transform3D(new Basis(Vector3.Up, yaw), point);
        if (Interiors.InteriorManager.Instance?.Current != null) return new Spot(false, "Not indoors.", at, "", null);
        if (normal.Y < 0.75f) return new Spot(false, "Too steep here: aim at something flat.", at, "", null);
        if (point.DistanceTo(player.GlobalPosition) > Reach) return new Spot(false, "Too far away.", at, "", null);

        switch (kind)
        {
            case PlacedKind.RopeLadder:
            {
                // the top of a ledge: the ladder hangs down the drop on the player's side of the spot
                var side = toPlayer.Normalized();
                var probe = point + side * 0.45f + Vector3.Up * 0.1f;
                var down = space.IntersectRay(PhysicsRayQueryParameters3D.Create(probe, probe + Vector3.Down * (Gadgets.LadderMax + 1f), uint.MaxValue,
                    new Godot.Collections.Array<Rid> { player.GetRid() }));
                float drop = down.Count > 0 ? probe.Y - down["position"].AsVector3().Y : Gadgets.LadderMax + 1f;
                float length = Mathf.Min(drop, Gadgets.LadderMax);
                var top = new Transform3D(at.Basis, point + side * 0.15f);
                return drop < Gadgets.LadderMin
                    ? new Spot(false, "Aim at the top edge of a wall or a ledge.", top, "", GadgetMeshes.Ladder(Gadgets.LadderMax))
                    : new Spot(true, drop > Gadgets.LadderMax ? $"{Gadgets.LadderMax:F0} m: it will not reach the bottom." : "", top,
                        Gadgets.LadderPayload(length), GadgetMeshes.Ladder(length));
            }
            case PlacedKind.Zipline when _zipStart is { } start:
            {
                string? problem = Gadgets.ZipProblem(start, point);
                var local = at.AffineInverse() * start;
                var (e, n) = PlacedObjects.Instance!.Origin.ToLv95(start);
                return new Spot(problem == null, problem ?? "", at, Gadgets.ZipPayload(e, n, start.Y), GadgetMeshes.Zipline(local));
            }
            default:
                return new Spot(true, "", at, "", GadgetMeshes.Mesh(kind));
        }
    }

    /// <summary>Every frame from the item controller: the ghost while a gadget is in hand (null player or none: hidden).</summary>
    public void Step(FootPlayer? player, ItemId held, bool aiming)
    {
        bool show = player != null && Gadgets.KindOf.TryGetValue(held, out _) && !Riding
                    && (Input.MouseMode == Input.MouseModeEnum.Captured || AlwaysShow);
        if (held != ItemId.Zipline) _zipStart = null;
        if (!show)
        {
            _ghost.Visible = _cable.Visible = false;
            if (!Riding) _hint.Visible = false;
            return;
        }
        var kind = Gadgets.KindOf[held];
        var name = ItemDefs.Get(held)?.Name ?? "";
        if (aiming)
        {
            _ghost.Visible = _cable.Visible = false;
            _hint.Text = InputHints.Format($"{name}\n{{use_item}}: take back a {name.ToLowerInvariant()} you set down");
            _hint.Visible = true;
            return;
        }
        var spot = Aim(player!, kind);
        Last = (spot.Valid, spot.Reason);
        _ghost.Visible = spot.Mesh != null;
        if (spot.Mesh != null)
        {
            _ghost.Mesh = spot.Mesh;
            _ghost.GlobalTransform = spot.At;
        }
        _material.AlbedoColor = spot.Valid ? new Color(0.3f, 1f, 0.4f, 0.35f) : new Color(1f, 0.25f, 0.2f, 0.35f);
        _cable.Visible = false;
        string action = kind == PlacedKind.Zipline ? (_zipStart == null ? "set the top post" : "set the bottom post and string the cable") : "set it down";
        _hint.Text = $"{name}\n" + (spot.Valid ? InputHints.Format($"{{use_item}}: {action}") + (spot.Reason.Length > 0 ? $" ({spot.Reason})" : "") : spot.Reason);
        _hint.Visible = true;
    }

    /// <summary>Use (or Aim + Use: take back) with a gadget in <paramref name="slot"/>.</summary>
    public void Use(FootPlayer player, int slot, bool aiming)
    {
        if (_busy || Riding || PlacedObjects.Instance is not { } placed) return;
        var held = _items.Inventory[slot].Id;
        if (!Gadgets.KindOf.TryGetValue(held, out var kind)) return;

        if (aiming)
        {
            var near = Nearest(player.GlobalPosition, 4f, k => k == kind);
            if (near == null) { _items.Toast($"No {ItemDefs.Get(held)?.Name.ToLowerInvariant()} of yours here."); return; }
            _busy = true;
            placed.RequestRemove(near.Id, r =>
            {
                _busy = false;
                if (r.Ok) _items.Give(new ItemStack(held, 1));
                else _items.Toast($"Cannot take it: {r.Refused}");
            });
            return;
        }

        var spot = Aim(player, kind);
        if (kind == PlacedKind.Zipline && _zipStart == null)
        {
            if (spot.Mesh == null || (!spot.Valid && spot.Reason.Length > 0)) { _items.Toast(spot.Reason); return; }
            _zipStart = spot.At.Origin;
            _items.Toast("Top post set. Now walk down to where it ends and Use again.");
            return;
        }
        if (!spot.Valid) { _items.Toast(spot.Reason); return; }

        _items.Inventory.TakeOne(slot);
        _busy = true;
        placed.RequestPlace(kind, spot.At, spot.Payload, r =>
        {
            _busy = false;
            if (r.Ok) { _zipStart = null; return; }
            _items.Give(new ItemStack(held, 1));
            _items.Toast($"Cannot set it here: {r.Refused}");
        });
    }

    /// <summary>The nearest placed gadget of a kind within <paramref name="within"/> metres of a point.</summary>
    private static PlacedObject? Nearest(Vector3 at, float within, Func<PlacedKind, bool> kind)
    {
        if (PlacedObjects.Instance is not { } placed) return null;
        PlacedObject? best = null;
        float bestD = within;
        foreach (var o in placed.All.Values)
        {
            if (!kind(o.Kind)) continue;
            float d = o.WorldTransform(placed.Origin).Origin.DistanceTo(at);
            if (d < bestD) { bestD = d; best = o; }
        }
        return best;
    }

    // ---- riding ----------------------------------------------------------------------------------

    /// <summary>A zipline's high and low cable points in world space (where the hands go).</summary>
    private static (Vector3 High, Vector3 Low)? Cable(PlacedObject o)
    {
        if (PlacedObjects.Instance is not { } placed || Gadgets.ZipStart(o.Payload) is not { } s) return null;
        var up = Vector3.Up * Gadgets.PostHeight;
        return (placed.Origin.ToWorld(s.E, s.N, s.Alt) + up, o.WorldTransform(placed.Origin).Origin + up);
    }

    /// <summary>What <see cref="InteractMount"/> would start here, for the prompt and for <see cref="TryInteract"/>.</summary>
    private (Mode Mode, PlacedObject O)? Offer(FootPlayer p)
    {
        if (PlacedObjects.Instance is not { } placed) return null;
        var pos = p.GlobalPosition;
        foreach (var o in placed.All.Values)
        {
            var at = o.WorldTransform(placed.Origin);
            switch (o.Kind)
            {
                case PlacedKind.Zipline when Cable(o) is { } c:
                {
                    var foot = c.High - Vector3.Up * Gadgets.PostHeight;
                    if (Flat(pos - foot) < 2f && Mathf.Abs(pos.Y - foot.Y) < 2f) return (Mode.Zip, o);
                    break;
                }
                case PlacedKind.RopeLadder:
                {
                    float len = Gadgets.LadderLength(o.Payload) ?? Gadgets.LadderMax;
                    var bottom = at.Origin - Vector3.Up * len;
                    if (Flat(pos - at.Origin) < 1.2f && pos.Y > bottom.Y - 1f && pos.Y < at.Origin.Y + 0.5f) return (Mode.Ladder, o);
                    break;
                }
                case PlacedKind.LaunchPad:
                    if (Flat(pos - at.Origin) < 1.1f && Mathf.Abs(pos.Y - at.Origin.Y - 0.28f) < 0.6f) return (Mode.Launch, o);
                    break;
            }
        }
        return null;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>E by a gadget: starts the ride. False when there is none here (E is left to the rest).</summary>
    public bool TryInteract(FootPlayer p)
    {
        if (Riding) return false;
        if (Offer(p) is not { } offer) return false;
        Board(p, offer.Mode, offer.O);
        return true;
    }

    /// <summary>Starts a ride (probes call it directly).</summary>
    public void Board(FootPlayer p, PlacedKind kind)
    {
        var mode = kind switch { PlacedKind.Zipline => Mode.Zip, PlacedKind.RopeLadder => Mode.Ladder, _ => Mode.Launch };
        if (Offer(p) is { } offer && offer.Mode == mode) Board(p, mode, offer.O);
    }

    private void Board(FootPlayer p, Mode mode, PlacedObject o)
    {
        _mode = mode;
        _riding = o;
        _t = 0;
        _speed = 2f;
        _along = 0;
        if (mode == Mode.Ladder)
        {
            float len = Gadgets.LadderLength(o.Payload) ?? Gadgets.LadderMax;
            var top = o.WorldTransform(PlacedObjects.Instance!.Origin).Origin;
            _height = Mathf.Clamp(p.GlobalPosition.Y - (top.Y - len), 0, len - 0.5f);
        }
        p.ShowWhileCarried = true;
        p.Carrier = () => Hold(p);
    }

    /// <summary>The carrier: where the ride holds the body this physics tick. Also moves the ride along.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? Hold(FootPlayer p)
    {
        if (_riding is not { } o || PlacedObjects.Instance is not { } placed || !placed.All.ContainsKey(o.Id))
        {
            Off(p, p.GlobalPosition, Vector3.Zero);
            return null;
        }
        float dt = (float)GetPhysicsProcessDeltaTime();
        bool jump = Input.IsActionJustPressed(PlayerInput.Jump);
        switch (_mode)
        {
            case Mode.Zip when Cable(o) is { } c:
            {
                var dir = (c.Low - c.High).Normalized();
                float len = c.High.DistanceTo(c.Low);
                float slope = Mathf.Clamp(-dir.Y, 0, 1);
                // gravity along the cable, air and pulley drag: tops out near ZipSpeedMax on a steep one
                _speed += (9.8f * slope - 0.03f * _speed * _speed - 0.4f) * dt;
                _speed = Mathf.Clamp(_speed, 1.5f, ZipSpeedMax);
                _along += _speed * dt;
                var hands = c.High + dir * Mathf.Min(_along, len);
                var at = hands - Vector3.Up * 2.05f;
                var vel = dir * _speed;
                if (jump) { Off(p, at, vel + Vector3.Up * 2f); return null; }
                if (_along >= len - 1.5f) { Off(p, at + dir * 0.8f, vel * 0.3f); return null; }
                return (at, Mathf.Atan2(-dir.X, -dir.Z), vel);
            }
            case Mode.Ladder:
            {
                var frame = o.WorldTransform(placed.Origin);
                float len = Gadgets.LadderLength(o.Payload) ?? Gadgets.LadderMax;
                var facing = frame.Basis.Z;   // toward the climber's side
                float input = ForceClimb ?? (PlayerInput.Held(PlayerInput.MoveForward) ? 1 : 0) - (PlayerInput.Held(PlayerInput.MoveBack) ? 1 : 0);
                _height += input * ClimbSpeed * dt;
                var bottom = frame.Origin - Vector3.Up * len;
                var at = bottom + Vector3.Up * Mathf.Max(_height, 0) + facing * 0.45f;
                if (jump) { Off(p, at + facing * 0.3f, facing * 3f + Vector3.Up * 2f); return null; }
                if (_height >= len - 0.2f && input > 0) { Off(p, frame.Origin - facing * 0.8f + Vector3.Up * 0.1f, Vector3.Zero); return null; }
                if (_height <= 0 && input < 0) { Off(p, at, Vector3.Zero); return null; }
                return (at, Mathf.Atan2(facing.X, facing.Z), Vector3.Up * input * ClimbSpeed);
            }
            case Mode.Launch:
            {
                var frame = o.WorldTransform(placed.Origin);
                var ahead = -frame.Basis.Z;   // away from whoever set it down: the chevrons
                _t += dt;
                float k = Mathf.Clamp(_t / LaunchSeconds, 0, 1);
                float rise = LaunchHeight * (1 - (1 - k) * (1 - k));
                var at = frame.Origin + Vector3.Up * (0.3f + rise) + ahead * 10f * k;
                var vel = Vector3.Up * LaunchHeight * 2 * (1 - k) / LaunchSeconds + ahead * 10f / LaunchSeconds;
                if (k >= 1)
                {
                    Off(p, null, null);
                    p.Leap(at, ahead * 24f + Vector3.Down * 3f, RideKind.Wingsuit);
                    return null;
                }
                return (at, Mathf.Atan2(-ahead.X, -ahead.Z), vel);
            }
        }
        Off(p, p.GlobalPosition, Vector3.Zero);
        return null;
    }

    /// <summary>Ends the ride; with a place, lets the body go there on foot (null: the caller leaps).</summary>
    private void Off(FootPlayer p, Vector3? at, Vector3? velocity)
    {
        _mode = Mode.None;
        _riding = null;
        if (at is { } a) p.Release(a, velocity ?? Vector3.Zero);
    }

    /// <summary>Every frame from the item controller with the local player: the prompt, and the trampoline.</summary>
    public void Tick(FootPlayer? p)
    {
        if (p == null) return;
        if (Riding)
        {
            _hint.Text = _mode switch
            {
                Mode.Zip => InputHints.Format("{jump}: let go"),
                Mode.Ladder => InputHints.Format("{move_forward} / {move_back}: climb · {jump}: let go"),
                _ => "",
            };
            _hint.Visible = _hint.Text.Length > 0;
            return;
        }
        if (Offer(p) is { } offer && !Gadgets.KindOf.ContainsKey(_items.Inventory.HeldId))
        {
            _hint.Text = InputHints.Format(offer.Mode switch
            {
                Mode.Zip => "{interact_mount}: ride the zipline",
                Mode.Ladder => "{interact_mount}: climb the ladder",
                _ => "{interact_mount}: launch",
            });
            _hint.Visible = true;
        }
        else if (!Gadgets.KindOf.ContainsKey(_items.Inventory.HeldId)) _hint.Visible = false;

        // a trampoline: walking or landing onto its mat throws you up
        if (Time.GetTicksMsec() < _nextBounceMs || p.Ride != RideKind.OnFoot) return;
        if (Nearest(p.GlobalPosition, 1.6f, k => k == PlacedKind.Trampoline) is not { } t || PlacedObjects.Instance is not { } placed) return;
        var mat = t.WorldTransform(placed.Origin).Origin + Vector3.Up * 0.46f;
        if (Flat(p.GlobalPosition - mat) > 1.1f || p.GlobalPosition.Y < mat.Y - 0.25f || p.GlobalPosition.Y > mat.Y + 0.4f || p.Velocity.Y > 0.5f) return;
        _nextBounceMs = Time.GetTicksMsec() + 600;
        Bounces++;
        var flat = p.Velocity with { Y = 0 };
        p.Release(p.GlobalPosition + Vector3.Up * 0.15f, flat * 0.8f + Vector3.Up * BounceSpeed);
        p.SoftLanding = true;   // a bounce pad, not a cliff: coming down from it does not hurt
    }
}
