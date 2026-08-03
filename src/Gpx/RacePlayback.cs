using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// The race clock. Every runner shares one time value, so loading several GPX files makes
/// them start together and race as ghosts of each other.
///
/// Alignment is by elapsed time, not by wall-clock date: tracks recorded months apart are
/// still meaningfully compared, which is what "ghost" means here.
/// </summary>
public partial class RacePlayback : Node3D
{
    private ChunkManager _chunks = null!;
    private WorldOrigin _origin = null!;

    public List<Runner> Runners { get; } = new();
    public double Time { get; private set; }
    public double Speed { get; set; } = 1.0;
    public bool Playing { get; set; } = true;

    /// <summary>Runner the camera and headline stats follow.</summary>
    public int FocusIndex { get; private set; }

    public Runner? Focused => Runners.Count == 0 ? null : Runners[Math.Clamp(FocusIndex, 0, Runners.Count - 1)];

    /// <summary>The race lasts as long as its slowest entrant.</summary>
    public double Duration => Runners.Count == 0 ? 1 : Runners.Max(r => r.Active.Duration);

    // ---- snap to roads -------------------------------------------------------------------

    /// <summary>Whether playback follows the road-matched variant of each track.</summary>
    public bool SnapToRoads { get; private set; }

    /// <summary>Short line for the HUD: what matching did, or why it could not.</summary>
    public string SnapStatus { get; private set; } = "";

    /// <summary>True while matching is running, so the button can say so rather than look dead.</summary>
    public bool Matching { get; private set; }

    private CancellationTokenSource? _matchCancel;

    /// <summary>
    /// Turns road snapping on or off, matching any track that has not been matched yet.
    ///
    /// <para>
    /// Matching reads road tiles and runs a Viterbi pass over thousands of fixes, so it happens
    /// off the main thread and the result is applied back on it. Turning the toggle off is
    /// instant and keeps the result — the two variants sit side by side on each runner, which is
    /// what makes flipping between them a fair comparison rather than a reload.
    /// </para>
    /// </summary>
    public void SetSnapToRoads(bool enabled)
    {
        if (enabled == SnapToRoads) return;
        SnapToRoads = enabled;

        if (!enabled)
        {
            foreach (var r in Runners) r.UseSnapped = false;
            SnapStatus = "";
            Seek(Time);
            return;
        }

        foreach (var r in Runners) r.UseSnapped = true;
        Seek(Time);

        var pending = Runners.Where(r => r.Snapped == null).ToList();
        if (pending.Count == 0)
        {
            SnapStatus = "on";
            return;
        }

        var source = _chunks.Source;
        if (source == null)
        {
            SnapStatus = "no terrain source";
            return;
        }

        _matchCancel?.Cancel();
        _matchCancel = new CancellationTokenSource();
        Matching = true;
        SnapStatus = "matching…";
        _ = MatchAllAsync(pending, source, _matchCancel.Token);
    }

    private async Task MatchAllAsync(List<Runner> runners, Terrain.IChunkSource source,
        CancellationToken ct)
    {
        int ok = 0;
        string lastProblem = "";

        foreach (var runner in runners)
        {
            MatchResult result;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                result = await TrackMatcher.MatchAsync(runner.Track, source, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                result = new MatchResult(null, e.Message, 0);
            }

            if (ct.IsCancellationRequested) return;

            if (result.Ok)
            {
                ok++;
                double shrink = runner.Track.Length > 0
                    ? 1 - result.Track!.Length / runner.Track.Length : 0;
                GD.Print($"[gpx] matched {runner.Track.Name}: {result.MatchedShare:P0} near a road, "
                    + $"length {runner.Track.Length / 1000:F2} -> {result.Track!.Length / 1000:F2} km "
                    + $"({shrink:P1} shorter), moved mean {result.MeanOffset:F1} m / "
                    + $"p95 {result.P95Offset:F1} m, {result.Jumps} road hops, "
                    + $"{clock.ElapsedMilliseconds} ms");
            }
            else
            {
                lastProblem = result.Status;
                GD.Print($"[gpx] could not match {runner.Track.Name}: {result.Status}");
            }

            // back to the main thread to touch the scene: a Runner is a live node
            var matched = result.Track;
            var target = runner;
            Callable.From(() => Apply(target, matched)).CallDeferred();
        }

        int total = runners.Count;
        string summary = ok == total ? "on"
            : ok == 0 ? lastProblem
            : $"{ok}/{total}";

        Callable.From(() => Finish(summary)).CallDeferred();
    }

    private void Apply(Runner runner, GpxTrack? matched)
    {
        if (!IsInstanceValid(runner)) return;
        runner.Snapped = matched;
        runner.UseSnapped = SnapToRoads;
        runner.UpdateTo(Time, Speed, 0);
    }

    private void Finish(string summary)
    {
        Matching = false;
        SnapStatus = summary;
        Seek(Time);
        SnapChanged?.Invoke();
    }

    /// <summary>Raised when matching completes, so the HUD can stop saying "matching".</summary>
    public event Action? SnapChanged;

    public override void _ExitTree() => _matchCancel?.Cancel();

    /// <summary>True once the clock has reached the finish and stopped there.</summary>
    public bool Complete => Runners.Count > 0 && Time >= Duration;

    public static RacePlayback Create(ChunkManager chunks, WorldOrigin origin) => new()
    {
        Name = "RacePlayback",
        _chunks = chunks,
        _origin = origin,
    };

    public Runner Add(GpxTrack track)
    {
        var runner = Runner.Create(track, _chunks, _origin, Runners.Count);
        Runners.Add(runner);
        AddChild(runner);

        // a ghost added while snapping is on gets matched too, rather than silently
        // running the raw track alongside snapped ones
        if (SnapToRoads)
        {
            SnapToRoads = false;
            SetSnapToRoads(true);
        }
        // place immediately so it appears without waiting for the next frame
        runner.CallDeferred(nameof(Runner.UpdateTo), Time, Speed, 0.0);
        return runner;
    }

    public void Clear()
    {
        foreach (var r in Runners) r.QueueFree();
        Runners.Clear();
        FocusIndex = 0;
        Time = 0;
    }

    public void CycleFocus()
    {
        if (Runners.Count > 0) FocusIndex = (FocusIndex + 1) % Runners.Count;
    }

    public void Seek(double seconds)
    {
        Time = Math.Clamp(seconds, 0, Duration);
        foreach (var r in Runners) r.UpdateTo(Time, Speed, 0);
    }

    public void TogglePlay() => Playing = !Playing;

    /// <summary>Runners ordered by distance covered — the live standings.</summary>
    public IEnumerable<Runner> Standings => Runners.OrderByDescending(r => r.Distance);

    public override void _Process(double delta)
    {
        if (Playing && Runners.Count > 0)
        {
            Time += delta * Speed;
            if (Time >= Duration)
            {
                Time = Duration;
                Playing = false;   // hold at the finish rather than looping unasked
            }
        }

        foreach (var r in Runners) r.UpdateTo(Time, Speed, delta);
    }
}
