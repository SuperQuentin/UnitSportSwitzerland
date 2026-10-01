using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Birds;

/// <summary>
/// <c>--birdnetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/birdnetcheck.sh</c>): the shared birds
/// between two real clients and a dedicated server (#143). Both stand at the same place.
/// <list type="bullet">
/// <item>both must be sent birds by the server, and the same ones (same id, same place);</item>
/// <item>A walks up to a resting bird and shoots it through the real item path: the server must kill it, A's
/// journal must score it, B must see the same bird fall, and B's journal must stay as it was;</item>
/// <item>A fires a second shot into the air: the server flushes the birds around it and B must see resting
/// birds take off;</item>
/// <item>an aircraft gun round (<see cref="BirdLife.TracerHit"/>) through a live bird: the server kills it
/// and A's journal scores it.</item>
/// </list>
/// Roles talk through chat lines. Scratch inventory; the journal is the machine's real <c>user://birds.json</c>.
/// </summary>
public partial class BirdNetProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--birdnetcheck");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private string _role = "";
    private int _failures;

    public BirdNetProbe(ItemController items) => _items = items;
    public BirdNetProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;
    private BirdLife? Life => BirdLife.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor() && Life != null && Life.Net != null, 150))
        {
            Fail("no player on the ground");
            GD.Print($"[birdnet {_role}] RESULT: FAILED ({_failures})");
            GetTree().Quit(1);
            return;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        var life = Life!;
        Expect(!life.Authority, "this client is not the authority on the birds");
        Expect(Enumerable.Range(0, BirdCatalog.All.Length).All(i => BirdCatalog.All[i].Index == i), "species index = catalogue position (the wire name)");
        life.Month = 10;   // the same hunting season everywhere

        Expect(await Until(() => life.Birds.Count(b => b.Remote) >= 3, 90), $"the server sent birds ({life.Birds.Count(b => b.Remote)})");
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!, life); else await RunB(Me!, life);

        GD.Print(_failures == 0 ? $"[birdnet {_role}] RESULT: ok" : $"[birdnet {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.0);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private string Ids(BirdLife life, FootPlayer me) => string.Join(";", life.Birds
        .Where(b => b.Remote && b.Id > 0 && b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming)
        .OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).Take(10)
        .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Id}:{b.Node.GlobalPosition.X:F0}:{b.Node.GlobalPosition.Z:F0}")));

    private async Task RunA(FootPlayer me, BirdLife life)
    {
        // B may not be listening yet: repeat until it answers
        bool ready = false;
        for (int i = 0; i < 30 && !ready; i++)
        {
            Say("ids " + Ids(life, me));
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never joined"); return; }

        int gun = Enumerable.Range(0, Inventory.Size).FirstOrDefault(i => _items.Inventory[i].Id == ItemId.Shotgun, -1);
        if (gun < 0) { Fail("no shotgun"); return; }
        if (!Enumerable.Range(0, Inventory.Size).Any(i => _items.Inventory[i].Id == ItemId.Shells)) _items.Inventory.Add(ItemId.Shells, 10);
        var home = me.GlobalPosition;

        // walk up to resting birds until one is shot: trees and slopes may be in the way of a try
        Bird? killed = null;
        int before = life.Journal.Score;
        var tried = new HashSet<int>();
        for (int attempt = 0; attempt < 6 && killed == null; attempt++)
        {
            var target = life.Birds.Where(b => b.Remote && b.Id > 0 && !tried.Contains(b.Id)
                    && b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming
                    && b.Node.GlobalPosition.DistanceTo(home) < 120f
                    && 5f + 18f * Mathf.Sqrt(b.Species.Length) + 4f < 28f)
                .OrderBy(b => b.Node.GlobalPosition.DistanceTo(home)).FirstOrDefault();
            if (target == null) break;
            tried.Add(target.Id);
            float stand = 5f + 18f * Mathf.Sqrt(target.Species.Length) + 4f;
            var away = (home - target.Node.GlobalPosition) with { Y = 0 };
            var spot = target.Node.GlobalPosition + (away.LengthSquared() < 1f ? Vector3.Back : away.Normalized()) * stand;
            spot.Y = life.Ground(spot) + 1.5f;
            me.LeaveInterior(spot, 0f);
            me.Velocity = Vector3.Zero;
            await Until(() => me.IsOnFloor(), 5);
            await Seconds(1.0);   // the server learns where we are; the bird has to still be there
            if (target.State is not (Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming)) continue;
            // in a town a house may stand between: a shot only at a bird the eye can see (a miss
            // would flush every bird around and leave nothing to try next)
            var sight = me.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                me.EyePosition, target.Centre + Vector3.Up * 0.1f, uint.MaxValue, new Godot.Collections.Array<Rid> { me.GetRid() }));
            if (sight.Count > 0) { GD.Print($"[birdnet A] #{target.Id} not in sight from {stand:F0} m, next"); continue; }
            await Aim(me, target.Centre);
            GD.Print($"[birdnet A] aimed: {-me.Camera.GlobalTransform.Basis.Z.Dot((target.Centre - me.EyePosition).Normalized()):F4}");
            int shells = Shells();
            GD.Print($"[birdnet A] shooting #{target.Id} {target.Species.Name} at {target.Node.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m");
            _items.UseSlot(me, gun);
            Expect(Shells() == shells - 1, "the item path spent a shell");
            if (await Until(() => life.Journal.Score != before, 4)) killed = target;
            await Seconds(1.0);
        }
        Expect(killed != null, "the shot was reported, the server killed the bird and A's journal scored it");
        if (killed != null)
        {
            Expect(await Until(() => killed.State is Bird.Mode.Falling or Bird.Mode.Dead, 3), $"the bird fell on A's screen ({killed.State})");
            Expect(await Until(() => killed.State == Bird.Mode.Dead, 8), "...and lies dead");
            Say($"shot {killed.Id} {killed.Species.Name.Replace(' ', '_')}");
        }
        else Say("shot 0 none");

        // a shot into the air: everything resting within 150 m takes off, for B too. The first shot
        // flushed everything around: B waits for newly spawned birds to settle near A first.
        await Seconds(1.0);
        Say(string.Create(CultureInfo.InvariantCulture, $"at {me.GlobalPosition.X:F0} {me.GlobalPosition.Z:F0}"));
        if (!await Heard("B", "flushready", 70)) { Fail("B not ready for the flush"); return; }
        me.LookPitch = 1.2f;
        await Seconds(0.3);
        _items.UseSlot(me, gun);
        Say("fired");
        await Heard("B", "done", 30);

        // an aircraft gun round (CombatManager -> TracerHit) through a live bird: reported, killed by
        // the server, scored here
        var bird = life.Birds.Where(x => x.Remote && x.State is not (Bird.Mode.Falling or Bird.Mode.Dead))
            .OrderBy(x => x.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        Expect(bird != null, "a live bird for the gun round");
        if (bird != null)
        {
            int score = life.Journal.Score;
            var dir = (bird.Centre - me.EyePosition).Normalized();
            var hit = life.TracerHit(me.EyePosition, bird.Centre + dir * 5f, mine: true);
            Expect(hit == bird, $"the round passes through #{bird.Id} {bird.Species.Name}");
            Expect(await Until(() => life.Journal.Score != score, 4) && bird.State is Bird.Mode.Falling or Bird.Mode.Dead,
                $"the server killed it and A's journal scored it ({bird.State})");
        }
    }

    private async Task RunB(FootPlayer me, BirdLife life)
    {
        // A may not be listening yet (its ground loads at its own pace): say it until A answers
        bool heard = false;
        for (int i = 0; i < 30 && !heard; i++)
        {
            Say("ready");
            heard = await Heard("A", "ids", 3);
        }
        Expect(heard, "heard A's birds");
        var line = _heard.LastOrDefault(l => l.Contains("PN A ids")) ?? "";
        var theirs = line[(line.IndexOf("ids", StringComparison.Ordinal) + 3)..].Trim().Split(';', StringSplitOptions.RemoveEmptyEntries);
        int shared = 0;
        foreach (var t in theirs)
        {
            var f = t.Split(':');
            if (f.Length < 3 || !int.TryParse(f[0], out int id)) continue;
            var mine = life.Birds.FirstOrDefault(b => b.Id == id);
            if (mine != null && Mathf.Abs(mine.Node.GlobalPosition.X - float.Parse(f[1], CultureInfo.InvariantCulture)) < 8f
                && Mathf.Abs(mine.Node.GlobalPosition.Z - float.Parse(f[2], CultureInfo.InvariantCulture)) < 8f) shared++;
        }
        Expect(shared >= 1, $"the same birds in the same places as A: {shared} of {theirs.Length} (ids {string.Join(" ", theirs.Select(t => t.Split(':')[0]))})");

        int score = life.Journal.Score;
        Expect(await Heard("A", "shot", 90), "A shot");
        var shot = _heard.LastOrDefault(l => l.Contains("PN A shot")) ?? "";
        var parts = shot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int dead = parts.Length > 4 && int.TryParse(parts[^2], out int d) ? d : 0;
        Expect(dead > 0, $"A bagged bird #{dead}");
        if (dead > 0)
        {
            var bird = life.Birds.FirstOrDefault(b => b.Id == dead);
            Expect(bird == null || await Until(() => bird.State is Bird.Mode.Falling or Bird.Mode.Dead || bird.Gone, 6),
                $"the same bird is down on B's screen too ({bird?.State.ToString() ?? "already gone"})");
        }
        Expect(life.Journal.Score == score, "the kill is not in B's journal");

        // flush: resting birds near A (new ones, the first shot flushed the rest), then A's air shot
        Expect(await Heard("A", "at", 30), "A says where it stands");
        var at = (_heard.LastOrDefault(l => l.Contains("PN A at")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shooter = at.Length >= 2 ? new Vector3(float.Parse(at[^2], CultureInfo.InvariantCulture), 0, float.Parse(at[^1], CultureInfo.InvariantCulture)) : me.GlobalPosition;
        HashSet<int> Resting() => life.Birds.Where(b => b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming
            && new Vector2(b.Node.GlobalPosition.X - shooter.X, b.Node.GlobalPosition.Z - shooter.Z).Length() < 130f).Select(b => b.Id).ToHashSet();
        await Until(() => Resting().Count >= 2, 50);
        var resting = Resting();
        GD.Print($"[birdnet B] {resting.Count} resting birds within 130 m of A before the flush");
        Expect(resting.Count > 0, "resting birds near A to flush");
        Say("flushready");
        Expect(await Heard("A", "fired", 40), "A fired into the air");
        Expect(resting.Count > 0 && await Until(() => life.Birds.Count(b => resting.Contains(b.Id) && b.State is Bird.Mode.Flying) * 2 >= resting.Count, 6),
            $"resting birds took off on B's screen after A's shot ({life.Birds.Count(b => resting.Contains(b.Id) && b.State is Bird.Mode.Flying)} of {resting.Count})");
        Say("done");
    }

    /// <summary>Turns the view toward a point (yaw and pitch, either sign convention) until the camera looks at it.</summary>
    private async Task Aim(FootPlayer me, Vector3 at)
    {
        var d = (at - me.EyePosition).Normalized();
        float pitch = Mathf.Asin(d.Y);
        foreach (float yaw in new[] { Mathf.Atan2(-d.X, -d.Z), Mathf.Atan2(d.X, d.Z) })
        {
            me.LookYaw = yaw;
            me.LookPitch = pitch;
            await Seconds(0.25);
            if (-me.Camera.GlobalTransform.Basis.Z.Dot(d) > 0.99f) return;
        }
    }

    private int Shells() => Enumerable.Range(0, Inventory.Size).Where(i => _items.Inventory[i].Id == ItemId.Shells).Sum(i => _items.Inventory[i].Count);

    private void Say(string what)
    {
        GD.Print($"[birdnet {_role}] say {what}");
        Chat?.Send($"PN {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"PN {role} {what}")), seconds);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[birdnet {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[birdnet {_role}] FAIL {why}");
        _failures++;
    }
}
