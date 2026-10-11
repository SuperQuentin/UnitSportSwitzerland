using Godot;
using UnitSport.Movie;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// A Record / Stop session streamed to disk (#782): pieces written by the spool's worker and joined
/// at Stop must give what one grab of the whole would have, a player's run as one track, the world as
/// one, the sound as one .wav, and split only where something really broke off.
/// </summary>
public sealed class MovieSpoolTests : IDisposable
{
    private static readonly string[] Props = { "RideKindId", "HeldRadio" };
    private const double Dt = 1.0 / Channels.Rate;
    private const int Rate = 8000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"usmovie-spool-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private SessionSpool NewSpool() => new(Path.Combine(_dir, "session.spool"), Path.Combine(_dir, "audio"), Rate);

    private static ActorState Frame(int k, long ride = 0, string radio = "")
    {
        var s = new ActorState(Props.Length) { E = 2_600_000 + k, N = 1_200_000, Alt = 500 };
        s.F[Channels.Yaw] = k * 0.01f;
        s.Num[0] = ride;
        s.Str[1] = radio;
        return s;
    }

    private static WorldPose At(double e) => new() { X = e, Y = 1_200_000, Z = 500, Rot = Quaternion.Identity, Scale = Vector3.One };
    private static WorldPose Local(float x) => new() { X = x, Rot = Quaternion.Identity, Scale = Vector3.One };

    [Fact]
    public async Task APlayersPiecesJoinIntoTheTrackOneGrabWouldHaveGiven()
    {
        var ring = new ReplayRing(2000, Props.Length);
        var spool = NewSpool();
        double grabbed = double.NegativeInfinity;
        // 300 frames in pieces of 100: the ride changes inside the second piece, the radio exactly where the third starts
        for (int k = 0; k < 300; k++)
        {
            ring.Append(10 + k * Dt, Frame(k, ride: k >= 150 ? 4 : 0, radio: k >= 200 ? "srf3" : ""));
            if (k % 100 != 99) continue;
            var piece = new SessionSpool.Piece();
            var track = ring.Slice(grabbed, out double start)!;
            piece.Actors.Add(new SessionSpool.ActorPiece(1, "1", "You", 0, start, track));
            grabbed = ring.Newest;
            spool.Add(piece);
        }
        var p = (await spool.Finish("Test", Props, Array.Empty<WorldLook>()))!;
        var whole = ring.Slice(double.NegativeInfinity, out _)!;

        Assert.Single(p.Clips);
        var joined = p.Tracks[0];
        Assert.Equal(whole.Frames, joined.Frames);
        var a = new ActorState(Props.Length);
        var b = new ActorState(Props.Length);
        foreach (double t in new[] { 0, 99.5 * Dt, 100 * Dt, 151 * Dt, 199.5 * Dt, 200 * Dt, 299 * Dt })
        {
            whole.Sample(t, a);
            joined.Sample(t, b);
            Assert.Equal(a.E, b.E, 6);
            Assert.Equal(a.F[Channels.Yaw], b.F[Channels.Yaw], 5);
            Assert.Equal(a.Num[0], b.Num[0]);
            Assert.Equal(a.Str[1], b.Str[1]);
        }
        Assert.Equal(2, joined.Events.Length);   // one change each, none repeated at the joins
        Assert.False(File.Exists(Path.Combine(_dir, "session.spool")));   // the temp file is gone
    }

    [Fact]
    public async Task APlayerOutOfViewForAWhileComesBackAsAClipOfItsOwn()
    {
        var spool = NewSpool();
        foreach (double from in new[] { 0.0, 5.0 })
        {
            var ring = new ReplayRing(100, Props.Length);
            for (int k = 0; k < 30; k++) ring.Append(from + k * Dt, Frame(k));
            var piece = new SessionSpool.Piece();
            var track = ring.Slice(double.NegativeInfinity, out double start)!;
            piece.Actors.Add(new SessionSpool.ActorPiece(7, "7", "Player 7", 7, start, track));
            spool.Add(piece);
        }
        var p = (await spool.Finish("Test", Props, Array.Empty<WorldLook>()))!;
        Assert.Equal(2, p.Clips.Count);
        Assert.Single(p.Lanes);                                       // the same player's lane
        Assert.Equal(5, p.Clips[1].Start - p.Clips[0].Start, 6);      // at its real spacing
    }

