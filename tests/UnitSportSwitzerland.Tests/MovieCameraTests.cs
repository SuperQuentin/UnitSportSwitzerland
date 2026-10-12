using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The movie's camera track and cuts (#669): keys in order, smooth, linear and cut motion
/// between them, lenses, aiming at an actor, cutting the whole movie at once and the file.
/// </summary>
public class MovieCameraTests
{
    private static CameraKey Key(double t, double e, float yaw = 0, float lens = 35, KeyEase ease = KeyEase.Smooth, int lookAt = -1)
    {
        // a turn about the vertical axis
        return new CameraKey
        {
            T = t, E = e, N = 1_200_000, Alt = 600,
            Qy = MathF.Sin(yaw / 2), Qw = MathF.Cos(yaw / 2), Lens = lens, Ease = ease, LookAt = lookAt,
        };
    }

    [Fact]
    public void KeysStayInOrderAndReplaceANearOne()
    {
        var cam = new CameraTrack();
        cam.Set(Key(4, 40), 1 / 30.0);
        cam.Set(Key(0, 0, ease: KeyEase.Cut), 1 / 30.0);
        cam.Set(Key(2, 20), 1 / 30.0);
        Assert.Equal(new[] { 0.0, 2.0, 4.0 }, cam.Keys.Select(k => k.T));
        // a new view at nearly the same time replaces the key, keeping its motion and aim
        var again = cam.Set(Key(0.01, 5, ease: KeyEase.Linear), 1 / 30.0);
        Assert.Equal(3, cam.Keys.Count);
        Assert.Equal(0, again.T);
        Assert.Equal(5, again.E);
        Assert.Equal(KeyEase.Cut, again.Ease);
        cam.Retime(cam.Keys[0], 3);
        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, cam.Keys.Select(k => k.T));
        Assert.Same(cam.Keys[1], cam.Near(3.05, 0.1));
        Assert.Null(cam.Near(2.5, 0.1));
        Assert.True(cam.Remove(cam.Keys[1]));
    }

    [Fact]
    public void LinearMovesEvenlyCutHoldsThenJumps()
    {
        var cam = new CameraTrack();
        cam.Set(Key(0, 0, lens: 24, ease: KeyEase.Linear), 0);
        cam.Set(Key(2, 100, yaw: MathF.PI / 2, lens: 85, ease: KeyEase.Cut), 0);
        cam.Set(Key(4, 300), 0);
        Assert.True(cam.Sample(1, out var mid));
        Assert.Equal(50, mid.E, 6);
        Assert.Equal(54.5f, mid.Lens, 3);
        // halfway through a quarter turn: an eighth of a turn
        Assert.Equal(MathF.Sin(MathF.PI / 8), mid.Qy, 4);
        Assert.True(cam.Sample(3.9, out var held));
        Assert.Equal(100, held.E, 9);   // a cut holds its view ...
        Assert.True(cam.Sample(4, out var cut));
        Assert.Equal(300, cut.E, 9);    // ... and jumps at the next key
        Assert.True(cam.Sample(-5, out var before));
        Assert.Equal(0, before.E, 9);
        Assert.True(cam.Sample(99, out var after));
        Assert.Equal(300, after.E, 9);
        Assert.False(new CameraTrack().Sample(0, out _));
    }

    [Fact]
    public void SmoothPassesThroughEveryKeyWithoutACorner()
    {
        var cam = new CameraTrack();
        foreach (var (t, e) in new[] { (0.0, 0.0), (1.0, 10.0), (2.0, 30.0), (3.0, 30.0) }) cam.Set(Key(t, e), 0);
        foreach (var k in cam.Keys)
        {
            Assert.True(cam.Sample(k.T, out var at));
            Assert.Equal(k.E, at.E, 6);
        }
        // the speed on either side of a key matches: no corner
        double V(double t) { cam.Sample(t + 1e-4, out var a); cam.Sample(t - 1e-4, out var b); return (a.E - b.E) / 2e-4; }
        Assert.Equal(V(1 - 0.001), V(1 + 0.001), 0);
        Assert.InRange(V(1), 10, 20);
    }

    [Fact]
    public void ALensIsAFieldOfView()
    {
        Assert.Equal(53.1f, CameraTrack.Fov(24), 1);   // 2·atan(12/24)
        Assert.Equal(16.1f, CameraTrack.Fov(85), 1);   // 2·atan(12/85)
        Assert.Equal(CameraTrack.Fov(CameraTrack.MaxLens), CameraTrack.Fov(1000), 3);
    }

    [Fact]
    public void AimingIsTheLeftKeys()
    {
        var cam = new CameraTrack();
        cam.Set(Key(0, 0, lookAt: 1), 0);
        cam.Set(Key(2, 10), 0);
        cam.Sample(1, out var p);
        Assert.Equal(1, p.LookAt);
        cam.Sample(2, out p);
        Assert.Equal(-1, p.LookAt);
    }

    [Fact]
    public void TheProgramShowsTheLastCutsCamera()
    {
        var p = new MovieProject(new[] { "RideKindId" });
        var two = p.AddCamera();
        var three = p.AddCamera();
        Assert.Equal(new[] { "Cam 1", "Cam 2", "Cam 3" }, p.Cameras.Select(c => c.Name));
        Assert.Equal(0, p.ProgramCamera(5));   // no cut: the first camera
        p.CutTo(4, 2, 1 / 30.0);
        p.CutTo(2, 1, 1 / 30.0);
        Assert.Equal(new[] { 2.0, 4.0 }, p.Cuts.Select(c => c.T));
        Assert.Equal(0, p.ProgramCamera(1.9));
        Assert.Equal(1, p.ProgramCamera(2));
        Assert.Equal(2, p.ProgramCamera(9));
        // a cut where one already is changes it
        p.CutTo(4.01, 0, 1 / 30.0);
        Assert.Equal(2, p.Cuts.Count);
        Assert.Equal(0, p.ProgramCamera(5));
        p.RetimeCut(p.Cuts[1], 1);
        Assert.Equal(new[] { 1.0, 2.0 }, p.Cuts.Select(c => c.T));
        Assert.Same(p.Cuts[0], p.CutNear(1.05, 0.1));
        Assert.Equal(2, p.Duration, 9);   // cuts count in the length
        Assert.True(p.RemoveCut(p.Cuts[0]));

        // a camera removed: its cuts go, the later cameras' cuts follow their new place
        p.CutTo(3, 2, 0);
        Assert.True(p.RemoveCamera(1));
        Assert.Equal(new[] { 3.0 }, p.Cuts.Select(c => c.T));
        Assert.Equal(1, p.Cuts[0].Camera);
        Assert.Same(three, p.Cameras[1]);
        Assert.True(p.RemoveCamera(0));
        Assert.False(p.RemoveCamera(0));   // the last camera stays
        Assert.Equal("Cam 1", p.AddCamera().Name);   // the first free name
        Assert.DoesNotContain(two, p.Cameras);
    }

    [Fact]
    public void CamerasAndTheProgramSurviveTheFileAndVersion3StillLoads()
    {
        var p = new MovieProject(new[] { "RideKindId" });
        p.Camera.Set(Key(1, 10, lens: 50), 0);
        var wide = p.AddCamera();
        wide.Name = "Wide";
        wide.Set(Key(2, 20, lens: 14, ease: KeyEase.Linear), 0);
        p.CutTo(0, 1, 0);
        p.CutTo(3, 0, 0);
        var bytes = new MemoryStream();
        MovieFile.Write(p, bytes);
        bytes.Position = 0;
        var q = MovieFile.Read(bytes, p.Discrete);
        Assert.Equal(new[] { "Cam 1", "Wide" }, q.Cameras.Select(c => c.Name));
        Assert.Equal(14f, q.Cameras[1].Keys[0].Lens);
        Assert.Equal(new[] { (0.0, 1), (3.0, 0) }, q.Cuts.Select(c => (c.T, c.Camera)));

        var old = new MemoryStream();
        MovieFile.Write(p, old, 3);
        old.Position = 0;
        var o = MovieFile.Read(old, p.Discrete);
        Assert.Single(o.Cameras);
        Assert.Equal(50f, o.Camera.Keys[0].Lens);
        Assert.Empty(o.Cuts);
    }

    [Fact]
    public void CutAllCutsEveryLaneAtOnce()
    {
        var p = new MovieProject(new[] { "RideKindId" });
        var ring = new ReplayRing(301, 1);
        var s = new ActorState(1);
        s.F[Channels.PoseLen] = 8;
        for (int k = 0; k < 301; k++) ring.Append(k / 30.0, s);
        var track = ring.Slice(double.NegativeInfinity, out _)!;
        p.AddTrack(p.LaneFor("1", "A", 1), track, 0);
        p.AddTrack(p.LaneFor("2", "B", 2), track, 2);
        p.AddAudio(p.AudioLaneFor("Music"), new AudioAsset { Name = "S", File = "s.ogg", Duration = 3 }, 8);
        var made = p.CutAll(5);
        Assert.Equal(2, made.Count);   // the song does not reach 5 s
        Assert.Equal(5, p.Clips.Count);
        Assert.All(made, c => Assert.Equal(5, c.Start, 9));
    }

    [Fact]
    public void TheCameraSurvivesTheFileAndAimsOnlyAtActorsThatStay()
    {
        var p = new MovieProject(new[] { "RideKindId" });
        var ring = new ReplayRing(31, 1);
        var s = new ActorState(1);
        s.F[Channels.PoseLen] = 8;
        for (int k = 0; k < 31; k++) ring.Append(k / 30.0, s);
        var gone = p.AddTrack(p.LaneFor("1", "A", 1), ring.Slice(double.NegativeInfinity, out _)!, 0);
        p.AddTrack(p.LaneFor("2", "B", 2), ring.Slice(double.NegativeInfinity, out _)!, 0);
        p.Camera.Set(Key(0.5, 1, lens: 50, ease: KeyEase.Cut, lookAt: 1), 0);
        p.Camera.Set(Key(7, 2, lookAt: 0), 0);
        Assert.Equal(7, p.Duration, 9);   // the last key counts in the movie's length

        var bytes = new MemoryStream();
        MovieFile.Write(p, bytes);
        bytes.Position = 0;
        var q = MovieFile.Read(bytes, p.Discrete);
        Assert.Equal(2, q.Camera.Keys.Count);
        var k0 = q.Camera.Keys[0];
        Assert.Equal((0.5, 50f, KeyEase.Cut, 1), (k0.T, k0.Lens, k0.Ease, k0.LookAt));

        // lane A goes: the key aimed at B now aims at lane 0, the one aimed at A at nobody
        q.Delete(gone.Id);
        q.Compact();
        Assert.Equal(0, q.Camera.Keys[0].LookAt);
        Assert.Equal(-1, q.Camera.Keys[1].LookAt);
    }
}
