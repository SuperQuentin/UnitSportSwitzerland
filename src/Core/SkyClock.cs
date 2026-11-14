using System;

namespace UnitSport.Core;

// Plain C# (linked into the unit tests).

/// <summary>
/// The sky's moment (#1850, docs/plans/planets.md section 2): <b>when the bodies are</b> and <b>how the Earth is turned</b>,
/// both from clocks every peer shares, so every peer has the same frame tree.
///
/// <para>
/// <b>The bodies</b> run in real time: the server's real UTC in normal play (the Moon's phase is today's, and so is
/// where it is); with a pinned date (<c>--date</c>, a check, a film) the date at the start hour plus the shared sim
/// time, the same on every run. They never jump: the sky's own date stays the calendar's while game days go by, so
/// reading the bodies from it moved them back a day at every game midnight (the Moon by ~13°).
/// </para>
///
/// <para>
/// <b>The Earth</b> turns with the game hour (the 24-minute day): its sidereal angle is the one the sky has today at the
/// calendar date and the game hour, moved by how far the sun has gone between that date and the bodies' time. So the
/// sun stands in the ground's sky exactly where it does today, and across game midnight the turn changes only by the
/// day's wander of the equation of time (≤ 0.1°) instead of a sidereal day's 0.99°.
/// </para>
/// </summary>
public static class SkyClock
{
    /// <summary>The moment the game's clocks give: set by the world's clock (World/WorldClock); real time while nothing has set it.</summary>
    public static Func<SkyMoment>? Live { get; set; }

    /// <summary>The sky now: <see cref="Live"/>, or the real sky.</summary>
    public static SkyMoment Now => Live?.Invoke() ?? SkyMoment.At(DateTime.UtcNow);

    /// <summary>
    /// When the bodies are: the server's real UTC (<paramref name="serverUnixSeconds"/>), or with a pinned date that date's
    /// <paramref name="startHour"/> (Swiss civil time) plus <paramref name="simSeconds"/> of shared sim time.
    /// </summary>
    public static DateTime BodiesUtc(DateOnly? pinned, double startHour, double simSeconds, double serverUnixSeconds) =>
        pinned is { } d
            ? SwissTime.ToUtc(d, startHour).AddSeconds(simSeconds)
            : DateTime.UnixEpoch.AddSeconds(serverUnixSeconds);

    /// <summary>
    /// The moment: the bodies at <paramref name="bodiesUtc"/>, the Earth turned so the sun stands where the ground's sky
    /// has it at <paramref name="date"/> and the game <paramref name="hour"/> (Swiss civil time).
    /// </summary>
    public static SkyMoment Moment(DateTime bodiesUtc, DateOnly date, double hour)
    {
        var groundUtc = SwissTime.ToUtc(date, hour);
        double gast = Ephemeris.SiderealTime(groundUtc);
        double sunThen = Ephemeris.Equatorial(SkyBody.Sun, groundUtc).Ra;
        double sunNow = Ephemeris.Equatorial(SkyBody.Sun, bodiesUtc).Ra;
        return new SkyMoment(bodiesUtc, Math.IEEERemainder(gast + (sunNow - sunThen), 2 * Math.PI));
    }
}
