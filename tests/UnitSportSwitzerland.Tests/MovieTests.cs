using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The movie studio's data (#638): the replay ring that always records, the sampler a puppet is
/// drawn from, the clip edits on the timeline and the file a project is saved in.
/// </summary>
public class MovieTests
{
    private static readonly string[] Props = { "RideKindId", "HeldRadio" };
    private const double Dt = 1.0 / Channels.Rate;

    private static ActorState Frame(double e, float yaw = 0, float phase = 0, long ride = 0, string radio = "")
    {
        var s = new ActorState(Props.Length) { E = e, N = 1_100_000, Alt = 500 };
        s.F[Channels.Yaw] = yaw;
        s.F[Channels.Anim + 1] = phase;
        s.F[Channels.PoseLen] = 8;
        s.F[Channels.Pose + 3] = 1;   // identity quaternion
        s.Num[0] = ride;
        s.Str[1] = radio;
        return s;
    }

    private static ActorTrack Walk(int frames, Func<int, ActorState>? at = null)
    {
        var ring = new ReplayRing(frames, Props.Length);
        for (int k = 0; k < frames; k++) ring.Append(k * Dt, at?.Invoke(k) ?? Frame(2_600_000 + k));
        return ring.Slice(double.NegativeInfinity, out _)!;
    }

    [Fact]
    public void TheRingKeepsTheLastFramesAndWhatWasTrueBeforeThem()
    {
        var ring = new ReplayRing(10, Props.Length);
        for (int k = 0; k < 25; k++)
            ring.Append(k * Dt, Frame(k, ride: k >= 20 ? 7 : k >= 3 ? 5 : 0, radio: k >= 3 ? "srf3" : ""));
        Assert.Equal(10, ring.Count);
        Assert.Equal(15 * Dt, ring.Oldest, 9);
        Assert.Equal(1, ring.EventCount);   // the change at frame 3 folded into the start values

        var track = ring.Slice(double.NegativeInfinity, out double start)!;
        Assert.Equal(15 * Dt, start, 9);
        Assert.Equal(10, track.Frames);
        Assert.Equal(5, track.BaseNum[0]);
        Assert.Equal("srf3", track.BaseStr[1]);
        var o = new ActorState(Props.Length);
        track.Sample(4 * Dt, o);
        Assert.Equal(5, o.Num[0]);
        track.Sample(5.5 * Dt, o);
        Assert.Equal(7, o.Num[0]);
        Assert.Equal(15, track.Pos[0], 9);
    }

    [Fact]
    public void ASliceTakesOnlyWhatCameAfter()
    {
        var ring = new ReplayRing(100, Props.Length);
        for (int k = 0; k < 40; k++) ring.Append(k * Dt, Frame(k, ride: k >= 30 ? 2 : 0));
        var track = ring.Slice(19.5 * Dt, out double start)!;
        Assert.Equal(20 * Dt, start, 9);
        Assert.Equal(20, track.Frames);
        Assert.Equal(0, track.BaseNum[0]);
        Assert.Single(track.Events);
        Assert.Equal(10 * Dt, track.Events[0].T, 9);
        Assert.Null(ring.Slice(39 * Dt, out _));
    }

    [Fact]
    public void SamplingInterpolatesAndTakesTheShortWayRound()
    {
        var track = Walk(2, k => k == 0 ? Frame(0, yaw: 3.1f, phase: 0.98f) : Frame(10, yaw: -3.1f, phase: 0.02f));
        var o = new ActorState(Props.Length);
        track.Sample(Dt * 0.5, o);
        Assert.Equal(5, o.E, 6);
        // 3.1 -> -3.1 rad is 0.08 rad through π, not 6.2 rad back through 0
        Assert.True(Math.Abs(Math.Abs(o.F[Channels.Yaw]) - MathF.PI) < 0.05f, $"yaw {o.F[Channels.Yaw]}");
        // a gait phase wrapping 0.98 -> 0.02 takes the nearer frame, never 0.5
        Assert.True(o.F[Channels.Anim + 1] is 0.98f or 0.02f, $"phase {o.F[Channels.Anim + 1]}");
        float q = 0;
        for (int c = 0; c < 4; c++) q += o.F[Channels.Pose + c] * o.F[Channels.Pose + c];
        Assert.Equal(1f, q, 4);
        // past either end: the end frames
        track.Sample(-1, o);
        Assert.Equal(0, o.E, 9);
        track.Sample(99, o);
        Assert.Equal(10, o.E, 9);
    }