    [Fact]
    public async Task TheWorldsPiecesJoinByTheRecordersIds()
    {
        var looks = new[] { new WorldLook(), new WorldLook(), new WorldLook() };
        var ring = new WorldRing(60);
        var spool = NewSpool();
        double grabbed = double.NegativeInfinity;
        void Spool(double now)
        {
            var piece = new SessionSpool.Piece { World = ring.Slice(grabbed, now, looks, out double start, raw: true) };
            piece.WorldStart = start;
            grabbed = now;
            spool.Add(piece);
        }
        ring.Spawn(0, 1, -1, -1, At(0), true);        // a car driving through every piece
        ring.Spawn(0, 2, 1, 2, Local(0), true);       // its body, wearing look 2
        ring.Spawn(0, 3, -1, -1, At(500), true);      // parked all along
        ring.Spawn(0, 4, 3, 0, Local(0), true);
        for (int k = 1; k <= 90; k++)
        {
            double t = k * Dt;
            ring.Move(t, 1, At(k));
            if (k == 40) { ring.Spawn(t, 5, -1, -1, At(900), true); ring.Spawn(t, 6, 5, 1, Local(1), true); }
            if (k == 70) { ring.Gone(t, 6); ring.Gone(t, 5); }
            if (k == 50) ring.Show(t, 2, false);
            if (k % 30 == 0) Spool(t);
        }
        var p = (await spool.Finish("Test", Props, looks))!;
        var whole = new WorldRing(60);
        // the same events again, sliced once
        whole.Spawn(0, 1, -1, -1, At(0), true);
        whole.Spawn(0, 2, 1, 2, Local(0), true);
        whole.Spawn(0, 3, -1, -1, At(500), true);
        whole.Spawn(0, 4, 3, 0, Local(0), true);
        for (int k = 1; k <= 90; k++)
        {
            double t = k * Dt;
            whole.Move(t, 1, At(k));
            if (k == 40) { whole.Spawn(t, 5, -1, -1, At(900), true); whole.Spawn(t, 6, 5, 1, Local(1), true); }
            if (k == 70) { whole.Gone(t, 6); whole.Gone(t, 5); }
            if (k == 50) whole.Show(t, 2, false);
        }
        var expected = whole.Slice(double.NegativeInfinity, 90 * Dt, looks, out _)!;

        Assert.Single(p.Worlds);
        var joined = p.Worlds[0];
        Assert.Equal(expected.Duration, joined.Duration, 6);
        Assert.Equal(expected.Entities.Length, joined.Entities.Length);
        for (int i = 0; i < expected.Entities.Length; i++)
        {
            var x = expected.Entities[i];
            var y = joined.Entities[i];
            Assert.Equal(x.Parent, y.Parent);
            Assert.Equal(x.Born, y.Born, 6);
            Assert.Equal(x.Died, y.Died, 6);
            Assert.Equal(x.Times.Length, y.Times.Length);   // nothing repeated where the pieces meet
            foreach (double t in new[] { 0, 29.5 * Dt, 30 * Dt, 45 * Dt, 60.5 * Dt, 75 * Dt, 90 * Dt })
            {
                Assert.Equal(x.AliveAt(t), y.AliveAt(t));
                if (!x.AliveAt(t)) continue;
                Assert.Equal(x.PoseAt(t).X, y.PoseAt(t).X, 6);
                Assert.Equal(x.ShownAt(t), y.ShownAt(t));
                if (x.Parent >= 0) Assert.Same(expected.Looks[x.LookAt(t)], joined.Looks[y.LookAt(t)]);
            }
        }
    }

    [Fact]
    public async Task TheSoundGoesOnInOneWavUntilItBreaksOff()
    {
        var spool = NewSpool();
        var all = new List<short>();
        short[] Tone(int n, int from)
        {
            var s = new short[n];
            for (int i = 0; i < n; i++) s[i] = (short)(8000 * Math.Sin((from + i) * 0.05));
            return s;
        }
        // two seconds in four pieces that follow on, then a second later another second and a half
        for (int k = 0; k < 4; k++)
        {
            var samples = Tone(Rate / 2, k * Rate / 2);
            all.AddRange(samples);
            spool.Add(new SessionSpool.Piece { Sound = samples, SoundStart = 100 + k * 0.5 + (k == 2 ? 0.02 : 0) });   // a little jitter is no break
        }
        spool.Add(new SessionSpool.Piece { Sound = Tone(Rate * 3 / 2, 0), SoundStart = 103 });
        var p = (await spool.Finish("Test", Props, Array.Empty<WorldLook>()))!;

        Assert.Equal(2, p.Audio.Count);
        Assert.Equal(2, p.Audio[0].Duration, 6);
        Assert.Equal(1.5, p.Audio[1].Duration, 6);
        Assert.Equal(3, p.Clips[1].Start - p.Clips[0].Start, 6);
        Assert.All(p.Audio, a => Assert.True(a.Game));
        var mono = all.Select(s => s / 32768f).ToArray();
        Assert.Equal(Pcm.Peaks(mono, Rate, AudioAsset.PeaksPerSecond), p.Audio[0].Peaks);

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "audio", p.Audio[0].File));
        Assert.Equal(44 + all.Count * 2, bytes.Length);
        Assert.Equal(all.Count * 2, BitConverter.ToInt32(bytes, 40));   // the header written again with the size
        Assert.Equal(all[1234], BitConverter.ToInt16(bytes, 44 + 2 * 1234));
    }

    [Fact]
    public async Task NothingRecordedGivesNoProject()
    {
        var spool = NewSpool();
        spool.Add(new SessionSpool.Piece());
        Assert.Null(await spool.Finish("Test", Props, Array.Empty<WorldLook>()));
    }
}
