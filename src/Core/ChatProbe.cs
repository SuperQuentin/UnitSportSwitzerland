using Godot;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// The scaffolding the multiplayer probes share (<c>--plantcheck</c>, <c>--gunshot</c>, <c>--placedcheck</c>,
/// <c>--useanim</c>, <c>--lootsynccheck</c>, <c>--locksynccheck</c>, <c>--econcheck</c>, <c>--photocheck</c>,
/// <c>--bankcheck</c>, <c>--brprobe</c>, <c>--pvpcheck</c>, <c>--birdnetcheck</c>):
/// clients coordinate through chat lines (<c>"&lt;prefix&gt; &lt;role&gt; &lt;what&gt;"</c>), wait on conditions,
/// count failed expectations and log as <c>[tag role]</c>. The scripts in <c>tools/</c> grep those lines.
/// </summary>
public abstract partial class ChatProbe : Node
{
    private readonly string _tag, _prefix, _shots;
    protected readonly ItemController _items;
    protected readonly List<string> _heard = new();
    /// <summary>A, B, C… from the command line; empty for a probe that runs alone.</summary>
    protected string _role = "";
    protected int _failures;

    /// <param name="tag">The log tag: <c>[tag role]</c>.</param>
    /// <param name="prefix">The chat prefix of <see cref="Say"/> and <see cref="Heard"/>.</param>
    /// <param name="shots">The file prefix of <see cref="Shot"/> in <c>test_output/</c>.</param>
    protected ChatProbe(ItemController items, string tag, string prefix = "", string shots = "")
    {
        _items = items;
        _tag = tag;
        _prefix = prefix;
        _shots = shots;
    }

    /// <summary>The upper-cased word after <paramref name="flag"/>, or null when the flag is absent.</summary>
    public static string? RoleArg(string flag) => CmdArgs.Value(flag)?.ToUpperInvariant();

    protected string Log => _role.Length > 0 ? $"[{_tag} {_role}]" : $"[{_tag}]";
    /// <summary>Echo each <see cref="Say"/> to the log (some scripts wait for the echo).</summary>
    protected virtual bool EchoSay => true;
    /// <summary>The dash of the failure line, as each probe always wrote it.</summary>
    protected virtual string Dash => "—";

