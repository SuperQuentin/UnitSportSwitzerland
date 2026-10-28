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
/// link of its own; every peer must hold the same link and T0, and A and B play it the way a player in the hall
/// would (<see cref="CinemaBooths.ProbeWatch"/>, headless too) and print, at every 2 s of the shared clock, where the
/// hall is and which moment of the video their frame shows. Each must be within two frames of the clock. The script
/// starts A with <c>--cinemanodecode</c> (a phone's way): A's frames come through the server's relay (#1298 phase 2),
/// B decodes the link itself, and both must be on the clock.
/// </para>
/// <para>
/// Then a live share (#1298 phase 3, with the video): A shares a test pattern with a tone (<see cref="CaptureKind.Test"/>)
/// on the hall; B walks into the house; both watch it through the server (A its own share, as a streamer sees it) and
/// must be on the clock with the sound due; A stops it and B sees the hall's link go. Then a video file from A's own disk
/// (phase 4, <see cref="CinemaFileSource"/>): the test film again, streamed from A through the server, watched by both.
/// </para>
/// <para>
/// And what the house's halls are showing by themselves (<c>CinemaBooths.Showings</c>): A says every hall's film and loop
/// start but hall 1's (which the check works); B and C, C joining late, must draw the same with nothing sent.
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

    private static readonly int Display = CinemaVideo.PackDisplay(CinemaVideo.Fit.Cover, Array.FindIndex(CinemaVideo.Ratios, r => r.Name == "16:9"));

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
            Say($"showings {Showings(booths, plan)}");
            Expect(booths.StateOf(plan, 0) == CinemaBooths.Unpack(CinemaBooths.Default(plan, 0)), $"the hall starts as its seed says ({booths.StateOf(plan, 0)})");
            booths.Ask(plan, 0, Target(plan));
            // the projector's display (#1298): cover at 16:9, for everyone, a joiner too
            booths.AskDisplay(plan, 0, Display);
            Expect(await Until(() => booths.DisplayOf(plan, 0) == Display, 10), "the server granted A's display (cover, 16:9)");
            Expect(await Until(() => booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), 10),
                $"the server granted A, inside, {CinemaBooths.Unpack(Target(plan))}");
            // said over and over: C has not joined yet
            bool b = false, c = false;
            for (int t = 0; t < 150 && !(b && c); t++)
            {
                Say($"showings {Showings(booths, plan)}");
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

                // a live share: A's test pattern, through the server to B and back to A
                booths.StartSharing(plan, 0, new CaptureSource(CaptureKind.Test, "Test pattern", "test"), CinemaVideo.LiveQuality.Low, false, null);
                Expect(await Until(() => booths.SharingIn(plan, 0), 10), "the server granted A's share");
                var (live, liveT0) = booths.VideoOf(plan, 0);
                Expect(CinemaVideo.LiveOf(live).Label == "Test pattern", $"the hall shows A's share ({live})");
                bool inside = false;
                for (int t = 0; t < 40 && !inside; t++)
                {
                    Say("live");
                    inside = await Until(() => _heard.Any(l => l.Contains("CN B inside")), 2);
                }
                Expect(inside, "B came in to watch");
                await Watch(live, liveT0);
                booths.StopSharing();
                Expect(await Until(() => booths.VideoOf(plan, 0).Link.Length == 0, 10), "A stopped the share: the hall's link is gone");

                // a video file from A's own disk, streamed from A (phase 4)
                booths.StartFile(plan, 0, VideoLink, CinemaVideo.LiveQuality.Low);
                Expect(await Until(() => booths.SharingIn(plan, 0), 10), "the server granted A's file");
                var (file, fileT0) = booths.VideoOf(plan, 0);
                Expect(CinemaVideo.LiveOf(file).Label.StartsWith("File: ", StringComparison.Ordinal), $"the hall shows A's file ({file})");
                bool watching = false;
                for (int t = 0; t < 30 && !watching; t++)
                {
                    Say("file");
                    watching = await Until(() => _heard.Any(l => l.Contains("CN B filewatch")), 2);
                }
                Expect(watching, "B watches A's file");
                await Watch(file, fileT0);
                booths.StopSharing();
            }
        }
        else
        {
            if (!await Heard("A", "set", 240)) { Fail("A never set anything"); return; }
            string line = _heard.Last(l => l.Contains("CN A set"));
            string plan = line[(line.IndexOf("CN A set", StringComparison.Ordinal) + "CN A set".Length)..].Trim();
            // the films the halls show by themselves: the same here as at A, a late joiner too
            if (_heard.LastOrDefault(l => l.Contains("CN A showings")) is { } shown)
            {
                string theirs = shown[(shown.IndexOf("CN A showings", StringComparison.Ordinal) + 13)..].Trim();
                string mine = Showings(booths, plan);
                Expect(mine == theirs, $"{_role} draws the halls' showings as A did ({mine} against {theirs})");
            }
            else Fail("A never said its showings");
            Expect(await Until(() => booths.StateOf(plan, 0) == CinemaBooths.Unpack(Target(plan)), 10),
                $"{_role} sees {plan}'s hall as A set it, {CinemaBooths.Unpack(Target(plan))}");
            Expect(await Until(() => booths.DisplayOf(plan, 0) == Display, 10), $"{_role} sees the hall's display as A set it (cover, 16:9)");
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
                if (_role == "B")
                {
                    // A's live share: only from inside, so B walks in
                    if (!await Heard("A", "live", 120)) { Fail("A never shared"); return; }
                    if (!await WalkIn(me, interiors, door)) return;
                    Expect(interiors.Current?.Key == plan, $"B is in A's house ({interiors.Current?.Key})");
                    Say("inside");
                    var (live, liveT0) = booths.VideoOf(plan, 0);
                    Expect(CinemaVideo.IsLive(live), $"B holds A's live share ({live})");
                    await Watch(live, liveT0);
                    Expect(await Until(() => booths.VideoOf(plan, 0).Link.Length == 0, 20), "B saw A's share end");
                    if (!await Heard("A", "file", 60)) { Fail("A never played a file"); return; }
                    Expect(await Until(() => CinemaVideo.LiveOf(booths.VideoOf(plan, 0).Link).Label.StartsWith("File: ", StringComparison.Ordinal), 10),
                        "B holds A's file share");
                    var (file, fileT0) = booths.VideoOf(plan, 0);
                    Say("filewatch");
                    await Watch(file, fileT0);
                }
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
        var booths = CinemaBooths.Instance!;
        string plan = InteriorManager.Instance?.Current?.Key ?? _heard.Last(l => l.Contains("CN A set"))[(_heard.Last(l => l.Contains("CN A set")).IndexOf("CN A set", StringComparison.Ordinal) + 8)..].Trim();
        booths.ProbeWatch = (plan, 0);
        double until = Net.ClockSync.ServerNow + WatchSeconds;
        ICinemaFeed? stream = null;
        long mark = -1;
        int marks = 0, close = 0;
        double worst = 0;
        bool relayed = false;
        int heard = 0, listened = 0;
        _lastLook = Core.RealClock.Now;
        _firstShown = double.NaN;
        while (Net.ClockSync.ServerNow < until && stream is not { Failed: true } && stream is not { Ended: true })
        {
            double pos = CinemaVideo.PositionAt(Net.ClockSync.ServerNow, t0);
            // the booth's own feed, pumped by it every frame as in a hall
            stream = booths.StreamOf(plan, 0);
            relayed |= stream is CinemaRelayFeed;
            if (stream == null) { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); continue; }
            // what the screen's speaker does in a hall: say which sample is heard, so a decoder keeps reading ahead of it
            if (stream.SoundAt(pos, out long heardNow)) stream.Heard(heardNow);
            long m = (long)Math.Floor(Net.ClockSync.ServerNow / 2);
            // the feed is pumped in the booth's own frame, after this one reads it: a frame of this peer that came long
            // after the last (a stall of the machine, not of the video) reads it a stall late, and the first second of a
            // feed is its start: neither is a mark of the clock
            double wall = Core.RealClock.Now;
            bool stalled = wall - _lastLook > 0.1;
            _lastLook = wall;
            if (stream.CurrentTime >= 0 && double.IsNaN(_firstShown)) _firstShown = pos;
            // a live source's first two seconds: the streamer's own decoder or capture still catching up (all peers alike)
            bool settling = !double.IsNaN(_firstShown) && pos < _firstShown + (CinemaVideo.IsLive(link) ? 2.0 : 1.0);
            if (m != mark && (stalled || settling) && stream.CurrentTime >= 0)
            {
                mark = m;
                GD.Print($"{Log} mark {m} at {pos:F3} s skipped ({(stalled ? "this peer's frame stalled" : "the feed is starting")}): shows {stream.CurrentTime:F3} s");
            }
            if (m != mark)
            {
                mark = m;
                if (stream.CurrentTime >= 0)
                {
                    double off = pos - stream.CurrentTime;
                    marks++;
                    // two frames: the relay's are 15 a second, a decoder's 30
                    // a live share's frames come as captured, not on a grid: a quarter second
                    double tolerance = CinemaVideo.IsLive(link) ? 0.25 : 2.0 / (stream is CinemaRelayFeed ? CinemaVideo.RelayFps : CinemaVideo.Fps);
                    if (off >= -0.001 && off < tolerance) close++;
                    // the sound due now: the test film's tone, not silence (a headless peer has no speaker to play it)
                    listened++;
                    if (stream.SoundAt(pos, out long sample))
                        for (long s = sample; s < sample + 4800; s += 7)
                            if (stream.Sample(s).Length() > 0.01f) { heard++; break; }
                    worst = Math.Max(worst, Math.Abs(off));
                    GD.Print($"{Log} mark {m} at {pos:F3} s shows {stream.CurrentTime:F3} s (off {off * 1000:F0} ms, restarts {stream.Restarts})");
                }
                else GD.Print($"{Log} mark {m} at {pos:F3} s: {stream.Status}");
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        booths.ProbeWatch = null;
        Expect(stream is { Failed: false }, $"{_role} plays the video ({stream?.Status ?? "no feed"})");
        Expect(heard >= listened - 1 && listened >= 3, $"{_role} has the sound due at {heard}/{listened} marks");
        Expect(relayed == (!CinemaVideoStream.CanDecode || CinemaVideo.IsLive(link)), $"{_role} plays it {(relayed ? "through the server" : "from the link itself")}");
        Expect(marks >= 3 && close == marks, $"{_role} on the shared clock at {close}/{marks} marks (worst {worst * 1000:F0} ms)");
    }

    private double _lastLook, _firstShown = double.NaN;

    /// <summary>
    /// Every hall's showing but hall 1's (the check works it): the film's place in the list ('-' the slides or the screen
    /// off) and its loop's phase (start modulo length) to the tenth of a second, as one line to compare.
    /// </summary>
    private static string Showings(CinemaBooths booths, string plan)
    {
        var parts = new List<string>();
        for (int h = 1; h < CinemaBooths.MaxHalls; h++)
        {
            var (link, t0) = booths.VideoOf(plan, h);
            int film = Array.FindIndex(CinemaVideo.OpenFilms, f => f.Link == link);
            // the loop's phase, not its start: the same whenever it is taken, across a loop
            double len = film >= 0 ? CinemaVideo.OpenFilms[film].Seconds : 1;
            double phase = ((t0 % len) + len) % len;
            parts.Add(link.Length == 0 ? "-" : $"{film}@{phase.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(",", parts);
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
