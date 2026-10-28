namespace UnitSport.Interiors;

/// <summary>
/// Ten-pin scoring (#1323): a game is the list of the pins each ball knocked down, in order, and
/// everything else is a pure function of it. A strike scores ten plus the next two balls, a spare ten
/// plus the next one; the tenth frame gives a third ball after a strike or a spare. A frame whose bonus
/// balls are not bowled yet has no total yet.
/// </summary>
public static class BowlingScore
{
    /// <summary>One frame on the score screen: what each ball shows (X, /, −, a digit) and the running total.</summary>
    public readonly record struct Frame(string[] Marks, int? Total);

    /// <summary>Which frame (0-9) and which ball in it (0-2) comes next; frame 10 when the game is over.</summary>
    public static (int Frame, int Ball) Next(IReadOnlyList<int> rolls)
    {
        int i = 0;
        for (int f = 0; f < 9; f++)
        {
            if (i >= rolls.Count) return (f, 0);
            if (rolls[i] == 10) { i++; continue; }
            if (i + 1 >= rolls.Count) return (f, 1);
            i += 2;
        }
        int left = rolls.Count - i;
        if (left <= 0) return (9, 0);
        if (left == 1) return (9, 1);
        if (left == 2) return rolls[i] == 10 || rolls[i] + rolls[i + 1] == 10 ? (9, 2) : (10, 0);
        return (10, 0);
    }

    public static bool Over(IReadOnlyList<int> rolls) => Next(rolls).Frame >= 10;

    /// <summary>
    /// Whether the next ball is bowled at a full rack: the first of a frame, or in the tenth after a
    /// strike or a spare. The pinsetter resets before it; otherwise it picks the standing pins.
    /// </summary>
    public static bool FreshRack(IReadOnlyList<int> rolls)
    {
        var (frame, ball) = Next(rolls);
        if (ball == 0 || frame >= 10) return true;
        int last = rolls[^1];
        if (frame < 9) return false;
        // the tenth: after a strike, or a spare on the second ball, the rack is full again
        if (ball == 1) return last == 10;
        int prev = rolls[^2];
        return last == 10 || prev != 10 && prev + last == 10;
    }

    /// <summary>The pins still standing before the next ball: what it can knock down at most.</summary>
    public static int Standing(IReadOnlyList<int> rolls) => FreshRack(rolls) ? 10 : 10 - rolls[^1];

    /// <summary>The ten frames as a score screen shows them (blank where not yet bowled).</summary>
    public static Frame[] Frames(IReadOnlyList<int> rolls)
    {
        var frames = new Frame[10];
        int i = 0, total = 0;
        bool known = true;
        for (int f = 0; f < 10; f++)
        {
            if (i >= rolls.Count) { frames[f] = new Frame(Array.Empty<string>(), null); known = false; continue; }
            int? Bonus(int at, int count)
            {
                if (at + count > rolls.Count) return null;
                int sum = 0;
                for (int k = 0; k < count; k++) sum += rolls[at + k];
                return sum;
            }
            int? score;
            string[] marks;
            if (f < 9)
            {
                if (rolls[i] == 10)
                {
                    marks = new[] { "X" };
                    score = Bonus(i + 1, 2) is { } b ? 10 + b : null;
                    i += 1;
                }
                else if (i + 1 < rolls.Count)
                {
                    bool spare = rolls[i] + rolls[i + 1] == 10;
                    marks = new[] { Mark(rolls[i]), spare ? "/" : Mark(rolls[i + 1]) };
                    score = spare ? Bonus(i + 2, 1) is { } b ? 10 + b : null : rolls[i] + rolls[i + 1];
                    i += 2;
                }
                else
                {
                    marks = new[] { Mark(rolls[i]) };
                    score = null;
                    i += 1;
                }
            }
            else
            {
                var tenth = new List<string>();
                int sum = 0, n = rolls.Count - i;
                for (int k = 0; k < n && k < 3; k++)
                {
                    int r = rolls[i + k];
                    sum += r;
                    // a slash after a ball that left pins, an X for every full rack knocked down
                    bool afterFresh = k == 0 || rolls[i + k - 1] == 10 || k == 2 && rolls[i] != 10 && rolls[i] + rolls[i + 1] == 10;
                    tenth.Add(afterFresh ? r == 10 ? "X" : Mark(r) : rolls[i + k - 1] + r == 10 ? "/" : Mark(r));
                }
                marks = tenth.ToArray();
                score = Over(rolls) ? sum : null;
                i = rolls.Count;
            }
            if (score is { } s && known) { total += s; frames[f] = new Frame(marks, total); }
            else { known = false; frames[f] = new Frame(marks, null); }
        }
        return frames;
    }

    /// <summary>The game's total so far: the last frame with a known total.</summary>
    public static int Total(IReadOnlyList<int> rolls)
    {
        int total = 0;
        foreach (var f in Frames(rolls))
            if (f.Total is { } t) total = t;
        return total;
    }

    static string Mark(int pins) => pins == 0 ? "-" : pins.ToString();
}