    [Fact]
    public void ClipsSplitTrimMoveAndOverlap()
    {
        var p = new MovieProject(Props);
        int lane = p.LaneFor("1", "Pilot", 1);
        Assert.Equal(lane, p.LaneFor("1", "Pilot", 1));
        var a = p.AddTrack(lane, Walk(301), 0);   // 10 s
        Assert.Equal(10, a.Length, 6);

        var b = p.Split(a.Id, 4)!;
        Assert.Equal(4, a.End, 6);
        Assert.Equal(4, b.Start, 6);
        Assert.Equal(4, b.In, 6);
        Assert.Null(p.Split(b.Id, 4.05));   // too close to an edge

        p.TrimStart(b.Id, 6);
        Assert.Equal(6, b.Start, 6);
        Assert.Equal(6, b.In, 6);
        p.TrimStart(b.Id, -5);   // back to the start of what was recorded, never before 0
        Assert.Equal(0, b.In, 6);
        Assert.Equal(0, b.Start, 6);
        p.TrimEnd(a.Id, 99);     // never past what was recorded
        Assert.Equal(10, a.Out, 6);
        p.TrimEnd(a.Id, 0);
        Assert.Equal(MovieProject.MinLength, a.Length, 6);

        p.Move(b.Id, 20);
        Assert.Equal(30, b.End, 6);
        Assert.Equal(30, p.Duration, 6);
        Assert.Equal(b.In + 1, b.Local(21), 6);

        var c = p.Duplicate(b.Id)!;
        Assert.Equal(30, c.Start, 6);
        p.Move(c.Id, 22);   // dropped onto b: it plays from where it begins
        Assert.Same(b, p.ActiveClip(lane, 21));
        Assert.Same(c, p.ActiveClip(lane, 23));
        Assert.Null(p.ActiveClip(lane, 15));
        Assert.True(p.Delete(c.Id));
        Assert.Same(b, p.ActiveClip(lane, 23));
    }

    [Fact]
    public void GrabsOfOneSessionKeepTheirSpacing()
    {
        var p = new MovieProject(Props);
        Assert.Equal(0, p.PlaceGrab(1000));
        p.AddTrack(p.LaneFor("1", "Me", 1), Walk(91), 0);
        Assert.Equal(40, p.PlaceGrab(1040), 9);
        p.NewSession();   // a loaded project or a new run: after everything there is
        Assert.Equal(p.Duration + 1, p.PlaceGrab(5), 9);
    }

    /// <summary>
    /// Two tracks alike once through the file (#1503, G2 A2b: places as ECEF, velocities in the local east-up-south,
    /// headings from true north): the place to a tenth of a millimetre, the velocity and the heading to float rounding,
    /// every other channel exactly.
    /// </summary>
    internal static void AssertSameTrack(ActorTrack a, ActorTrack b)
    {
        Assert.Equal(a.Frames, b.Frames);
        for (int k = 0; k < a.Pos.Length; k++) Assert.True(Math.Abs(a.Pos[k] - b.Pos[k]) < 1e-4, $"pos[{k}] {a.Pos[k]} came back as {b.Pos[k]}");
        for (int k = 0; k < a.F.Length; k++)
        {
            int c = k % Channels.Stride;
            if (c is >= Channels.Vel and < Channels.Vel + 3 or Channels.Yaw)
                Assert.True(Math.Abs(a.F[k] - b.F[k]) < 1e-5f, $"channel {c} of frame {k / Channels.Stride}: {a.F[k]} came back as {b.F[k]}");
            else Assert.Equal(a.F[k], b.F[k]);
        }
    }

