using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--cinemanet A|B|C</c> with <c>--connect</c> on the generated world (#1238): a cinema's booth
/// (<see cref="CinemaBooths"/>) over the network. The booth's rule is the server's — a peer works it only
/// from inside the building — so any building tests it, and the generated world has a village of houses
/// where it has no cinema. A walks into a house and asks for the lights off and the cat on; the server
/// grants it. B, outside, must see that state arrive, then asks for the opposite and must be refused (it
/// is not inside). C joins late and must find the state from the table the server sends a newcomer.
/// Coordinated through chat lines; A repeats its line until both answer.
/// <para>
/// Then the hall's video (#1298), with <c>--cinemavideo &lt;link&gt;</c>: A plays it; B, outside, is refused a
/// link of its own; every peer must hold the same link and T0, and A and B decode it
/// (<see cref="CinemaVideoStream"/>, headless too) and prints, at every 2 s of the shared clock, where the
/// hall is and which moment of the video its frame shows. Each must be within two frames of the clock.
/// </para>
/// </summary>
public partial class CinemaNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--cinemanet");

    private readonly WorldOrigin _origin;

    public CinemaNetProbe(ItemController items, WorldOrigin origin) : base(items, "cinema", "CN") => _origin = origin;

    public CinemaNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    /// <summary>The video A plays, or null for no video phase.</summary>
    private static readonly string? VideoLink = CmdArgs.Value("--cinemavideo");

    /// <summary>How long each peer watches it, s.</summary>
    private const double WatchSeconds = 16;

    private static readonly int Show = CinemaBooths.Pack(CinemaBooths.Lights.Off, true);

    /// <summary>What A sets on hall 0: the show, or, where the hall starts mid-film (#1279), the lights dimmed and the screen off.</summary>
    private static int Target(string plan) =>
        CinemaBooths.Unpack(CinemaBooths.Default(plan, 0)).Screen ? CinemaBooths.Pack(CinemaBooths.Lights.Dim, false) : Show;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(180)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => CinemaBooths.Instance != null, 10)) { Fail("no World/CinemaBooths"); return; }
        var booths = CinemaBooths.Instance!;

        // to the village, and the house door nearest it: the same on every peer
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? found = null;
        if (!await Until(() => me.IsOnFloor() && !me.KnockedOut
                && (found = DoorIndex.NearestOfKind(me.GlobalPosition, 200f, BuildingKind.House, also: d => d.Key.Slot == 0)) != null, 90))
        { Fail("no house door near the spot"); return; }
        var door = found!.Value;
        GD.Print($"{Log} the house door {door.Key} at {door.World}");

        if (_role == "A")
        {
            if (!await WalkIn(me, interiors, door)) return;
            string plan = interiors.Current!.Key;
            Expect(booths.StateOf(plan, 0) == CinemaBooths.Unpack(CinemaBooths.Default(plan, 0)), $"the hall starts as its seed says ({booths.StateOf(plan, 0)})");
            booths.Ask(plan, 0, Target(plan));
            Expect(await Until(() => booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), 10),
                $"the server granted A, inside, {CinemaBooths.Unpack(Target(plan))}");
            // said over and over: C has not joined yet
            bool b = false, c = false;
            for (int t = 0; t < 150 && !(b && c); t++)
            {
                Say($"set {plan}");
                if (t == 0) GD.Print($"{Log} set {plan}");
                await Until(() => _heard.Any(l => l.Contains("CN B done")) && _heard.Any(l => l.Contains("CN C done")), 2);
                b |= _heard.Any(l => l.Contains("CN B done"));
                c |= _heard.Any(l => l.Contains("CN C done"));
            }
            Expect(b, "B saw it, and was refused");
            Expect(c, "C, joining late, found it");
            Expect(booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), "B's refused ask changed nothing for A");
            if (VideoLink != null)
            {
                booths.AskVideo(plan, 0, VideoLink);
                Expect(await Until(() => booths.VideoOf(plan, 0).Link == VideoLink, 10), "the server granted A's video");
                var (_, t0) = booths.VideoOf(plan, 0);
                GD.Print($"{Log} video {t0:F3} {VideoLink}");
                b = c = false;
                for (int t = 0; t < 40 && !(b && c); t++)
                {
                    Say($"video {t0.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
                    await Until(() => _heard.Any(l => l.Contains("CN B watching")) && _heard.Any(l => l.Contains("CN C watching")), 2);
                    b |= _heard.Any(l => l.Contains("CN B watching"));
                    c |= _heard.Any(l => l.Contains("CN C watching"));
                }
                Expect(b && c, "B and C got the video");
                await Watch(VideoLink, t0);
                Expect(booths.VideoOf(plan, 0).Link == VideoLink, "B's refused link changed nothing for A");
            }
        }
        else
        {
            if (!await Heard("A", "set", 240)) { Fail("A never set anything"); return; }
            string line = _heard.Last(l => l.Contains("CN A set"));
            string plan = line[(line.IndexOf("CN A set", StringComparison.Ordinal) + "CN A set".Length)..].Trim();
            Expect(await Until(() => booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), 10),
                $"{_role} sees {plan}'s hall as A set it, {CinemaBooths.Unpack(Target(plan))}");
            if (_role == "B")
            {
                // outside: the server must refuse, and nothing changes
                Expect(!me.Indoors, "B is outside");
                booths.Ask(plan, 0, CinemaBooths.Default(plan, 0));
                await Seconds(3);
                Expect(booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), "the server refused B, who is not inside");
            }
            Say("done");
            if (VideoLink != null)
            {
                if (!await Heard("A", "video", 120)) { Fail("A never played a video"); return; }
                string said = _heard.Last(l => l.Contains("CN A video"));
                double t0Said = double.Parse(said[(said.IndexOf("CN A video", StringComparison.Ordinal) + 10)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                Expect(await Until(() => booths.VideoOf(plan, 0).Link == VideoLink, 10), $"{_role} holds A's link");
                var (_, t0) = booths.VideoOf(plan, 0);
                Expect(Math.Abs(t0 - t0Said) < 0.002, $"{_role} holds A's T0 ({t0:F3} against {t0Said:F3})");
                if (_role == "B") booths.AskVideo(plan, 0, "https://example.org/b.mp4");
                Say("watching");
                // C only checks what it was sent: two decoding peers prove the clock, a third ffmpeg pair costs the RAM floor
                if (_role == "B") await Watch(VideoLink, t0);
                else await Seconds(WatchSeconds);
                Expect(booths.VideoOf(plan, 0).Link == VideoLink, $"{_role}: the server refused B's link (B is outside)");
            }
        }
        await Finish(6);
    }

    /// <summary>
    /// Decodes the hall's video here for <see cref="WatchSeconds"/> and prints, at each 2 s of the shared clock, the
    /// hall's position and the moment the shown frame is of; expects them within two frames once it plays.
    /// </summary>
    private async Task Watch(string link, double t0)
    {
        if (!CinemaVideoStream.CanDecode) { GD.Print($"{Log} cannot decode here: no watch"); return; }
        using var stream = new CinemaVideoStream(link, t0);
        double until = Net.ClockSync.ServerNow + WatchSeconds;
        long mark = -1;
        int marks = 0, close = 0;
        double worst = 0;
        while (Net.ClockSync.ServerNow < until && !stream.Failed && !stream.Ended)
        {
            double pos = CinemaVideo.PositionAt(Net.ClockSync.ServerNow, t0);
            stream.Pump(pos);
            long m = (long)Math.Floor(Net.ClockSync.ServerNow / 2);
            if (m != mark)
            {
                mark = m;
                if (stream.CurrentIndex >= 0)
                {
                    double off = pos - stream.CurrentTime;
                    marks++;
                    if (off >= -0.001 && off < 2.0 / CinemaVideo.Fps) close++;
                    worst = Math.Max(worst, Math.Abs(off));
                    GD.Print($"{Log} mark {m} at {pos:F3} s shows {stream.CurrentTime:F3} s (off {off * 1000:F0} ms, restarts {stream.Restarts})");
                }
                else GD.Print($"{Log} mark {m} at {pos:F3} s: {stream.Status}");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Expect(!stream.Failed, $"{_role} plays the video ({stream.Status})");
        Expect(marks >= 3 && close == marks, $"{_role} on the shared clock at {close}/{marks} marks (worst {worst * 1000:F0} ms)");
    }

    /// <summary>From just outside the door: open it unless it stands open, and walk through (as <see cref="LiftSyncProbe"/>).</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
        door = DoorIndex.Find(door.Key) ?? door;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 4 && !open; attempt++)
        {
            if (!interiors.IsOpen(doorKey)) me.TryInteract();
            open = await Until(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }
}
