using Godot;
using UnitSport.Interiors.Pinball;
using Xunit;
using Xunit.Abstractions;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Pinball (#1325): the solver (src/Interiors/Pinball/PinballSolver.cs) on its four tables, and the rules
/// engine. Shots are flipped by a fixed timing from a ball rolling down an inlane, as a player would.
/// </summary>
public class PinballTests
{
    private readonly ITestOutputHelper _out;
    public PinballTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void A_venues_machines_are_all_different_up_to_four_and_neighbours_never_match()
    {
        for (int venue = 0; venue < 200; venue++)
        {
            float z = venue * 3.17f - 40f;
            for (int n = 1; n <= 7; n++)
            {
                // a row along x, listed right to left
                var machines = Enumerable.Range(0, n).Select(i => (X: 5f + venue * 0.41f + (n - 1 - i) * 0.76f, Z: z)).ToList();
                var tables = Enumerable.Range(0, n).Select(k => PinballLayouts.TableIn(machines, k)).ToList();
                Assert.All(tables, t => Assert.InRange(t, 0, PinballLayouts.Count - 1));
                if (n <= PinballLayouts.Count) Assert.Equal(n, tables.Distinct().Count());
                var row = Enumerable.Range(0, n).OrderBy(k => machines[k].X).Select(k => tables[k]).ToList();
                for (int i = 1; i < n; i++) Assert.NotEqual(row[i - 1], row[i]);
            }
        }
    }

    /// <summary>
    /// A ball rolling down <paramref name="side"/>'s inlane, that flipper pressed <paramref name="delayMs"/> after,
    /// held 0.3 s: the switches the ball closes in the next 3 s (until it comes back past the slings).
    /// </summary>
    internal static List<string> Shoot(PinballTable t, int side, int delayMs, float startSpeed = 0.3f)
    {
        var s = new PinballSolver(t);
        float dx = side == 0 ? -0.170f : 0.170f;
        var b = s.Serve();
        b.P = new Vector2(PinballLayouts.Xc + dx, 0.31f);
        b.V = new Vector2(0, -startSpeed);
        var hits = new List<string>();
        bool up = false;
        for (int step = 0; step < 3500; step++)
        {
            bool held = step >= delayMs && step < delayMs + 300;
            if (side == 0) s.LeftHeld = held; else s.RightHeld = held;
            s.Tick();
            foreach (var e in s.Events)
                if (e.Kind == PinballSolver.EventKind.Switch && step >= delayMs) hits.Add(t.Switches[e.Id].Name);
                else if (e.Kind == PinballSolver.EventKind.Drain) hits.Add("drain");
                else if (e.Kind == PinballSolver.EventKind.RampFail) hits.Add($"fail {t.Ramps[e.Id].Name}");
            s.Events.Clear();
            if (s.Balls.Count == 0) break;
            var ball = s.Balls[0];
            if (!up && hits.Count(h => h.StartsWith("[")) == 0 && ball.P.Y > 0.25f && step > delayMs) hits.Add($"[{Mathf.RadToDeg(ball.V.Angle()):F0}° {ball.V.Length():F2} @{ball.P.X:F3}]");
            if (ball.Pos3.Y > 0.45f) up = true;
            if (up && ball.Ramp < 0 && ball.Held < 0 && ball.P.Y < 0.34f) break;
            if (ball.Held >= 0) { hits.Add("held"); break; }
        }
        return hits;
    }

    /// <summary>
    /// A ball cradled on <paramref name="side"/>'s raised flipper, let go (it rolls down the bat) and flipped
    /// again <paramref name="delayMs"/> after: a drop catch, the way a player aims.
    /// </summary>
    internal static List<string> Cradle(PinballTable t, int side, int delayMs)
    {
        var s = new PinballSolver(t);
        var fl = t.Flippers[side];
        var b = s.Serve();
        var mid = fl.Pivot + Vector2.FromAngle(fl.Up) * 0.035f;
        b.P = mid + new Vector2(0, 0.03f);
        b.V = Vector2.Zero;
        var hits = new List<string>();
        bool up = false, shot = false;
        const int hold = 1200;
        for (int step = 0; step < hold + 3500; step++)
        {
            bool held = step < hold || step >= hold + delayMs && step < hold + delayMs + 300;
            if (side == 0) s.LeftHeld = held; else s.RightHeld = held;
            s.Tick();
            foreach (var e in s.Events)
                if (e.Kind == PinballSolver.EventKind.Switch && step >= hold) hits.Add(t.Switches[e.Id].Name);
                else if (e.Kind == PinballSolver.EventKind.Drain) hits.Add("drain");
                else if (e.Kind == PinballSolver.EventKind.RampFail) hits.Add($"fail {t.Ramps[e.Id].Name}");
            s.Events.Clear();
            if (s.Balls.Count == 0) break;
            var ball = s.Balls[0];
            if (!shot && step > hold + delayMs && ball.P.Y > 0.25f) { shot = true; hits.Add($"[{Mathf.RadToDeg(ball.V.Angle()):F0}° {ball.V.Length():F2} @{ball.P.X:F3}]"); }
            if (ball.Pos3.Y > 0.45f) up = true;
            if (up && ball.Ramp < 0 && ball.Held < 0 && ball.P.Y < 0.34f) break;
            if (ball.Held >= 0) { hits.Add("held"); break; }
        }
        return hits;
    }

    /// <summary>Every shot reached by some timing: (scenario, side, delay) for the first time each.</summary>
    internal static Dictionary<string, string> Reached(PinballTable t)
    {
        var got = new Dictionary<string, string>();
        void Note(List<string> hits, string how)
        {
            foreach (var shot in t.Shots)
                if (!got.ContainsKey(shot.Name) && shot.Switches.Any(sw => hits.Contains(t.Switches[sw].Name)))
                    got[shot.Name] = how;
        }
        bool All() => got.Count == t.Shots.Count;
        for (int d = 20; d <= 300 && !All(); d += 10)
            for (int side = 0; side < 2; side++) Note(Cradle(t, side, d), $"cradle {side} {d}");
        for (int d = 500; d <= 800 && !All(); d += 10)
            for (int side = 0; side < 2; side++) Note(Shoot(t, side, d), $"inlane {side} {d}");
        for (int d = 150; d <= 450 && !All(); d += 10)
            for (int side = 0; side < 2; side++) Note(Shoot(t, side, d, 0.9f), $"fast inlane {side} {d}");
        return got;
    }

    [Fact]
    public void Reach_report()
    {
        foreach (var t in PinballLayouts.All())
        {
            var got = Reached(t);
            _out.WriteLine($"== {t.Name}: " + string.Join(" | ", t.Shots.Select(s => got.TryGetValue(s.Name, out var how) ? $"{s.Name} ({how})" : $"MISSING {s.Name}")));
        }
    }

    /// <summary>
    /// A drawing of each table and its cradle shots (1 mm a pixel) into <c>PINBALL_PNG</c>'s folder, for
    /// laying out shots; nothing when the variable is unset.
    /// </summary>
    [Fact]
    public void Draw_tables_with_their_shots()
    {
        string? dir = System.Environment.GetEnvironmentVariable("PINBALL_PNG");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        foreach (var t in PinballLayouts.All())
        {
            var img = new TablePicture();
            foreach (var w in t.Walls) img.Line(w.A, w.B, w.Kind switch
            {
                PinballTable.WallKind.Sling => 0xff4040, PinballTable.WallKind.Target => 0xffd040, PinballTable.WallKind.Drop => 0xff9020,
                PinballTable.WallKind.Gate => 0x40ff40, PinballTable.WallKind.Rubber => 0xc0c0ff, _ => 0xffffff,
            });
            foreach (var d in t.Discs) img.Circle(d.C, d.R, d.Kind == PinballTable.DiscKind.Bumper ? 0xff40ff : 0xc0c0c0);
            foreach (var s in t.Sensors) img.Line(s.A, s.B, s.Kind == PinballTable.SensorKind.RampEntry ? 0x40a0ff : 0x40ff40);
            foreach (var s in t.Scoops) img.Circle(s.C, s.R, 0xffff00);
            foreach (var r in t.Ramps)
                for (int i = 1; i < r.Track.Length; i++)
                    img.Line(new Vector2(r.Track[i - 1].X, r.Track[i - 1].Y), new Vector2(r.Track[i].X, r.Track[i].Y), 0x2060c0);
            foreach (var f in t.Flippers) img.Line(f.Pivot, f.Pivot + Vector2.FromAngle(f.Rest) * f.Length, 0xff0000);
            for (int side = 0; side < 2; side++)
                for (int d = 40; d <= 280; d += 20)
                {
                    var path = CradlePath(t, side, d);
                    for (int i = 1; i < path.Count; i++) img.Line(path[i - 1], path[i], side == 0 ? 0xff8000 : 0x00c0ff, 0.5f);
                }
            File.WriteAllBytes(Path.Combine(dir, $"{t.Name}.png"), img.Png());
        }
    }
    private static List<Vector2> CradlePath(PinballTable t, int side, int delayMs)
    {
        var s = new PinballSolver(t);
        var fl = t.Flippers[side];
        var b = s.Serve();
        b.P = fl.Pivot + Vector2.FromAngle(fl.Up) * 0.035f + new Vector2(0, 0.03f);
        var path = new List<Vector2>();
        const int hold = 1200;
        for (int step = 0; step < hold + 1500 && s.Balls.Count > 0; step++)
        {
            bool held = step < hold || step >= hold + delayMs && step < hold + delayMs + 300;
            if (side == 0) s.LeftHeld = held; else s.RightHeld = held;
            s.Tick();
            if (step > hold + delayMs && step % 5 == 0) path.Add(new Vector2(b.Pos3.X, b.Pos3.Y));
        }
        return path;
    }
}

/// <summary>A tiny RGB canvas in the playfield's frame (1 mm a pixel, y up), saved as a PNG.</summary>
internal sealed class TablePicture
{
    private const int W = 520, H = 1080;
    private readonly byte[] _px = new byte[W * H * 3];

    private void Set(int x, int y, int rgb, float a)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        int i = ((H - 1 - y) * W + x) * 3;
        for (int c = 0; c < 3; c++)
        {
            int v = (rgb >> (16 - 8 * c)) & 0xff;
            _px[i + c] = (byte)(_px[i + c] * (1 - a) + v * a);
        }
    }

    private static (int, int) P(Vector2 p) => ((int)MathF.Round(p.X * 1000 + W / 2f), (int)MathF.Round(p.Y * 1000 + 5));

    public void Line(Vector2 a, Vector2 b, int rgb, float alpha = 1f)
    {
        int n = Math.Max(1, (int)((b - a).Length() * 1000));
        for (int i = 0; i <= n; i++)
        {
            var (x, y) = P(a + (b - a) * (i / (float)n));
            Set(x, y, rgb, alpha);
        }
    }

    public void Circle(Vector2 c, float r, int rgb)
    {
        for (int i = 0; i < 64; i++)
            Line(c + Vector2.FromAngle(i * MathF.Tau / 64) * r, c + Vector2.FromAngle((i + 1) * MathF.Tau / 64) * r, rgb);
    }

    public byte[] Png()
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(td);
            var crc = BitConverter.GetBytes(Crc(td)); Array.Reverse(crc); ms.Write(crc);
        }
        var ihdr = new byte[13];
        BitConverter.GetBytes(W).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(H).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Fastest, true))
            for (int y = 0; y < H; y++) { z.WriteByte(0); z.Write(_px, y * W * 3, W * 3); }
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xffffffff;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
