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
    public double Duration => Runners.Count == 0 ? 1 : Runners.Max(r => r.Track.Duration);

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
