using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// <c>--togethercheck --world fixture --systems ui,terrain,physics,adventure</c> (quick tier, #1266): offline, this
/// client plays the server's part, and a local <c>Npc</c> body is the partner (<see cref="TogetherService.Pair"/>, the
/// way <c>BotProbe</c> drives a body). The together count and its chip; holding hands (both wrists posed to one point);
/// the partner walking away 20 m held by the tether and the hands paying out to a rope; back side by side to hands, then
/// led along the leader's trail with no input; a roped fall off a platform caught at the rope's length, hanging, and
/// hauled up; the company's regen; letting go. Windowed it saves <c>test_output/together_hands.png</c> and
/// <c>together_rope.png</c>. Progress in <c>test_output/togethercheck</c>, a scratch bank.
/// </summary>
public partial class TogetherCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--togethercheck");
    private static bool Windowed => DisplayServer.GetName() != "headless";
    private const long Pal = 2;

    private readonly Func<FootPlayer?> _me;
    private FootPlayer? _partner;
    private Vector3 _partnerWish, _myWish;
    private Camera3D? _cam;
    private ItemStack _keptSlot = ItemStack.Empty;

    public TogetherCheck(ItemController items, Func<FootPlayer?> me) : base(items, "togethercheck", "", "together_") => _me = me;
    public TogetherCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static TogetherService Svc => TogetherService.Instance!;

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => TogetherService.Instance != null && AdventureService.Instance != null && Me is { } m && m.IsOnFloor(), 90))
        {
            Fail($"no together service ({TogetherService.Instance != null}) or no player on the ground");
            return;
        }
        // the spawn may drop the body from high up and knock it out (as NavCheck waits for)
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var me = Me!;
        Expect(await Until(() => me.BodyPose.Basis.Y.Y > 0.95f, 20), "the player stands");
        Expect(Svc.Authority, "offline, this client keeps the links");
        Expect(Svc.NearHere == 0, "alone: nobody near");

        // the partner: an NPC body two metres east, in our party through the seam
        _partner = new FootPlayer { Name = "Partner", Npc = true, Terrain = me.Terrain, Origin = me.Origin };
        _partner.WalkControls = () => (_partnerWish, false);
        GetParent().AddChild(_partner);
        _partner.DebugLaunch(me.GlobalPosition + new Vector3(1.4f, 0.3f, 0f), Vector3.Zero);
        Svc.Pair(_partner, Pal);
        me.WalkControls = () => (_myWish, false);
        Expect(await Until(() => _partner.IsOnFloor(), 10), "the partner stands");
        Expect(await Until(() => Svc.NearHere == 1, 3), $"a party member near: together x1 ({Svc.NearHere})");
        Expect(Svc.Near(AdventureService.LocalPeer) == 1, "the authority counts it too");
        var chip = GetParent().GetNodeOrNull<TogetherChip>("TogetherChip");
        Expect(await Until(() => chip?.Shown == "Together ×1", 3), $"the chip says so ('{chip?.Shown}')");

        await Hands(me);
        await Tether(me);
        await Led(me);
        await Fall(me);

        Svc.ServeUnlink(AdventureService.LocalPeer, "the check");
        Expect(await Until(() => Svc.Links.Count == 0 && me.HandHold == null && _partner.HandHold == null, 3), "let go: no links, no held hands");
        me.WalkControls = null;
        if (!_keptSlot.IsEmpty) _items.Inventory.Put(0, _keptSlot);
        await Finish(0.5);
    }

    private async Task Hands(FootPlayer me)
    {
        // side by side, facing the same way, an arm's length apart
        me.LookYaw = 0f;
        _partner!.DebugLaunch(me.GlobalPosition + new Vector3(1.0f, 0.2f, 0f), Vector3.Zero);
        _partner.Rotation = Vector3.Zero;
        await Seconds(0.6);
        // an empty hand to hold with (a held item keeps the other hand, which may not reach across)
        _keptSlot = _items.Inventory[0];
        _items.Inventory.Put(0, ItemStack.Empty);
        _items.Inventory.Select(0);
        Expect(Svc.ServeLink(AdventureService.LocalPeer, Pal, LinkMode.Hand) == null, "the player takes the partner's hand");
        Expect(Svc.Links.ModeOf(Pal) == LinkMode.Hand && Svc.Links.LeaderOf(Pal) == AdventureService.LocalPeer, "a hand link, the player leads");
        Expect(await Until(() => me.HandHold != null && _partner!.HandHold != null, 2), "both bodies hold a hand");
        await Seconds(0.5);
        if (me.HoldWrist is { } a && _partner!.HoldWrist is { } b)
            Expect(a.DistanceTo(b) < 0.05f, FormattableString.Invariant($"the two wrists meet ({a.DistanceTo(b):F3} m apart, holding item {me.HeldItemId})"));
        else Expect(false, "posed wrists to compare");
        Expect(await Until(() => Svc.CompanyOf(AdventureService.LocalPeer) == StaminaCompany.Linked, 2), "standing together: linked regen");
        if (Windowed) GD.Print($"{Log} shot {await ShotAt("hands", me.GlobalPosition, _partner!.GlobalPosition, 3.2f)}");
    }

    private async Task Tether(FootPlayer me)
    {
        // the partner tries to walk 20 m east: the tether holds it, the hands pay out to a rope
        var start = _partner!.GlobalPosition;
        _partnerWish = Vector3.Right;
        await Seconds(7);
        _partnerWish = Vector3.Zero;
        float away = Flat(_partner.GlobalPosition - me.GlobalPosition);
        Expect(away < LinkBook.RopeLength + 1.5f, FormattableString.Invariant($"the tether held it within the rope ({away:F1} m, it walked {Flat(_partner.GlobalPosition - start):F1} m of 20)"));
        Expect(Svc.Links.ModeOf(Pal) == LinkMode.Rope, "pulled apart, the hands paid out to a rope");
        Expect(Svc.RopeOf(Pal) is { Visible: true }, "a rope is drawn between them");
        if (Windowed) GD.Print($"{Log} shot {await ShotAt("rope", me.GlobalPosition, _partner.GlobalPosition, 6f)}");
        // back side by side on easy ground: hands again
        _partnerWish = Vector3.Left;
        await Until(() => Flat(_partner.GlobalPosition - me.GlobalPosition) < 1.0f, 8);
        _partnerWish = Vector3.Zero;
        Expect(await Until(() => Svc.Links.ModeOf(Pal) == LinkMode.Hand, 3), $"side by side on easy ground: hands again ({Svc.Links.ModeOf(Pal)})");
    }

    private async Task Led(FootPlayer me)
    {
        // the leader walks north; the follower, given no input, is walked along the trail
        var from = _partner!.GlobalPosition;
        var leaderFrom = me.GlobalPosition;
        _myWish = Vector3.Forward;
        await Seconds(9);
        _myWish = Vector3.Zero;
        await Seconds(1.5);
        float led = Flat(me.GlobalPosition - leaderFrom);
        float moved = Flat(_partner.GlobalPosition - from), gap = Flat(_partner.GlobalPosition - me.GlobalPosition);
        Expect(led > 10f && moved > led - 1.5f && gap < 2.5f,
            FormattableString.Invariant($"led along the trail with no input: the leader walked {led:F1} m, the follower {moved:F1} m, {gap:F1} m behind"));
        Expect(Svc.Links.ModeOf(Pal) == LinkMode.Hand, "still hand in hand");
    }

    private async Task Fall(FootPlayer me)
    {
        // a 12 m block, the player on top, the partner stepping off its edge on the rope
        Expect(Svc.ServeLink(AdventureService.LocalPeer, Pal, LinkMode.Rope) == null && Svc.Links.Forced(Pal), "roped up (forced)");
        var ground = me.GlobalPosition;
        var block = new StaticBody3D { Name = "TogetherBlock" };
        block.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(6, 12, 6) } });
        block.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(6, 12, 6) } });
        GetParent().AddChild(block);
        block.GlobalPosition = ground + new Vector3(0, 6f, -4f);
        var top = ground + new Vector3(0, 12.3f, -4f);
        me.DebugLaunch(top, Vector3.Zero);
        _partner!.DebugLaunch(top + new Vector3(1.2f, 0f, 0f), Vector3.Zero);
        await Until(() => me.IsOnFloor() && _partner.IsOnFloor(), 3);
        await Seconds(0.5);
        Expect(!Svc.Hanging(Pal) && !Svc.Hanging(AdventureService.LocalPeer), "both on the block: nobody hangs");
        _partner.DebugLaunch(top + new Vector3(3.6f, 0.2f, 0f), new Vector3(2f, 0, 0));
        Expect(await Until(() => Svc.Hanging(Pal), 4), "the partner's fall is caught by the rope");
        await Seconds(2.5);
        float hang = (_partner.GlobalPosition - me.GlobalPosition).Length();
        Expect(hang > 6.5f && hang < 9f && _partner.GlobalPosition.Y > ground.Y + 2f,
            FormattableString.Invariant($"it hangs at the rope's length ({hang:F1} m, {_partner.GlobalPosition.Y - ground.Y:F1} m over the ground)"));
        Svc.ServeHaul(AdventureService.LocalPeer, true);
        await Seconds(3);
        Svc.ServeHaul(AdventureService.LocalPeer, false);
        float hauled = (_partner.GlobalPosition - me.GlobalPosition).Length();
        Expect(hauled < hang - 1f, FormattableString.Invariant($"hauled up the rope ({hang:F1} -> {hauled:F1} m)"));
        block.QueueFree();
        me.DebugLaunch(ground + Vector3.Up * 0.3f, Vector3.Zero);
        _partner.DebugLaunch(ground + new Vector3(1.2f, 0.3f, 0), Vector3.Zero);
        await Seconds(1);
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>A picture of the pair from the side and a little above.</summary>
    private async Task<string> ShotAt(string name, Vector3 a, Vector3 b, float back)
    {
        var mid = (a + b) * 0.5f + Vector3.Up;
        _cam ??= new Camera3D { Name = "TogetherCam", Fov = 50f };
        if (_cam.GetParent() == null) GetParent().AddChild(_cam);
        var across = (b - a) with { Y = 0 };
        var side = across.LengthSquared() > 0.01f ? across.Normalized().Cross(Vector3.Up) : Vector3.Back;
        _cam.GlobalPosition = mid + side * back + Vector3.Up * back * 0.35f;
        _cam.LookAt(mid, Vector3.Up);
        var was = GetViewport().GetCamera3D();
        _cam.MakeCurrent();
        for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string path = Shot(name);
        was?.MakeCurrent();
        return path;
    }
}
