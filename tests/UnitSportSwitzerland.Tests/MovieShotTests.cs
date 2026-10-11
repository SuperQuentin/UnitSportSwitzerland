using Godot;
using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The shot model (#776): rigs that follow an actor with a smoothing spring, the shake, the beat links,
/// the program's transitions between shots, and the file that keeps them.
/// </summary>
public class MovieShotTests
{
    private static readonly string[] Props = { "RideKindId" };
    private const double Dt = 1.0 / Channels.Rate;
    private const double E0 = 2_600_000, N0 = 1_200_000;

    /// <summary>A target walking north at <paramref name="speed"/> m/s, facing north (yaw 0).</summary>
    private static ShotMath.TargetSampler North(double speed) =>
        (double t, out double e, out double n, out double alt, out float yaw) => { e = E0; n = N0 + speed * t; alt = 500; yaw = 0; return true; };

    [Fact]
    public void AFollowKeepsTheTargetsPlaceAndSoftensItsTurns()
    {
        var fly = North(50);   // a plane's speed
        // the position is the target's, smoothing or not: the camera never falls behind or ahead
        Assert.True(ShotMath.Follow(20, 0.5, fly, out var e, out var n, out _, out var yaw));
        Assert.Equal((E0, N0 + 1000), (e, n));
        Assert.Equal(0, yaw, 6);
        // a quarter turn at 10 s: half a second later the heading is still on its way round, then settles
        ShotMath.TargetSampler turn = (double t, out double te, out double tn, out double ta, out float ty) =>
        { te = E0; tn = N0; ta = 0; ty = t >= 10 ? MathF.PI / 2 : 0; return true; };
        ShotMath.Follow(10.3, 0.5, turn, out _, out _, out _, out var during);
        Assert.InRange(during, 0.1f, 1.4f);
        ShotMath.Follow(14, 0.5, turn, out _, out _, out _, out var after);
        Assert.Equal(MathF.PI / 2, after, 2);
        // a pure function of time: asking in any order gives the same
        ShotMath.Follow(3, 0.5, turn, out _, out _, out _, out _);
        ShotMath.Follow(10.3, 0.5, turn, out _, out _, out _, out var again);
        Assert.Equal(during, again);
    }

    [Fact]
    public void TheFollowedHeadingTurnsTheShortWayRoundTheBack()
    {
        // facing just west of south, then just east of south: a few degrees, not a whole turn
        ShotMath.TargetSampler turn = (double t, out double e, out double n, out double alt, out float yaw) =>
        {
            e = E0; n = N0; alt = 0;
            yaw = t < 5 ? MathF.PI - 0.05f : -MathF.PI + 0.05f;
            return true;
        };
        ShotMath.Follow(5.5, 0.3, turn, out _, out _, out _, out var yaw);
        Assert.True(Math.Abs(Math.Abs(yaw) - MathF.PI) < 0.1, $"the spring went the long way: {yaw}");
    }

    [Fact]
    public void AChaseHangsBehindAndALeadAheadLookingBack()
    {
        // facing north: behind is south
        var chase = CameraTrack.DefaultPose(ShotRig.Chase);
        ShotMath.InFrame(ref chase, E0, N0, 500, 0);
        Assert.Equal(E0, chase.E, 3);
        Assert.Equal(N0 - 7, chase.N, 3);
        Assert.Equal(502, chase.Alt, 3);
        // facing west (yaw +90°): behind is east
        var west = CameraTrack.DefaultPose(ShotRig.Chase);
        ShotMath.InFrame(ref west, E0, N0, 500, MathF.PI / 2);
        Assert.Equal(E0 + 7, west.E, 3);
        Assert.Equal(N0, west.N, 3);
        // a lead is ahead, its view turned round to the target
        var lead = CameraTrack.DefaultPose(ShotRig.Lead);
        ShotMath.InFrame(ref lead, E0, N0, 500, 0);
        Assert.Equal(N0 + 6, lead.N, 3);
        var forward = new Quaternion(lead.Qx, lead.Qy, lead.Qz, lead.Qw) * Vector3.Forward;
        Assert.True(forward.Z > 0.9f, $"a lead looks back south (+Z), got {forward}");
    }

    [Fact]
    public void FrameFromViewKeepsTheViewInTheTargetsFrame()
    {
        var p = Walker(out int lane);
        var cam = p.Camera;
        cam.Rig = ShotRig.Chase; cam.Target = lane; cam.Smoothing = 0;
        // the view 3 m east of the walker's body at 2 s, looking west
        var look = new Quaternion(Vector3.Up, MathF.PI / 2);
        double e = E0 + 3, n = N0 + 4, alt = 500 + ShotMath.PartHeight(TargetPart.Body);
        var key = p.FrameFromView(0, 2, null, e, n, alt, look, 50)!;
        Assert.Equal(3, key.E, 3);       // x right of a north-facing walker is east
        Assert.Equal(0, key.Alt, 3);
        Assert.Equal(0, key.N, 3);
        // the shot films from there at 2 s, and keeps that side as the walker goes on
        p.ShotPose(0, 2, null, out var at2, out _);
        Assert.Equal((e, n, alt), (at2.E, at2.N, at2.Alt), new TupleNear(1e-3));
        p.ShotPose(0, 7, null, out var at7, out _);
        Assert.Equal(E0 + 3, at7.E, 3);
        Assert.Equal(N0 + 14, at7.N, 3);
        Assert.Equal(50, at7.Lens, 3);
        Assert.Single(cam.Keys);         // a placement: one key, replaced, not added to
        p.FrameFromView(0, 5, null, e, n, alt, look, 35);
        Assert.Single(cam.Keys);
    }

    private sealed class TupleNear : IEqualityComparer<(double, double, double)>
    {
        private readonly double _eps;
        public TupleNear(double eps) => _eps = eps;
        public bool Equals((double, double, double) a, (double, double, double) b) =>
            Math.Abs(a.Item1 - b.Item1) < _eps && Math.Abs(a.Item2 - b.Item2) < _eps && Math.Abs(a.Item3 - b.Item3) < _eps;
        public int GetHashCode((double, double, double) obj) => 0;
    }

    [Fact]
    public void ShakeIsTheSameAtEveryReplayAndNothingWhenOff()
    {
        var hand = new ShakeSettings { Style = ShakeStyle.Handheld, Strength = 1 };
        var a = ShotMath.Shake(hand, 7, 3.21);
        var b = ShotMath.Shake(hand, 7, 3.21);
        Assert.Equal(a, b);
        Assert.NotEqual(a, ShotMath.Shake(hand, 8, 3.21));
        Assert.True(a.Move.Length() < 0.1f && a.Turn.Length() < 0.1f, "a handheld shake stays small");
        Assert.Equal((Vector3.Zero, Vector3.Zero), ShotMath.Shake(new ShakeSettings(), 7, 3.21));
        // a beat link can add shake to a shot that has none
        Assert.NotEqual(Vector3.Zero, ShotMath.Shake(new ShakeSettings(), 7, 3.21, 0.5f).Turn);
    }

    private static readonly List<(double T, bool Downbeat)> Beats =
        Enumerable.Range(0, 17).Select(i => (i * 0.5, i % 4 == 0)).ToList();   // 120 BPM for 8 s

    [Fact]
    public void BeatShapesPulseStepAndRamp()
    {
        var pulse = new BeatLink { Shape = BeatShape.Pulse, Amount = 10 };
        Assert.Equal(10, ShotMath.Beat(pulse, 1.0, Beats, Array.Empty<double>()), 3);     // on the beat: the peak
        Assert.Equal(0, ShotMath.Beat(pulse, 1.25, Beats, Array.Empty<double>()), 3);     // between: the trough
        var ramp = new BeatLink { Shape = BeatShape.Ramp, Amount = 10 };
        Assert.Equal(5, ShotMath.Beat(ramp, 1.25, Beats, Array.Empty<double>()), 3);      // half way: half
        var step = new BeatLink { Shape = BeatShape.Step, Steps = new[] { 0f, 26f } };   // 24 mm, 50 mm, 24 mm …
        Assert.Equal(0, ShotMath.Beat(step, 1.1, Beats, Array.Empty<double>()), 3);
        Assert.Equal(26, ShotMath.Beat(step, 1.6, Beats, Array.Empty<double>()), 3);
        Assert.Equal(0, ShotMath.Beat(step, 2.1, Beats, Array.Empty<double>()), 3);
        // on the bar: one stretch every four beats
        var bar = new BeatLink { Shape = BeatShape.Ramp, Amount = 8, Every = 4 };
        Assert.Equal(4, ShotMath.Beat(bar, 3.0, Beats, Array.Empty<double>()), 3);
        // on the markers, and nothing outside the beats
        var marked = new BeatLink { Shape = BeatShape.Ramp, Amount = 2, Every = 0 };
        Assert.Equal(1, ShotMath.Beat(marked, 2, Beats, new[] { 1.0, 3.0 }), 3);
        Assert.Equal(0, ShotMath.Beat(pulse, 9, Beats, Array.Empty<double>()));
        Assert.Equal(0, ShotMath.Beat(pulse, -1, Beats, Array.Empty<double>()));
    }

    private static MovieProject Walker(out int lane)
    {
        // an actor walking north at 2 m/s for 10 s
        var ring = new ReplayRing(301, Props.Length);
        for (int k = 0; k <= 300; k++)
        {
            var s = new ActorState(Props.Length) { E = E0, N = N0 + 2 * k * Dt, Alt = 500 };
            s.F[Channels.PoseLen] = 8;
            s.F[Channels.Pose + 3] = 1;
            ring.Append(k * Dt, s);
        }
        var p = new MovieProject(Props);
        lane = p.LaneFor("Owner", "You", 1);
        p.AddTrack(lane, ring.Slice(double.NegativeInfinity, out _)!, 0);
        return p;
    }

    [Fact]
    public void ALeadShotStaysAheadOfItsActor()
    {
        var p = Walker(out int lane);
        var cam = p.Camera;
        cam.Rig = ShotRig.Lead;
        cam.Target = lane;
        cam.Smoothing = 0;
        Assert.True(p.ShotPose(0, 5, null, out var pose, out _));
        Assert.Equal(N0 + 10 + 6, pose.N, 2);   // the actor at +10 m, the lead 6 m further on
        Assert.Equal(500 + ShotMath.PartHeight(TargetPart.Body) + 0.6, pose.Alt, 2);
        // a lens beat link on the music's beats would need sound; markers do too
        cam.Links.Add(new BeatLink { Param = BeatParam.Lens, Shape = BeatShape.Ramp, Amount = 20, Every = 0 });
        p.AddMarker(4); p.AddMarker(6);
        p.ShotPose(0, 5, null, out var punched, out _);
        Assert.Equal(pose.Lens + 10, punched.Lens, 2);
        // no keys and no target: nothing to show
        Assert.False(p.ShotPose(p.Cameras.IndexOf(p.AddCamera()), 5, null, out _, out _));
    }

    private static CameraKey Key(double t, double e) => new() { T = t, E = e, N = N0, Alt = 500, Lens = 35, Ease = KeyEase.Cut };

    [Fact]
    public void TransitionsBlendFadeAndCut()
    {
        var p = new MovieProject(Props);
        p.Camera.Set(Key(0, E0), -1);
        p.AddCamera().Set(Key(0, E0 + 100), -1);
        var cut = p.CutTo(5, 1, -1);
        cut.Transition = TransitionKind.Blend;
        cut.Duration = 1;
        Assert.Equal(E0, p.Program(4.4, null).Pose.E, 3);
        var mid = p.Program(5, null);
        Assert.Equal(E0 + 50, mid.Pose.E, 3);
        Assert.Equal(0.5f, mid.Mix, 3);
        Assert.Equal(E0 + 100, p.Program(5.6, null).Pose.E, 3);

        cut.Transition = TransitionKind.FadeBlack;
        Assert.Equal(1, p.Program(5, null).Fade, 3);
        Assert.Equal(0, p.Program(4.4, null).Fade, 3);
        Assert.Equal(E0 + 100, p.Program(5.01, null).Pose.E, 3);

        cut.Transition = TransitionKind.Cut;
        Assert.Equal(E0, p.Program(4.99, null).Pose.E, 3);
        Assert.Equal(E0 + 100, p.Program(5, null).Pose.E, 3);
    }

    [Fact]
    public void SequencesKeepTheirOwnCuts()
    {
        var p = new MovieProject(Props);
        p.AddCamera();
        p.CutTo(2, 1, -1);
        p.ActiveSequence = p.AddSequence();
        Assert.Empty(p.Cuts);
        p.CutTo(4, 0, -1);
        Assert.Equal(1, p.ProgramCamera(3) + 1);   // the second sequence shows Cam 1 throughout
        p.ActiveSequence = 0;
        Assert.Equal(1, p.ProgramCamera(3));
        Assert.True(p.RemoveCamera(1));
        Assert.Empty(p.Sequences[0].Cuts);
        Assert.Single(p.Sequences[1].Cuts);
    }

    [Fact]
    public void ShotsAndSequencesSurviveTheFileAndOldCutsBecomeMain()
    {
        var p = Walker(out int lane);
        var chase = p.AddCamera();
        chase.Group = "Take-off"; chase.Rig = ShotRig.Chase; chase.Target = lane; chase.Part = TargetPart.Head; chase.Smoothing = 0.8f;
        chase.Shake = new ShakeSettings { Style = ShakeStyle.Engine, Strength = 0.6f, Speed = 12, Turn = 0.4f, Roll = 0.5f };
        chase.Links.Add(new BeatLink { Param = BeatParam.Lens, Shape = BeatShape.Step, Steps = new[] { 0f, 26f }, Every = 2, Phase = 0.1f });
        var c = p.CutTo(3, 1, -1);
        c.Transition = TransitionKind.Crossfade; c.Duration = 0.6;
        p.Sequences.Add(new Sequence { Name = "Landing", In = 4, Out = 9 });
        p.ActiveSequence = 1;

        var bytes = new MemoryStream();
        MovieFile.Write(p, bytes);
        bytes.Position = 0;
        var q = MovieFile.Read(bytes, Props);
        var back = q.Cameras[1];
        Assert.Equal(("Take-off", ShotRig.Chase, lane, TargetPart.Head, 0.8f, chase.Seed), (back.Group, back.Rig, back.Target, back.Part, back.Smoothing, back.Seed));
        Assert.Equal((ShakeStyle.Engine, 0.6f, 12f, 0.4f, 0.5f), (back.Shake.Style, back.Shake.Strength, back.Shake.Speed, back.Shake.Turn, back.Shake.Roll));
        Assert.Equal(new[] { 0f, 26f }, back.Links[0].Steps);
        Assert.Equal((BeatShape.Step, 2, 0.1f), (back.Links[0].Shape, back.Links[0].Every, back.Links[0].Phase));
        Assert.Equal(2, q.Sequences.Count);
        Assert.Equal(1, q.ActiveSequence);
        Assert.Equal(("Landing", 4.0, 9.0), (q.Sequences[1].Name, q.Sequences[1].In, q.Sequences[1].Out));
        Assert.Equal((TransitionKind.Crossfade, 0.6), (q.Sequences[0].Cuts[0].Transition, q.Sequences[0].Cuts[0].Duration));
        var again = new MemoryStream();
        MovieFile.Write(q, again);
        Assert.Equal(bytes.ToArray(), again.ToArray());

        // a version 5 file: one cut list, which loads as the sequence "Main"
        var old = new MemoryStream();
        MovieFile.Write(p, old, 5);
        old.Position = 0;
        var o = MovieFile.Read(old, Props);
        Assert.Single(o.Sequences);
        Assert.Equal("Main", o.Sequences[0].Name);
        Assert.Single(o.Cuts);
        Assert.Equal(ShotRig.World, o.Cameras[1].Rig);
    }
}