    internal static void AssertSameTracks(MovieProject a, MovieProject b)
    {
        Assert.Equal(a.Tracks.Count, b.Tracks.Count);
        for (int i = 0; i < a.Tracks.Count; i++) AssertSameTrack(a.Tracks[i], b.Tracks[i]);
        Assert.Equal(a.Cameras.Count, b.Cameras.Count);
        for (int i = 0; i < a.Cameras.Count; i++)
        {
            Assert.Equal(a.Cameras[i].Keys.Count, b.Cameras[i].Keys.Count);
            for (int k = 0; k < a.Cameras[i].Keys.Count; k++)
            {
                var (x, y) = (a.Cameras[i].Keys[k], b.Cameras[i].Keys[k]);
                Assert.True(Math.Abs(x.E - y.E) < 1e-4 && Math.Abs(x.N - y.N) < 1e-4 && Math.Abs(x.Alt - y.Alt) < 1e-4, "a camera key moved");
                Assert.True(Math.Abs(new Godot.Quaternion(x.Qx, x.Qy, x.Qz, x.Qw).Dot(new Godot.Quaternion(y.Qx, y.Qy, y.Qz, y.Qw))) > 1 - 1e-6f, "a camera key turned");
            }
        }
    }

    [Fact]
    public void AProjectSurvivesTheFileAndAPropertyRename()
    {
        var p = new MovieProject(Props) { Name = "Landing" };
        var track = Walk(60, k => Frame(k, yaw: k * 0.01f, ride: k >= 30 ? 9 : 0, radio: k >= 45 ? "couleur3" : ""));
        var clip = p.AddTrack(p.LaneFor("42", "Pilot", 42), track, 3);
        p.Split(clip.Id, 4);

        var bytes = new MemoryStream();
        MovieFile.Write(p, bytes);
        bytes.Position = 0;
        var q = MovieFile.Read(bytes, Props);
        Assert.Equal("Landing", q.Name);
        Assert.Equal(42, q.Lanes[0].PeerId);
        Assert.Equal(2, q.Clips.Count);
        Assert.Equal(track.Times, q.Tracks[0].Times);
        AssertSameTrack(track, q.Tracks[0]);
        Assert.Equal(track.Events, q.Tracks[0].Events);
        var again = new MemoryStream();
        MovieFile.Write(q, again);
        // the same file again, its places (ECEF since version 7, #1503) only a rounding apart
        Assert.Equal(bytes.Length, again.Length);
        AssertSameTracks(q, MovieFile.Read(new MemoryStream(again.ToArray()), Props));

        // a newer build without HeldRadio and with a new property: the ride still plays, the radio is gone
        bytes.Position = 0;
        var r = MovieFile.Read(bytes, new[] { "NewThing", "RideKindId" });
        var o = new ActorState(2);
        r.Tracks[0].Sample(59 * Dt, o);
        Assert.Equal(9, o.Num[1]);
        Assert.Equal(0, o.Num[0]);
        Assert.Throws<InvalidDataException>(() => MovieFile.Read(new MemoryStream(new byte[16]), Props));
    }

    [Fact]
    public void CompactKeepsOnlyWhatPlays()
    {
        var p = new MovieProject(Props);
        var a = p.AddTrack(p.LaneFor("1", "A", 1), Walk(30), 0);
        var b = p.AddTrack(p.LaneFor("2", "B", 2), Walk(40), 0);
        p.Delete(a.Id);
        p.Compact();
        Assert.Single(p.Tracks);
        Assert.Single(p.Lanes);
        Assert.Equal("2", p.Lanes[0].Key);
        Assert.Equal(0, p.Clips[0].Lane);
        Assert.Equal(b.Id, p.Clips[0].Id);
    }
}
