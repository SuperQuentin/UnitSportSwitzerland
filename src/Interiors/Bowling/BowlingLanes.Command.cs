using System.Globalization;

namespace UnitSport.Interiors;

// /bowl (#1323): the game's actions from the chat, the same Serve* paths a player's keys and a bot take.
// For a playtest, a scripted shot queue and a check: sign in, take a ball, roll by numbers.
public partial class BowlingLanes
{
    public const string CommandUsage =
        "Usage: /bowl signin | /bowl ball [colour 0-7] [pounds 6-16] | /bowl roll <x> <angle> <m/s> <revs> <axis> | /bowl bot | /bowl score";

    /// <summary>
    /// <c>/bowl</c> for <paramref name="peer"/> in centre <paramref name="key"/> (the one they are in): what to
    /// tell them. Runs on the server (or offline), as the Serve* actions do.
    /// </summary>
    public string Command(long peer, string rest, string? key, string name)
    {
        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0) return CommandUsage;
        if (key is not { Length: > 0 } || InteriorManager.Instance?.Planned(key) is not { Type: BuildingType.Bowling })
            return "You are not in a bowling centre.";
        float F(int i, float or) => i < args.Length && float.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : or;
        switch (args[0].ToLowerInvariant())
        {
            case "signin":
                return ServeSignIn(peer, key, name)
                    ? SignedIn(peer) is { } at ? $"Signed in: lane pair {at.Pair}, lane {at.Lane + 1}. Take a ball from a rack." : "Signed out."
                    : "Not signed in.";
            case "ball":
                return ServeTakeBall(peer, key, (int)F(1, 2), (int)F(2, 12)) ? "Ball in hand." : "No ball.";
            case "roll":
            {
                if (args.Length < 6) return CommandUsage;
                var release = new BowlingBall.Release(F(1, 0), F(2, 0), F(3, 8), F(4, 30), F(5, 0));
                if (SignedIn(peer) is { } me && me.Key == key && peer == MyId)
                {
                    // the local player's roll shows at once, as the keys' would
                    AskRelease(key, release);
                    return "Rolled.";
                }
                return ServeRelease(peer, key, release.Pack(), Now) ? "Rolled." : "Not rolled.";
            }
            case "bot":
                return ServeAddBot(peer, key) ? "A bot partner joins your game." : "No bot added.";
            case "score":
            {
                if (SignedIn(peer) is not { } me) return "Not signed in.";
                var lane = PairOf(me.Key, me.Pair)[me.Lane];
                return string.Join(" | ", lane.Bowlers.Select(b =>
                    $"{b.Name}: {string.Join(" ", BowlingScore.Frames(b.Rolls).Where(f => f.Marks.Length > 0).Select(f => string.Concat(f.Marks)))} = {BowlingScore.Total(b.Rolls)}"));
            }
            default:
                return CommandUsage;
        }
    }
}
