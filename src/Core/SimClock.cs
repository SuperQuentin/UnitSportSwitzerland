using System;
using System.Globalization;

namespace UnitSport.Core;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// Simulation time: the pace of everything that follows physics, owned by the server (#579).
///
/// <para>
/// The server owns three numbers — <see cref="Sim0"/> at <see cref="Epoch"/> on its own clock
/// (<c>Net.ClockSync.ServerNow</c>), running at <see cref="Scale"/> — and every peer turns them
/// into "the simulated time now". Nothing is summed frame by frame, so nothing drifts, a late
/// joiner lands on the same value, and the trip over the wire does not move it. The same shape as
/// <c>World.WorldClock</c> (#452), one layer down: environment time is derived from this, so a
/// change here carries the sun and the traffic lights with it and the world stays consistent.
/// </para>
///
/// <para>
/// A change is <see cref="Schedule"/>d at a server instant rather than applied on receipt, and
/// <see cref="Tick"/> rebases <em>at that instant</em> whatever frame a peer happens to notice on.
/// Two peers that agree on the server's clock therefore agree on <see cref="SimAt"/> across the
/// change, instead of rubber-banding for the width of the RPC spread.
/// </para>
///
/// <para>
/// <see cref="SimAt"/> is authoritative-by-formula and comparable between peers; use it for
/// anything whose timing crosses the wire. <see cref="GameClock.Now"/> is this peer's *actual*
/// physics progress and falls behind on a machine dropping frames; use it for purely local timers,
/// because it matches what the physics really did. Static, because a process has one simulation.
/// </para>
/// </summary>
public static class SimClock
{
    /// <summary>Normal speed.</summary>
    public const double Normal = 1.0;

    /// <summary>
    /// Slowest and fastest the simulation may run. Zero is deliberately out of range: freezing the
    /// simulation would freeze environment time, growth and the day with it, and a real pause needs
    /// its own answers for input, UI and network keepalive. Stopping the day is
    /// <c>/time speed 0</c>, which leaves the simulation running.
    /// </summary>
    public const double MinScale = 0.05, MaxScale = 8.0;

    /// <summary>Simulated seconds at <see cref="Epoch"/>.</summary>
    public static double Sim0 { get; private set; }

    /// <summary>When <see cref="Sim0"/> held, in <c>Net.ClockSync.ServerNow</c> seconds.</summary>
    public static double Epoch { get; private set; }

    /// <summary>Simulated seconds per second of the server's clock.</summary>
    public static double Scale { get; private set; } = Normal;

    /// <summary>A change is waiting for the server's clock to reach <see cref="PendingAt"/>.</summary>
    public static bool HasPending => !double.IsNaN(_pendingAt);

    /// <summary>The server instant a scheduled change takes effect; NaN when none is waiting.</summary>
    public static double PendingAt => _pendingAt;

    /// <summary>The scale a scheduled change will move to.</summary>
    public static double PendingScale => _pendingScale;

    private static double _pendingScale = Normal;
    private static double _pendingAt = double.NaN;

    /// <summary>Simulated seconds at this moment of the server's clock. Pure.</summary>
    public static double SimAt(double serverNow) => Sim0 + (serverNow - Epoch) * Scale;

    /// <summary>Takes the clock as given, by the server or from a join.</summary>
    public static void Set(double sim0, double epoch, double scale)
    {
        Sim0 = sim0;
        Epoch = epoch;
        Scale = Clamp(scale);
    }

    /// <summary>
    /// From <paramref name="serverNow"/> on the simulation runs at <paramref name="scale"/>, and
    /// <see cref="SimAt"/> is continuous across the change: the simulated time already elapsed is
    /// carried into <see cref="Sim0"/> before the new rate applies.
    /// </summary>
    public static void Rebase(double serverNow, double scale) => Set(SimAt(serverNow), serverNow, scale);

    /// <summary>
    /// Server, and every peer it tells: at <paramref name="applyAt"/> on the server's clock the
    /// scale becomes <paramref name="scale"/>. Scheduling it ahead is what lets every peer change
    /// at the same instant.
    /// </summary>
    public static void Schedule(double scale, double applyAt)
    {
        _pendingScale = Clamp(scale);
        _pendingAt = applyAt;
    }

    /// <summary>
    /// Promotes a scheduled change once the server's clock has reached it. True when the scale just
    /// changed, so the caller can hand the new value to the engine.
    ///
    /// <para>
    /// It rebases at <see cref="PendingAt"/>, never at <paramref name="serverNow"/>: a peer that
    /// notices three frames late must still land on the same <see cref="Sim0"/> and
    /// <see cref="Epoch"/> as one that noticed immediately, or the two would disagree about
    /// simulated time from then on.
    /// </para>
    /// </summary>
    public static bool Tick(double serverNow)
    {
        if (double.IsNaN(_pendingAt) || serverNow < _pendingAt) return false;
        Rebase(_pendingAt, _pendingScale);
        _pendingAt = double.NaN;
        return true;
    }

    /// <summary>Back to normal speed, nothing scheduled: leaving a server, or a fresh process.</summary>
    public static void Reset()
    {
        Sim0 = 0;
        Epoch = 0;
        Scale = Normal;
        _pendingScale = Normal;
        _pendingAt = double.NaN;
    }

    public static double Clamp(double scale) =>
        !double.IsFinite(scale) ? Normal : Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>Parses the argument of <c>/speed</c>. Invariant culture: the French locale would read "1.5" as 15.</summary>
    public static bool TryParse(string text, out double scale, out string error)
    {
        scale = Normal;
        error = "";
        text = text.Trim().ToLowerInvariant();
        if (text is "normal" or "reset") return true;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale) || !double.IsFinite(scale))
        {
            error = $"'{text}' is not a speed. {Usage}";
            return false;
        }
        if (scale < MinScale || scale > MaxScale)
        {
            error = scale == 0
                ? "0 would stop the world's clock and everything growing in it. To stop the day only, use /time speed 0."
                : $"Simulation speed runs from {Fmt(MinScale)} to {Fmt(MaxScale)}.";
            return false;
        }
        return true;
    }

    public const string Usage = "Usage: /speed [<multiplier, 0.05 to 8> | normal]";

    /// <summary>"quarter speed" / "normal speed" / "x4", for replies.</summary>
    public static string Describe(double scale) =>
        Math.Abs(scale - Normal) < 1e-9 ? "normal speed" : $"x{Fmt(scale)}";

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