    protected ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    /// <summary>Our player: by default the one the camera rides.</summary>
    protected virtual FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    /// <summary>Waits for chat, a connection and our player on the ground (plus <paramref name="also"/>), then listens to chat. False after a <see cref="Fail"/>.</summary>
    protected async Task<bool> Joined(double seconds, Func<bool>? also = null)
    {
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor() && (also?.Invoke() ?? true), seconds))
        {
            Fail("no player on the ground");
            return false;
        }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        return true;
    }

    /// <summary>
    /// The RESULT line, then quit: once every peer this probe heard from has said it finished too, and after
    /// <paramref name="linger"/> seconds at most (#992). The linger keeps us up while a peer still checks our body
    /// or waits for our last line; a fixed 1-2 s of it ended every net check. A probe with no chat prefix, or
    /// one that never heard a peer, lingers as before.
    /// </summary>
    protected async Task Finish(double linger)
    {
        GD.Print(_failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({_failures})");
        if (linger > 0)
        {
            if (_prefix.Length > 0 && Chat != null && _role.Length > 0 && Peers().Count > 0)
            {
                Say(FinishedWord);
                await Until(() => Peers().All(p => HeardLine(p, FinishedWord)), linger);
                await Until(() => false, FlushSeconds);   // our own last line and RPCs out (frames, not /speed)
            }
            else await Seconds(linger);
        }
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private const string FinishedWord = "finished";
    /// <summary>Loopback: a dozen frames carry the last reliable packets out before the process quits.</summary>
    private const double FlushSeconds = 0.25;

    /// <summary>The roles of the peers we heard (<c>"&lt;prefix&gt; &lt;role&gt; …"</c>), ours left out.</summary>
    private HashSet<string> Peers()
    {
        var roles = new HashSet<string>();
        string mark = _prefix + " ";
        foreach (string line in _heard)
        {
            int at = line.IndexOf(mark, StringComparison.Ordinal);
            if (at < 0) continue;
            int start = at + mark.Length, end = line.IndexOf(' ', start);
            if (end <= start) continue;
            string role = line[start..end];
            if (role != _role) roles.Add(role);
        }
        return roles;
    }

    private bool HeardLine(string role, string what) => _heard.Any(l => l.Contains($"{_prefix} {role} {what}"));

    /// <summary>
    /// Admin through <c>/login</c> (the server's <c>--admin-password</c>): waits for the grant rather than a fixed
    /// pause (#992). False after a <see cref="Fail"/> when none came within <paramref name="seconds"/>.
    /// </summary>
    protected async Task<bool> LogIn(string password, double seconds = 10)
    {
        if (Permissions.IsAdmin) return true;
        Chat!.Send($"/login {password}");
        if (await Until(() => Permissions.IsAdmin, seconds)) return true;
        Fail("not logged in as admin");
        return false;
    }

    protected void Say(string what)
    {
        if (EchoSay) GD.Print($"{Log} say {what}");
        Chat?.Send($"{_prefix} {_role} {what}");
    }

    protected Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"{_prefix} {role} {what}")), seconds);

    /// <summary>What <paramref name="role"/> said from <paramref name="what"/> on (a line carrying a value), or null.</summary>
    protected async Task<string?> HeardStarting(string role, string what, double seconds)
    {
        string key = $"{_prefix} {role} ";
        string? found = null;
        await Until(() => (found = _heard.FirstOrDefault(l => l.Contains(key + what))) != null, seconds);
        return found?[(found.IndexOf(key, StringComparison.Ordinal) + key.Length)..];
    }

    protected async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    /// <summary>
    /// Until <paramref name="condition"/> has held for <paramref name="hold"/> s running, physics frame
    /// by frame, at most <paramref name="seconds"/>: a state that reads true from construction (an
    /// A320's "on the ground", put a metre up and dropping onto its wheels) passes a plain
    /// <see cref="Until"/> on the first frame, before the settling it stood for (#1080).
    /// </summary>
    protected async Task<bool> UntilHeld(Func<bool> condition, double hold, double seconds)
    {
        double end = GameClock.Now + seconds, since = -1;
        while (true)
        {
            if (!condition()) since = -1;
            else if (since < 0) since = GameClock.Now;
            if (since >= 0 && GameClock.Now - since >= hold) return true;
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
    }

    protected async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    /// <summary>
    /// Walks <paramref name="me"/> along <paramref name="way"/> (world, flattened) until <paramref name="there"/>, at
    /// most <paramref name="seconds"/>, and <paramref name="past"/> s on, through the walk intent (<see
    /// cref="FootPlayer.WalkControls"/>, a bot's seam). Never a held <c>Input.ActionPress</c>: Godot lets go of every
    /// held action when the window loses focus (another test window opening, a click elsewhere), and a player walking
    /// in through a door stopped on the doorstep now and then (#1401). Says where it stopped when it never got there.
    /// </summary>
    protected Task<bool> WalkUntil(FootPlayer me, Vector3 way, Func<bool> there, double seconds, double past = 0.4)
    {
        var wish = (way with { Y = 0 }).Normalized();
        return WalkUntil(me, () => wish, there, seconds, past);
    }

    /// <summary>
    /// In through the doorway whose outside frame is <paramref name="doorway"/> (<c>DoorLink.Outside</c>: the sill's
    /// middle, Z out of the building): aimed at the doorway's middle line a little ahead, so a player off that line is drawn back between the jambs.
    /// The building's shell has no hole, it lets a walker through only with their feet inside the doorway's width
    /// (<c>DoorLink.InOutsideDoorway</c>), and a player set down beside another on the same doorstep was eased half a
    /// metre aside and walked into the wall next to the door (#1401).
    /// </summary>
    protected Task<bool> WalkInThrough(FootPlayer me, Transform3D doorway, Func<bool> there, double seconds = 8)
    {
        var door = doorway.Origin;
        var o = (doorway.Basis.Z with { Y = 0 }).Normalized();
        return WalkUntil(me, () =>
        {
            // over the sill the body is in the interior's frame, 3 km down and turned: walk on the way it faces,
            // as a held key would, or the aim at the street's doorway took it back out (#1401)
            if (me.Indoors) return (-me.Camera.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
            float along = (me.GlobalPosition - door).Dot(o);
            var aim = door + o * (along - DoorAimAhead) - me.GlobalPosition;
            return (aim with { Y = 0 }).Normalized();
        }, there, seconds, 0.4);
    }

    /// <summary>
    /// How long a door must have stood open before a probe walks at it, s: two clients reaching for one door
    /// at once, the second E shuts what the first opened a round trip later, and a walker who set off on the
    /// first frame it read open met the shell (no hole in it) and climbed the facade (#1401).
    /// </summary>
    protected const double DoorSettle = 1.0;

    /// <summary>How far ahead on the doorway's middle line <see cref="WalkInThrough"/> aims, m: a player half a metre off it is back within a metre.</summary>
    private const float DoorAimAhead = 0.6f;

    private async Task<bool> WalkUntil(FootPlayer me, Func<Vector3> wish, Func<bool> there, double seconds, double past)
    {
        var from = me.GlobalPosition;
        me.WalkControls = () => (wish(), false);
        bool ok = await Until(there, seconds);
        await Seconds(past);
        me.WalkControls = null;
        if (!ok)
        {
            var near = PlayerSnapshot.Of(GetTree()).Where(s => s.Player != me && s.Pos.DistanceTo(me.GlobalPosition) < 3f)
                .Select(s => $"{s.Player.Name} {s.Pos - me.GlobalPosition}");
            GD.Print($"{Log} walked {(me.GlobalPosition - from).Length():F2} m of the way in {seconds} s, from {from} to {me.GlobalPosition}, "
                + $"on the floor {me.IsOnFloor()}, velocity {me.Velocity}, players within 3 m: [{string.Join("; ", near)}]");
        }
        return ok;
    }

    /// <summary>Logged, for a check whose window loses focus: Godot lets go of every held action then (#1401).</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut) GD.Print($"{Log} the window lost focus");
    }

    /// <summary>
    /// <c>--checkspeed n</c> (#931): the whole simulation n times faster through <c>/speed</c>, for a net check whose
    /// time is simulated waiting (bots falling, fight rounds). Logs in with <paramref name="password"/> (the server's
    /// <c>--admin-password</c>) unless already admin, and waits until the scale is on this peer; every peer follows,
    /// so one client asks. <see cref="Seconds"/> shrinks with it, <see cref="Until"/>'s deadlines (physics frames)
    /// do not. True at once without the flag; false after a <see cref="Fail"/>.
    /// </summary>
    protected async Task<bool> SpeedUp(string password)
    {
        if (CmdArgs.Double("--checkspeed") is not double speed || speed <= 0) return true;
        if (!await LogIn(password)) return false;
        Chat!.Send(FormattableString.Invariant($"/speed {speed}"));
        if (!await Until(() => Math.Abs(SimClock.Scale - speed) < 1e-6, 10)) { Fail($"/speed {speed} never applied"); return false; }
        GD.Print($"{Log} the simulation at {SimClock.Describe(speed)} (--checkspeed)");
        return true;
    }

    /// <summary>
    /// Back to normal speed after <see cref="SpeedUp"/>, before a part a faster simulation would distort: a hit on
    /// a remote body moving n times as far in the network's delay, which stays real time (#931).
    /// </summary>
    protected async Task<bool> SpeedBack()
    {
        if (CmdArgs.Double("--checkspeed") is not double speed || speed <= 0 || speed == SimClock.Normal) return true;
        Chat!.Send("/speed normal");
        if (!await Until(() => Math.Abs(SimClock.Scale - SimClock.Normal) < 1e-6, 10)) { Fail("/speed normal never applied"); return false; }
        GD.Print($"{Log} the simulation back at normal speed");
        return true;
    }

    protected void Expect(bool ok, string what)
    {
        GD.Print($"{Log} {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    /// <summary>Logs the failure and quits with 1. Counted: a <see cref="Finish"/> after it no longer prints "RESULT: ok" last (#1006).</summary>
    protected virtual void Fail(string why)
    {
        _failures++;
        GD.Print($"{Log} RESULT: FAILED {Dash} {why}");
        GetTree().Quit(1);
    }

    /// <summary>Saves the view to <c>test_output/&lt;shots&gt;&lt;name&gt;.png</c>; the absolute path.</summary>
    protected virtual string Shot(string name)
    {
        System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://test_output"));
        string path = ProjectSettings.GlobalizePath($"res://test_output/{_shots}{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        return path;
    }

    protected static float Float(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first slot holding <paramref name="id"/>, or -1.</summary>
    protected int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    protected int CountOf(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    /// <summary>Items in the pack plus cash.</summary>
    protected int PackTotal()
    {
        var inv = _items.Inventory;
        int n = inv.Cash;
        for (int i = 0; i < Inventory.Size; i++) if (!inv[i].IsEmpty) n += inv[i].Count;
        return n;
    }
}
