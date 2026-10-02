using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Verification helper: analyses a track and prints what the director would have to work with.
///
/// <para>
/// <c>godot --path . -- --cinema &lt;track.gpx&gt;</c>
/// </para>
///
/// <para>
/// Thresholds for "a surge", "a summit", "a bend" cannot be picked by reasoning — the only way to
/// know whether a rule fires ten times or ten thousand on a real recording is to run it on one.
/// This prints the timeline and the counts so they are tuned against measurement.
/// </para>
/// </summary>
public partial class CinemaProbe : Node
{
    private readonly string _path;
    private readonly WorldOrigin _origin;
    private readonly IChunkSource _source;
    private readonly IReadOnlySet<TileId>? _known;
    private bool _started;

    public CinemaProbe(string path, WorldOrigin origin, IChunkSource source,
        IReadOnlySet<TileId>? known = null)
    {
        _path = path;
        _origin = origin;
        _source = source;
        _known = known;
        Name = "CinemaProbe";
    }

    public static string? ParseArgs() => CmdArgs.Value("--cinema");

    public override void _Process(double delta)
    {
        if (_started) return;
        _started = true;
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        GpxTrack track;
        try
        {
            track = GpxParser.Parse(_path);
        }
        catch (Exception e)
        {
            GD.PushError($"[cinema] could not read {_path}: {e.Message}");
            Callable.From(() => GetTree().Quit(1)).CallDeferred();
            return;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        GD.Print($"[cinema] {track.Name}: {track.Points.Count} pts, "
            + $"{(track.Length / 1000).ToString("0.00", inv)} km, "
            + $"{TimeSpan.FromSeconds(track.Duration):hh\\:mm\\:ss}");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var corridor = await Corridor.LoadAsync(track, _source, _known).ConfigureAwait(false);
        GD.Print($"[cinema] corridor: {corridor.TileCount} tiles, "
            + $"{corridor.BuildingCount} buildings, {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        var profile = TrackAnalyser.Analyse(track, _origin, corridor);
        GD.Print($"[cinema] analysed {profile.Samples.Count} samples in {clock.ElapsedMilliseconds} ms");

        // how much of the run has terrain under it at all: a corridor that failed to load makes
        // every gradient zero and every summit vanish, which would look like a flat route
        int draped = 0;
        foreach (var s in profile.Samples)
            if (corridor.HeightAt(s.E, s.N) != null) draped++;
        GD.Print($"[cinema] draped on real terrain: {draped}/{profile.Samples.Count} "
            + $"({100.0 * draped / Math.Max(1, profile.Samples.Count):F0}%)");

        double climb = 0;
        foreach (double g in profile.Gradient) climb = Math.Max(climb, g);
        GD.Print($"[cinema] steepest gradient {climb * 100:F1}%");
        GD.Print($"[cinema] revisited ground {profile.RevisitShare:P1}"
            + (profile.RevisitShare > 0.15
                ? "  <- an out-and-back or laps; a linear film will look like it repeats"
                : ""));

        GD.Print($"[cinema] --- {profile.Events.Count} events ---");
        foreach (var group in profile.Events.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
            GD.Print($"[cinema]   {group.Key,-16} {group.Count(),4}");

        GD.Print("[cinema] --- timeline (first 40) ---");
        foreach (var e in profile.Events.Take(40))
            GD.Print($"[cinema]   {TimeSpan.FromSeconds(e.Time):hh\\:mm\\:ss}  "
                + $"{e.Kind,-16} {e.Strength:F2}{(e.Label != null ? "  " + e.Label : "")}");

        // A run should offer a few events per minute. Far more means the detectors are firing on
        // noise; far fewer means the director will have nothing to cut to.
        double perMinute = profile.Events.Count / Math.Max(1.0, track.Duration / 60.0);
        GD.Print($"[cinema] density {perMinute:F2} events/min "
            + (perMinute is > 0.3 and < 6 ? "(workable)" : "(NEEDS TUNING)"));

        // How much of this run is on a structure, and how far the road surface sits from the
        // terrain there. That difference IS the bug: draped onto terrain, the runner would be
        // that far below a bridge deck or above a tunnel bore.
        var network = await RoadNetwork.LoadAsync(_source,
            track.Points.Min(q => q.E), track.Points.Min(q => q.N),
            track.Points.Max(q => q.E), track.Points.Max(q => q.N)).ConfigureAwait(false);

        int bridges = 0, tunnels = 0, offGround = 0;
        double worst = 0;
        foreach (var s2 in profile.Samples)
        {
            var near = network.Near(s2.E, s2.N, 20);
            if (near.Count == 0) continue;

            var edge = network.Edges[near[0].Edge];
            bool bridge = (edge.Flags & Terrain.Format.RoadFlags.Bridge) != 0;
            bool tunnel = (edge.Flags & Terrain.Format.RoadFlags.Tunnel) != 0;
            if (bridge) bridges++;
            if (tunnel) tunnels++;

            double gap = near[0].Height - s2.Height;
            if (Math.Abs(gap) > 1.5) { offGround++; worst = Math.Max(worst, Math.Abs(gap)); }
        }

        GD.Print($"[cinema] on a bridge {bridges} samples, in a tunnel {tunnels}, "
            + $"road surface more than 1.5 m off the terrain at {offGround} "
            + $"(worst {worst:F1} m)");

        Callable.From(() => GetTree().Quit(0)).CallDeferred();
    }
}
