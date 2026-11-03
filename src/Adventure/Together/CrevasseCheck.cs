using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// <c>--crevassecheck --world fixture --systems ui,terrain,physics,adventure</c> (quick tier, #1372): offline, on the fixture
/// course with a pretend glacier box round the player (<see cref="TogetherService.GlacierBox"/>) and a local <c>Npc</c> body as the
/// rope partner (<see cref="TogetherService.Pair"/>). Walks across hidden crevasses: with the setting off nothing breaks; unroped
/// the bridge breaks, the slot opens with its own collision, the body falls 10 m onto the ice floor in <c>PoseCrevasse</c>,
/// unhurt, with the terrain layer out of its mask, and climbs the wall out onto solid ground; a body that cannot climb is set on
/// the lip; roped to a standing partner the rope holds the fall 3 m under the lip and the partner hauls it out. Progress in
/// <c>test_output/crevassecheck</c>, a scratch bank (never the real saves).
/// </summary>
public partial class CrevasseCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--crevassecheck");
    private const long Pal = 2;

    private readonly Func<FootPlayer?> _me;
    private FootPlayer? _partner;
    private Vector3 _myWish;
    private readonly ItemStack[] _kept = new ItemStack[Inventory.Size];

    public CrevasseCheck(ItemController items, Func<FootPlayer?> me) : base(items, "crevassecheck", "", "crevasse_") => _me = me;
    public CrevasseCheck() : this(null!, () => null) { }

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
        // the spawn may drop the body from high up and knock it out
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var me = Me!;
        for (int i = 0; i < Inventory.Size; i++) _kept[i] = _items.Inventory[i];
        bool setting = GameSettings.Current.Crevasses;
        try { await Run(me); }
        catch (Exception e) { Fail($"exception {e}"); return; }
        finally
        {
            GameSettings.Current.Crevasses = setting;
            TogetherService.RescueAfter = Crevasses.RescueSeconds;
            me.WalkControls = null;
            me.LeaveCrevasse();
            for (int i = 0; i < Inventory.Size; i++) _items.Inventory.Put(i, _kept[i]);
        }
        await Finish(0.5);
    }

    private async Task Run(FootPlayer me)
    {
        _partner = new FootPlayer { Name = "Partner", Npc = true, Terrain = me.Terrain, Origin = me.Origin };
        _partner.WalkControls = () => (Vector3.Zero, false);
        GetParent().AddChild(_partner);
        _partner.DebugLaunch(me.GlobalPosition + new Vector3(1.4f, 0.3f, 0f), Vector3.Zero);
        Svc.Pair(_partner, Pal);
        me.WalkControls = () => (_myWish, false);
        Expect(await Until(() => _partner.IsOnFloor(), 10), "the partner stands");

        // the fixture has no ice: a glacier box round the player
        TogetherService.GlacierBox = (me.Global.E, me.Global.N, 150);
        var found = Cells(TogetherService.GlacierBox.Value, 4);
        if (found.Count < 4) { Expect(false, $"four crevasse cells in the glacier box ({found.Count})"); return; }
        var origin = me.Origin!;
        float health = me.Health;
        uint mask = me.CollisionMask;
        Expect((mask & 1u) != 0 && (mask & FootPlayer.CrevasseLayer) == 0, "on the ground: the terrain layer in the mask, the slot's out");

        // 1. the setting off: the bridge holds
        GameSettings.Current.Crevasses = false;
        int broken = Svc.CrevassesBroken;
        await Cross(me, found[0]);
        Expect(Svc.CrevassesBroken == broken && !Svc.InSlot && !me.InCrevasse, "Settings > Crevasses off: a hidden strip is crossed and nothing breaks");
        GameSettings.Current.Crevasses = true;

        // 2. unroped: the fall, the slot, the climb out
        Svc.ServeUnlink(AdventureService.LocalPeer, "the check");
        await Stand(me, found[1]);
        float surface = me.GlobalPosition.Y;
        await CrossFrom(me, found[1]);
        Expect(Svc.CrevassesBroken == broken + 1 && Svc.InSlot && me.InCrevasse, $"unroped, the bridge breaks and the slot opens ({Svc.CrevassesBroken - broken})");
        var slot = Svc.Slot;
        Expect(slot?.GetNodeOrNull<StaticBody3D>("Walls") is { } walls && walls.CollisionLayer == FootPlayer.CrevasseLayer, "the slot has its own collision, on its own layer");
        Expect((me.CollisionMask & 1u) == 0 && (me.CollisionMask & FootPlayer.CrevasseLayer) != 0, "the terrain layer is out of the body's mask, the slot's in");
        Expect(await Until(() => me.IsOnFloor() && me.GlobalPosition.Y < surface - 8f, 8), $"it falls 10 m and lands on the ice floor ({me.GlobalPosition.Y - surface:F1} m)");
        await Seconds(1.0);
        float floorY = me.GlobalPosition.Y;
        Expect(floorY > surface - 10.8f && floorY < surface - 9f, FormattableString.Invariant($"the slot's floor holds it ({floorY - surface:F2} m, not through the world)"));
        Expect(me.PoseKind == FootPlayer.PoseCrevasse, $"the replicated pose says crevasse ({me.PoseKind})");
        Expect(Mathf.IsEqualApprox(me.Health, health), $"no fall damage ({health:F0} -> {me.Health:F0})");
        // climb the wall out: the wish into the wall (across the strip)
        me.DebugSetStamina(me.StaminaMeter.Max);
        _myWish = _across;
        Expect(await Until(() => me.IsClimbing, 4), $"pushing into the ice wall grabs it ({me.ClimbRefused})");
        Expect(await Until(() => me.PoseKind == FootPlayer.PoseCrevasse && me.GlobalPosition.Y > floorY + 2f, 15), "climbing in the crevasse pose");
        Expect(await Until(() => !Svc.InSlot, 40), $"it climbs out ({me.GlobalPosition.Y - surface:F1} m, climbing {me.IsClimbing}, {me.ClimbRefused})");
        _myWish = Vector3.Zero;
        Expect(!me.InCrevasse && (me.CollisionMask & 1u) != 0 && (me.CollisionMask & FootPlayer.CrevasseLayer) == 0, "out: the terrain layer is back, the slot's gone");
        await Seconds(2.0);
        float ground = me.Terrain!.TryGetHeight(me.GlobalPosition, out float g0) ? g0 : surface;
        Expect(me.GlobalPosition.Y > ground - 0.2f && me.GlobalPosition.Y < ground + 5f, FormattableString.Invariant($"standing on the ground again, not falling through ({me.GlobalPosition.Y - ground:F2} m over it)"));
        Expect(Svc.CrevassesOut >= 1, "counted as out");

        // 3. stuck: no climbing, set on the lip after the time
        await Stand(me, found[2]);
        TogetherService.RescueAfter = 4;
        int rescued = Svc.CrevassesRescued;
        surface = me.GlobalPosition.Y;
        await CrossFrom(me, found[2]);
        _myWish = Vector3.Zero;
        Expect(Svc.InSlot, "a third crevasse opens");
        Expect(await Until(() => !Svc.InSlot, 12), "a body that cannot climb is set on the lip");
        Expect(Svc.CrevassesRescued == rescued + 1 && !me.InCrevasse, "counted as rescued, the ground solid again");
        await Seconds(1.5);
        Expect(me.GlobalPosition.Y > surface - 1f, FormattableString.Invariant($"on the snow (not under it): {me.GlobalPosition.Y - surface:F2} m"));
        TogetherService.RescueAfter = Crevasses.RescueSeconds;

        // 4. roped to a standing partner: the rope holds the fall, the partner hauls
        var c = found[3];
        await Stand(me, c);
        surface = me.GlobalPosition.Y;
        _partner.DebugLaunch(origin.ToWorld(c.E - Math.Cos(c.Angle) * 5 - Math.Sin(c.Angle) * 3, c.N - Math.Sin(c.Angle) * 5 + Math.Cos(c.Angle) * 3, me.Global.Alt) + Vector3.Up * 0.5f, Vector3.Zero);
        await Seconds(0.8);
        string? tied = Svc.ServeLink(AdventureService.LocalPeer, Pal, LinkMode.Rope);
        Expect(tied == null, $"roped up on the glacier ({tied})");
        int caught = Svc.CrevassesCaught;
        await CrossFrom(me, c);
        _myWish = Vector3.Zero;
        Expect(Svc.CrevassesCaught == caught + 1 && Svc.InSlot, "roped, the bridge breaks under the owner and the partner holds the fall");
        Expect(await Until(() => Svc.Hanging(AdventureService.LocalPeer), 3), "the rope takes the weight: hanging");
        await Seconds(1.5);
        float depth = surface - me.GlobalPosition.Y;
        Expect(depth > 1.5f && depth < 4.5f && !me.IsOnFloor(), FormattableString.Invariant($"it hangs {depth:F1} m under the lip, not on the ice floor"));
        Expect(me.PoseKind == FootPlayer.PoseCrevasse, "hanging in the crevasse pose");
        Svc.ServeHaul(Pal, true);
        Expect(await Until(() => !Svc.InSlot, 25), $"hauled out ({surface - me.GlobalPosition.Y:F1} m under the lip)");
        Svc.ServeHaul(Pal, false);
        await Seconds(2.0);
        float ground4 = me.Terrain!.TryGetHeight(me.GlobalPosition, out float g4) ? g4 : surface;
        Expect(me.GlobalPosition.Y > ground4 - 0.3f && !me.InCrevasse, FormattableString.Invariant($"back on the ground ({me.GlobalPosition.Y - ground4:F2} m over it)"));
        Expect(Svc.CompanyOf(AdventureService.LocalPeer) != StaminaCompany.None || !Svc.Hanging(AdventureService.LocalPeer), "the rope hangs nobody now");
        Svc.ServeUnlink(AdventureService.LocalPeer, "the check");
        await Seconds(0.3);
        _partner.QueueFree();
    }

    /// <summary>Crevasse cells inside the box (a 30 m apart from each other), nearest to the middle first.</summary>
    private static List<(double E, double N, double Angle)> Cells((double E, double N, double R) box, int want)
    {
        var found = new List<(double E, double N, double Angle)>();
        for (double e = box.E - box.R * 0.7; e < box.E + box.R * 0.7 && found.Count < want; e += Crevasses.Cell)
            for (double n = box.N - box.R * 0.7; n < box.N + box.R * 0.7 && found.Count < want; n += Crevasses.Cell)
                if (Crevasses.InCell(e, n) is { } c && found.All(f => Math.Abs(f.E - c.E) + Math.Abs(f.N - c.N) > 40)) found.Add(c);
        return found;
    }

    private Vector3 _across;

    /// <summary>The body 3 m to the side of the strip's middle and the world direction straight across it (set in <see cref="_across"/>).</summary>
    private async Task Stand(FootPlayer me, (double E, double N, double Angle) c)
    {
        _myWish = Vector3.Zero;
        var origin = me.Origin!;
        double se = -Math.Sin(c.Angle), sn = Math.Cos(c.Angle);
        var from = origin.ToWorld(c.E - se * 3, c.N - sn * 3, me.Global.Alt) + Vector3.Up * 0.5f;
        var to = origin.ToWorld(c.E + se * 3, c.N + sn * 3, me.Global.Alt) + Vector3.Up * 0.5f;
        _across = ((to - from) with { Y = 0 }).Normalized();
        me.DebugLaunch(from, Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.6);
    }

    /// <summary>Walks across the strip from where <see cref="Stand"/> put the body, until it breaks or 3 s have passed; then stops.</summary>
    private async Task CrossFrom(FootPlayer me, (double E, double N, double Angle) c)
    {
        _myWish = _across;
        await Until(() => Svc.InSlot, 3);
        _myWish = Vector3.Zero;
        await Seconds(0.3);
    }

    private async Task Cross(FootPlayer me, (double E, double N, double Angle) c)
    {
        await Stand(me, c);
        _myWish = _across;
        await Seconds(3);
        _myWish = Vector3.Zero;
        await Seconds(0.3);
    }
}
