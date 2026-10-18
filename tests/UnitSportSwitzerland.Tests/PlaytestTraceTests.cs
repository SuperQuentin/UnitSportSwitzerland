using UnitSport.Playtest.Recording;
using UnitSport.Playtest.TopView;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The playtest recorder's trace (#844): written, read back, a window found through the index; and the top view drawn from it.</summary>
public class PlaytestTraceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "usp-trace-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>A car driving east at 10 m/s from (2600000, 1200000) and a player walking north beside it, 10 frames a second for <paramref name="seconds"/>.</summary>
    private static IEnumerable<TraceFrame> Drive(double seconds, double from = 0)
    {
        for (int i = 0; i < seconds * 10; i++)
        {
            double t = from + i / 10.0;
            var f = new TraceFrame();
            f.Clear(t);
            ref var car = ref f.Add();
            car.Id = 2; car.E = 2600000 + 10 * t; car.N = 1200000; car.Alt = 500; car.Heading = MathF.PI / 2; car.VE = 10;
            ref var me = ref f.Add();
            me.Id = 1; me.E = 2600000; me.N = 1200000 + 1.5 * t; me.Heading = 0; me.VN = 1.5f; me.HasPlayer = true; me.Ride = 3; me.Seat = -1; me.Held = 42;
            yield return f;
        }
    }

    private void Write(IEnumerable<TraceFrame> frames, long start = 1_700_000_000_000)
    {
        using var w = new TraceWriter(_dir, new TraceHeader(start, "client"));
        w.Entity(new TraceEntity { Id = 1, Kind = "player", Name = "1", Label = "Me", Local = true });
        w.Entity(new TraceEntity { Id = 2, Kind = "Car", Class = "VehicleBody", Name = "Vehicle_7", Label = "Car", Size = [2, 1.5f, 4.5f] });
        foreach (var f in frames) w.Frame(f);
    }

    [Fact]
    public void FramesRoundTrip()
    {
        Write(Drive(3));
        var r = new TraceReader(_dir);
        Assert.Equal("client", r.Header.Role);
        Assert.Equal(1_700_000_000_000, r.Header.StartUnixMs);
        Assert.Equal(2, r.Entities.Count);
        Assert.Equal(4.5f, r.Entities[2].Size[2]);
        var all = r.Window(0, 100);
        Assert.Equal(30, all.Count);
        Assert.True(all[25].TryGet(1, out var me));
        Assert.True(me.HasPlayer);
        Assert.Equal(3, me.Ride);
        Assert.Equal(42, me.Held);
        Assert.Equal(-1, me.Seat);
        Assert.True(all[25].TryGet(2, out var car));
        Assert.False(car.HasPlayer);
        Assert.Equal(2600025, car.E, 6);
    }

    [Fact]
    public void WindowSeeksThroughTheIndex()
    {
        Write(Drive(30));
        var r = new TraceReader(_dir);
        Assert.Equal(30, r.Duration);
        var w = r.Window(12.0, 13.95);
        Assert.Equal(20, w.Count);
        Assert.Equal(12.0, w[0].T, 6);
        Assert.Equal(13.9, w[^1].T, 6);
        Assert.Equal(5, r.Window(12, 13.95, every: 4).Count);
    }

    [Fact]
    public void ARelaunchAppendsToTheSameSession()
    {
        Write(Drive(2));
        // the game relaunched 5 s later: a fresh header is ignored, the first one's start kept
        Write(Drive(2, from: 7), start: 1_800_000_000_000);
        var r = new TraceReader(_dir);
        Assert.Equal(1_700_000_000_000, r.Header.StartUnixMs);
        Assert.Equal(40, r.Window(0, 100).Count);
        // the seconds with no frames point at the next one
        var gap = r.Window(4, 7.05);
        Assert.Single(gap);
        Assert.Equal(7.0, gap[0].T, 6);
    }

    [Fact]
    public void ACrashCutFrameEndsTheReadCleanly()
    {
        Write(Drive(1));
        string path = Path.Combine(_dir, TraceFormat.TraceFile);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^7]);
        Assert.Equal(9, new TraceReader(_dir).Window(0, 10).Count);
    }

    [Fact]
    public void RingKeepsTheLastFrames()
    {
        var ring = new TraceRing(5);
        for (int i = 0; i < 8; i++) ring.Next(i).Add().Id = i;
        Assert.Equal(5, ring.Count);
        Assert.Equal(3, ring[0].T);
        Assert.Equal(7, ring.Last!.T);
        Assert.Equal(2, ring.Window(5, 6).Count);
    }

    [Fact]
    public void TopViewDrawsTheCarAndThePlayerWhereTheyWere()
    {
        Write(Drive(10));
        var r = new TraceReader(_dir);
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = r.Window(0, 10), Entities = r.Entities, T0 = 2, T1 = 6,
            Notes = [new TopNote(4, 2600040, 1200000, "bumped the car")],
        });
        Assert.Equal(2, shapes.Tracks.Count);
        var box = Assert.Single(shapes.Boxes);
        Assert.Equal(2600060, box.Center.E, 3);   // the car at t = 6, the window's end
        Assert.Equal(4.5, box.Along, 3);
        var arrow = Assert.Single(shapes.Arrows);
        Assert.Equal("Me", arrow.Label);
        Assert.Equal(1200009, arrow.At.N, 3);
        // a tick a second: 2, 3, 4, 5, 6
        Assert.Equal(5, shapes.Tracks.First(t => t.Id == 2).Ticks.Count);
        Assert.Single(shapes.Pins);
        Assert.Equal(2, shapes.Vectors.Count);
        // the square holds both tracks
        Assert.True(shapes.Min.E <= 2600000 && shapes.Min.E + shapes.Side >= 2600060);
        Assert.True(shapes.Min.N <= 1200000 && shapes.Min.N + shapes.Side >= 1200009);

        string svg = TopViewSvg.Render(shapes, 600, pixelText: true);
        Assert.StartsWith("<svg", svg);
        Assert.DoesNotContain("<text", svg);
        Assert.Contains("<polygon", svg);
        Assert.Contains("<text", TopViewSvg.Render(shapes, 600, pixelText: false));
        Assert.Contains("type=\"range\"", TopViewSvg.Html("x", [(1, svg), (2, svg)]));
    }

    [Fact]
    public void TopViewAroundAPointKeepsOnlyWhatIsNear()
    {
        Write(Drive(10));
        var r = new TraceReader(_dir);
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = r.Window(0, 10), Entities = r.Entities, T0 = 0, T1 = 10, At = 1,
            Around = new EN(2600000, 1200005), Radius = 6,
        });
        // the car left the square after half a second, but was in it: still drawn; nothing else exists
        Assert.Equal(12, shapes.Side);
        Assert.Equal(2, shapes.Tracks.Count);
        Assert.Equal(2600010, Assert.Single(shapes.Boxes).Center.E, 3);
    }

    [Fact]
    public void ServerTraceShowsTheOffset()
    {
        Write(Drive(5));
        var r = new TraceReader(_dir);
        // the server saw the car half a metre behind, 0.2 s later on its clock
        var server = Drive(5).Select(f =>
        {
            for (int i = 0; i < f.Count; i++) if (f.States[i].Id == 2) { f.States[i].E -= 0.5; f.States[i].Id = 9; }
            f.T += 0.2;
            return f;
        }).ToList();
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = r.Window(0, 10), Entities = r.Entities, T0 = 0, T1 = 4, At = 3,
            ServerFrames = server, ServerShift = 0.2,
            ServerEntities = new Dictionary<int, TraceEntity> { [9] = r.Entities[2] with { Id = 9 } },
        });
        var off = Assert.Single(shapes.Offsets);
        Assert.Equal(0.5, off.Metres, 3);
        Assert.Contains(shapes.Boxes, b => b.Server);
    }

    [Theory]
    [InlineData(100, 20)]
    [InlineData(40, 5)]
    [InlineData(1000, 200)]
    [InlineData(7, 1)]
    public void ScaleBarIsRound(double side, double bar) => Assert.Equal(bar, TopViewScene.ScaleBar(side));
}
