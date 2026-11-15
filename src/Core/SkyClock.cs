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
///
/// <para>
/// <b>Every other body takes the same short day where one stands on it</b> (#1992): the body under this peer
/// (<see cref="Local"/>) is turned on by the Earth's extra turn (<see cref="ExtraTurn"/>: the game's turn less the true
/// one), so its sun, stars and planets cross its sky about once a game day (the Moon's 24.4 minutes, Mars' 24.0, at the
/// 24-minute day), and a moon's parent hangs still (<see cref="SkyMoment"/>). With <c>/time speed real</c> the extra
/// turn stops growing: each body turns at its true rate (the Moon's 29.5 days), offset by the angle the game's hour is
/// off the real one, the Earth's own offset. Fading out toward the body's sphere of influence's edge
/// (<see cref="LocalWeight"/>), so a crossing re-expresses positions in the true frame tree.
/// </para>
/// </summary>
public static class SkyClock
{
    /// <summary>The moment the game's clocks give: set by the world's clock (World/WorldClock); real time while nothing has set it.</summary>
    public static Func<SkyMoment>? Live { get; set; }

    /// <summary>The body this peer stands on (or flies near) and how much of the local day it takes (#1992); null: none (the server).</summary>
    public sealed record LocalDay(BodyId Body, double Weight);

    /// <summary>Set each frame by the client's body views (World/Bodies/BodyViews); swapped whole, read on any thread.</summary>
    public static volatile LocalDay? Local;

    /// <summary>The local day full within this share of the body's sphere of influence, gone past <see cref="LocalFadeOut"/> (#1992).</summary>
    public const double LocalFadeIn = 0.15, LocalFadeOut = 0.6;

    /// <summary>
    /// How much of the local day a body's sky takes with the camera <paramref name="distance"/> metres from its centre
    /// (#1992): all of it near (the Moon: within 9,900 km), none from 60 % of its sphere of influence out (39,500 km), smooth
    /// between. The Earth and the Sun take none (the Earth's day is its own turn; nothing stands on the Sun).
    /// </summary>
    public static double LocalWeight(BodyId body, double distance)
    {
        if (body is BodyId.Earth or BodyId.Sun) return 0;
        double soi = CelestialBody.Of(body).SphereOfInfluence;
        double t = Math.Clamp((distance / soi - LocalFadeIn) / (LocalFadeOut - LocalFadeIn), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }

    /// <summary>
    /// How far the game's Earth is turned past the true one at the bodies' time (#1992), radians in (−π, π]: what the
    /// 24-minute day adds. Grows ~59 turns a real day at the normal day, stands still at <c>/time speed real</c>.
    /// </summary>
    public static double ExtraTurn(DateTime bodiesUtc, double earthAngle) =>
        Math.IEEERemainder(earthAngle - Ephemeris.SiderealTime(bodiesUtc), 2 * Math.PI);

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
        double earth = Math.IEEERemainder(gast + (sunNow - sunThen), 2 * Math.PI);
        return WithLocal(new SkyMoment(bodiesUtc, earth), Local);
    }

    /// <summary><paramref name="m"/> with the local day of <paramref name="local"/> (#1992): its body turned by its weight of the extra turn.</summary>
    public static SkyMoment WithLocal(SkyMoment m, LocalDay? local) =>
        local is { Weight: > 0 } l && l.Body is not (BodyId.Earth or BodyId.Sun)
            ? m with { Local = l.Body, LocalTurn = l.Weight * ExtraTurn(m.Utc, m.EarthAngle) }
            : m;
}
