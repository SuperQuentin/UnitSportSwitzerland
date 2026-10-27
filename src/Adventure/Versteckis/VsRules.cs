using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>
/// The rules of Versteckis (#1268, <c>docs/plans/adventure-versteckis.md</c>), pure: the phases' lengths by head
/// count, the seekers' rotation, the tools' charges and cooldowns, the tag's geometry, the server's checks on a
/// hider's published position, the points and the payout. The server applies them, the HUD shows them, the unit
/// tests pin them.
/// </summary>
public static class VsRules
{
    // ---- games and areas ----
    /// <summary>Games a server runs at once, and how far apart their areas must lie, m.</summary>
    public const int MaxGames = 4;
    public const double AreaSpacing = 600;
    public const int MinPlayers = 2, MinPlayersNoBots = 4, MaxPlayers = 8;
    /// <summary>Bots top a game up to this many players unless the opener says otherwise.</summary>
    public const int DefaultBots = 4;

    // ---- phases, game seconds ----
    public const double LobbySeconds = 90, GatherSeconds = 5, RoundEndSeconds = 12, ResultsSeconds = 20;

    /// <summary>Hide: 45 s, 35 s at four players or fewer.</summary>
    public static double HideSeconds(int players) => players <= 4 ? 35 : 45;

    /// <summary>Seek: 150 s and 25 s a hider, 300 s at most (3 hiders: 225 s).</summary>
    public static double SeekSeconds(int hiders) => Math.Min(300, 150 + 25 * Math.Max(0, hiders));

    /// <summary>The rounds of a game: one per human, 3 to 6; the opener may set 2 to 8.</summary>
    public static int RoundsFor(int humans, int? asked = null) =>
        asked is int a ? Math.Clamp(a, 2, 8) : Math.Clamp(humans, 3, 6);

    /// <summary>
    /// Who seeks each round: every human once, in join order, before anyone seeks twice; the rounds left go to a
    /// bot (in turn); with one human, it seeks round 1 and a bot the rest. With no bot, the humans seek again in
    /// order. <paramref name="humans"/> in join order.
    /// </summary>
    public static List<long> SeekerOrder(IReadOnlyList<long> humans, IReadOnlyList<long> bots, int rounds)
    {
        var order = new List<long>(rounds);
        for (int r = 0; r < rounds; r++)
        {
            if (r < humans.Count) order.Add(humans[r]);
            else if (bots.Count > 0) order.Add(bots[(r - humans.Count) % bots.Count]);
            else if (humans.Count > 0) order.Add(humans[r % humans.Count]);
        }
        return order;
    }

    // ---- the area ----
    public const float MinRadius = 130, MaxRadius = 220;

    /// <summary>The area's radius for a head count: 100 + 15 a player, 130 to 220 m (4 players: 160 m, 8: 220 m).</summary>
    public static float Radius(int players) => Math.Clamp(100f + 15f * players, MinRadius, MaxRadius);

    /// <summary>The HUD warns within this of the line, m.</summary>
    public const float WarnInside = 10f;
    /// <summary>A hider outside the line is revealed after this, caught after <see cref="OutCaughtSeconds"/>.</summary>
    public const double OutRevealSeconds = 3, OutCaughtSeconds = 10;

    // ---- the seeker ----
    /// <summary>In Hide the seeker stays within this of the church door; beyond it the server puts it back.</summary>
    public const float DoorHold = 3f;
    /// <summary>A tag reaches this far from the seeker's body, m; a hider's footprint within <see cref="TagSlack"/> of the point is caught.</summary>
    public const float TagReach = 2.5f, TagSlack = 0.3f;
    public const double TagCooldown = 1.5, MissCooldown = 4;
    public const int SniffCharges = 3;
    public const double SniffRecharge = 60, SniffSeconds = 3;
    public const float SniffRadius = 30f, SniffOffset = 4f;

    // ---- the hiders ----
    public const double DecoySeconds = 60;
    public const double SprintSeconds = 2, SprintRecharge = 30;
    public const float SprintSpeed = 6.5f, SprintHeard = 25f;
    public const double MooCooldown = 20, MooQuietEnd = 20;
    public const int MooMax = 5;
    public const float MooHeard = 60f;
    /// <summary>In Seek a hider may change prop once in this, with a poof.</summary>
    public const double ChangeEvery = 20;
    /// <summary>A disguise comes back this long after an objective's hold ends.</summary>
    public const double DisguiseBack = 6;
    /// <summary>A Use on a decor prop within this copies it.</summary>
    public const float CopyReach = 4f;

    // ---- objectives ----
    public const float SiteReach = 2.5f;
    public const double BellHold = 3, FireHold = 4;
    public const float CanCarryMax = 120f;

    /// <summary>An objective's points: its doer's and every other hider's still hiding.</summary>
    public static (int Doer, int Each) ObjectivePoints(VsSite site) => site switch
    {
        VsSite.Bell => (3, 1),
        VsSite.Fire => (3, 1),
        _ => (4, 1),
    };

    /// <summary>All three done: each hider still hidden gets this.</summary>
    public const int AwakeBonus = 3;

    public static double HoldSeconds(VsSite site) => site == VsSite.Fire ? FireHold : BellHold;

