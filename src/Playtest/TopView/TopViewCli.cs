using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Playtest.Recording;

namespace UnitSport.Playtest.TopView;

/// <summary>
/// The offline top view (#844, docs/notes/general/topview.md), headless, no world:
/// <code>
/// --topview &lt;session dir&gt; [--from T] [--to T] [--at T] [--around E,N] [--radius m]
///           [--out x.png] [--size px] [--server &lt;dir&gt;] [--html] [--ground fixture:&lt;course&gt;|&lt;dir&gt;] [--no-ground]
/// </code>
/// The ground (#851) is the one the session ran on, found from its start event; <c>--ground</c>
/// names another, <c>--no-ground</c> draws the shapes alone.
/// Times are seconds since the session began (the report's times); the default window is the
/// session's last 30 s. The server's trace is read from <c>&lt;dir&gt;/server</c> when it is there,
/// and drawn dashed red with the offset of each thing the client drew. <c>--html</c> writes a page
/// with a slider instead (<c>.html</c> beside <c>--out</c>). Also <c>--playtest-report &lt;dir&gt;</c>:
/// writes a session's report.md again (after a crash).
/// </summary>
public static class TopViewCli
{
    public static bool Requested => CmdArgs.Has("--topview");
    public static bool ReportRequested => CmdArgs.Has("--playtest-report");

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Report()
    {
        string? dir = CmdArgs.Value("--playtest-report", notFlag: true);
        if (dir == null || !Directory.Exists(dir)) { GD.PrintErr("[topview] usage: --playtest-report <session dir>"); return 2; }
        SessionReport.Write(dir);
        GD.Print($"[playtest] wrote {Path.Combine(dir, SessionReport.ReportFile)}");
        return 0;
    }

    public static int Run()
    {
        string? dir = CmdArgs.Value("--topview", notFlag: true);
        if (dir == null || !File.Exists(Path.Combine(dir, TraceFormat.TraceFile)))
        {
            GD.PrintErr("[topview] usage: --topview <session dir> [--from T] [--to T] [--at T] [--around E,N] [--radius m] [--out x.png] [--size px] [--server dir] [--html] [--ground fixture:<course>|<dir>] [--no-ground]");
            return 2;
        }
        string outPath = CmdArgs.Value("--out", notFlag: true) ?? Path.Combine(PlaytestDirector.Root, "test_output", "topview.png");
        if (!Path.IsPathRooted(outPath)) outPath = Path.Combine(PlaytestDirector.Root, outPath);
        try
        {
            string written = Draw(dir, CmdArgs.Double("--from"), CmdArgs.Double("--to"), CmdArgs.Double("--at"), Around(), CmdArgs.Double("--radius"),
                CmdArgs.Value("--server", notFlag: true), outPath, CmdArgs.Int("--size") ?? 900, CmdArgs.Has("--html"),
                CmdArgs.Has("--no-ground") ? null : TopViewImage.SourceOf(dir, CmdArgs.Value("--ground", notFlag: true)));
            GD.Print($"[topview] {written}");
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PrintErr($"[topview] {e.Message}");
            return 1;
        }
    }

    private static EN? Around()
    {
        var parts = CmdArgs.Value("--around", notFlag: true)?.Split(',');
        return parts is [var e, var n] && double.TryParse(e, NumberStyles.Float, Inv, out double ev) && double.TryParse(n, NumberStyles.Float, Inv, out double nv)
            ? new EN(ev, nv) : null;
    }

    /// <summary>Draws a session's top view to <paramref name="outPath"/> (or its .html); what was written, for the log.</summary>
    public static string Draw(string dir, double? from, double? to, double? at, EN? around, double? radius, string? serverDir,
        string outPath, int px, bool html, Terrain.IChunkSource? groundSource = null)
    {
        var reader = new TraceReader(dir);
        double t1 = to ?? Math.Max(0, reader.Duration);
        double t0 = from ?? Math.Max(0, t1 - 30);
        if (t1 < t0) (t0, t1) = (t1, t0);
        // a long window would be millions of points: every few frames, up to about 3000 a track
        int every = Math.Max(1, (int)((t1 - t0) * PlaytestRecorder.Rate / 3000));
        var frames = reader.Window(t0, t1, every);
        var notes = SessionReport.ReadLines(Path.Combine(dir, SessionReport.NotesFile))
            .Where(n => n.Num("e") != null)
            .Select(n => new TopNote(n.T, n.Num("e")!.Value, n.Num("n")!.Value, n.Str("text")))
            .ToList();

        serverDir ??= Path.Combine(dir, "server");
        TraceReader? server = File.Exists(Path.Combine(serverDir, TraceFormat.TraceFile)) ? new TraceReader(serverDir) : null;
        // the server's frame times on the client's clock: both count from their own start, on one wall clock
        double shift = server == null ? 0 : (reader.Header.StartUnixMs - server.Header.StartUnixMs) / 1000.0;
        var serverFrames = server?.Window(t0 + shift, t1 + shift, every);

        TopViewShapes Shapes(double? when) => TopViewScene.Build(new TopViewInput
        {
            Frames = frames, Entities = reader.Entities, T0 = t0, T1 = t1, At = when, Around = around, Radius = radius, Notes = notes,
            ServerFrames = serverFrames, ServerEntities = server?.Entities, ServerShift = shift,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        if (html)
        {
            // one square for every step, so the ground is read once and lies under all of them
            if (around == null || radius == null)
            {
                var whole = Shapes(t1);
                around = new EN(whole.Min.E + whole.Side / 2, whole.Min.N + whole.Side / 2);
                radius = whole.Side / 2;
            }
            var htmlGround = TopViewImage.Ground(groundSource, Shapes(t1), px);
            string? background = htmlGround == null ? null
                : Convert.ToBase64String(Image.CreateFromData(px, px, false, Image.Format.Rgba8, htmlGround.Rgba).SavePngToBuffer());
            string page = Path.ChangeExtension(outPath, ".html");
            double step = Math.Max(0.5, (t1 - t0) / 120);
            var steps = new List<(double, string)>();
            for (double t = t0; t <= t1 + 1e-6; t += step) steps.Add((t, TopViewSvg.Render(Shapes(t), px, pixelText: false, htmlGround)));
            File.WriteAllText(page, TopViewSvg.Html($"{Path.GetFileName(dir.TrimEnd('/', '\\'))}: {t0.ToString("0.0", Inv)}-{t1.ToString("0.0", Inv)} s", steps, background));
            return $"wrote {page} ({steps.Count} steps, {frames.Count} frames{(server != null ? ", with the server's trace" : "")}{(htmlGround != null ? $", ground from {htmlGround.Tiles} tiles" : ", no ground")})";
        }
        var shapes = Shapes(at);
        var ground = TopViewImage.Ground(groundSource, shapes, px);
        var err = TopViewImage.Save(shapes, ground, px, outPath);
        if (err != Error.Ok) throw new IOException($"could not draw {outPath}: {err}");
        return $"wrote {outPath} ({shapes.Tracks.Count} tracks, {shapes.Boxes.Count} boxes, {shapes.Arrows.Count} players, {shapes.Offsets.Count} server offsets; "
            + $"{frames.Count} frames, {shapes.Side:0} m across; {(ground == null ? "no ground" : $"ground from {ground.Tiles} tiles, {ground.Roads.Count} roads")})";
    }
}