    // ---- points ----
    public const int CatchPoints = 3, AllCaughtBonus = 2, SurvivePoints = 3;
    public const double HiddenPointEvery = 30, LeftPointEvery = 20;

    /// <summary>A hider's points for the time hidden: one a 30 s.</summary>
    public static int HiddenPoints(double seconds) => seconds <= 0 ? 0 : (int)(seconds / HiddenPointEvery);

    /// <summary>The seeker's bonus when every hider is caught with <paramref name="secondsLeft"/> to go: 1 a 20 s left, +2.</summary>
    public static int AllCaughtPoints(double secondsLeft) => AllCaughtBonus + (int)(Math.Max(0, secondsLeft) / LeftPointEvery);

    /// <summary>A Moo scores when its cooldown is over, under the round's cap, and not in the round's last 20 s.</summary>
    public static bool MooScores(int moos, double secondsLeft) => moos < MooMax && secondsLeft > MooQuietEnd;

    /// <summary>The game's winners: the highest total; ties share.</summary>
    public static List<long> Winners(IEnumerable<VsEntrant> entrants)
    {
        var list = entrants.ToList();
        if (list.Count == 0) return new();
        int top = list.Max(e => e.Score);
        return list.Where(e => e.Score == top).Select(e => e.Peer).ToList();
    }

    // ---- the tag ----
    /// <summary>
    /// Whether a tag at (<paramref name="e"/>, <paramref name="n"/>) catches a body disguised as <paramref name="prop"/>
    /// standing at (<paramref name="he"/>, <paramref name="hn"/>), facing <paramref name="yaw"/> (radians, the body's
    /// <c>NetYaw</c>: 0 faces north, positive turns to the west): the point within <see cref="TagSlack"/> of its
    /// footprint box, in the box's own frame. Height is not judged: a tag aims at the ground or the prop.
    /// </summary>
    public static bool TagHits(VsProp prop, double he, double hn, float yaw, double e, double n)
    {
        var info = VsProps.Of(prop);
        double dx = e - he, dn = n - hn;
        // the box's axes: forward (−Z in Godot) is north turned by yaw; right is east turned by yaw
        double c = Math.Cos(yaw), s = Math.Sin(yaw);
        double right = dx * c + dn * s;     // along the body's right (east at yaw 0)
        double fwd = -dx * s + dn * c;      // along the body's facing (north at yaw 0)
        return Math.Abs(right) <= info.Width / 2 + TagSlack && Math.Abs(fwd) <= info.Depth / 2 + TagSlack;
    }

    /// <summary>A tag point within reach of the seeker's body (server positions), 2D, with the slack the client's aim needs.</summary>
    public static bool InReach(double se, double sn, double e, double n) =>
        (e - se) * (e - se) + (n - sn) * (n - sn) <= (TagReach + 0.5) * (TagReach + 0.5);

    // ---- the server's checks (plan "Network") ----
    /// <summary>Slack on the walk cap for the network's jitter.</summary>
    public const float SpeedSlack = 1.3f;
    /// <summary>A hider's published body is checked this often, s.</summary>
    public const double CheckEvery = 0.25;
    /// <summary>Within this of the server's coarse ground, m.</summary>
    public const float GroundSlack = 2f;

    /// <summary>
    /// A hider's two published positions <paramref name="dt"/> apart: within the walk cap (a sprint's burst when
    /// <paramref name="sprinting"/>), with slack. A teleport the server made itself (Gather) is not checked.
    /// </summary>
    public static bool SpeedOk(double e0, double n0, double e1, double n1, double dt, float cap, bool sprinting)
    {
        if (dt <= 0) return true;
        double d = Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0));
        double top = (sprinting ? SprintSpeed : cap) * SpeedSlack;
        // a small step (a nudge, the network's jitter) always passes: 0.6 m whatever the interval
        return d <= Math.Max(0.6, top * dt);
    }

    /// <summary>Inside the area's circle.</summary>
    public static bool Inside(double ce, double cn, float radius, double e, double n) =>
        (e - ce) * (e - ce) + (n - cn) * (n - cn) <= (double)radius * radius;

    /// <summary>Metres from the line, positive inside.</summary>
    public static double ToLine(double ce, double cn, float radius, double e, double n) =>
        radius - Math.Sqrt((e - ce) * (e - ce) + (n - cn) * (n - cn));

    /// <summary>On the ground: within <see cref="GroundSlack"/> of the coarse height (unknown ground passes).</summary>
    public static bool GroundOk(float alt, float? ground) => ground is not float g || Math.Abs(alt - g) <= GroundSlack + 1.2f;

    // ---- the payout (plan 5) ----
    public const int PayBase = 20, PayPerRound = 6, PayTop = 10, PaidGamesADay = 5;

    /// <summary>
    /// A human's francs for a game: 20 + 6 a round played, +10 for a top scorer; nothing once five games were paid
    /// today. Being the host, in a party or winning changes nothing else: every human who played is paid the same way.
    /// </summary>
    public static int Payout(int roundsPlayed, bool top, int paidToday) =>
        paidToday >= PaidGamesADay || roundsPlayed <= 0 ? 0 : PayBase + PayPerRound * roundsPlayed + (top ? PayTop : 0);
}
