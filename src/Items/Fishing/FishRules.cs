namespace UnitSport.Items.Fishing;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>One fish on the bank: what it is, how big, and whether it may be kept.</summary>
public readonly record struct Catch(FishSpecies Species, float Cm, float Kg, Verdict Verdict)
{
    public bool Kept => Verdict == Verdict.Keep;
}

/// <summary>
/// Who bites where, how long it takes, how big it is and the law on it (docs/notes/items/fishing.md).
/// Pure functions of the spot, the bait, the hour and the month, so the tests pin them down.
/// </summary>
public static class FishRules
{
    /// <summary>How likely a species is to bite at all, before the place and bait.</summary>
    public static double RarityWeight(Rarity r) => r switch
    {
        Rarity.Common => 10, Rarity.Uncommon => 4, Rarity.Rare => 1.2, Rarity.VeryRare => 0.35, _ => 0.03,
    };

    /// <summary>A known fish of the lake (its catch statistics) is this much more likely there.</summary>
    public const double KnownBoost = 3;

    public static bool IsNight(double hour) => hour < 5.5 || hour >= 21;

    /// <summary>How keen a species is on this spot, bait and hour; 0 = it does not live there.</summary>
    public static double Weight(FishSpecies s, FishSpot spot, Bait bait, double hour)
    {
        if ((s.Waters & spot.Kind) == 0 || (s.Basins & spot.Basin) == 0) return 0;
        // the altitude range is soft at its ends: 100 m beyond, the odds fade to nothing
        double over = Math.Max(s.AltMin - spot.Altitude, spot.Altitude - s.AltMax);
        if (over > 100) return 0;
        double w = RarityWeight(s.Rarity) * (over > 0 ? 1 - over / 100 : 1);
        if (spot.Lake is { } lake)
        {
            if (lake.Known.Contains(s.Item)) w *= KnownBoost;
            // a lake fish the statistics never list there is scarcer
            else if (s.Waters == WaterKind.LargeLake) w *= 0.3;
        }
        w *= bait switch
        {
            Bait.Spinner => s.Hunter ? 2.5 : 0.1,
            Bait.Dough => s.Hunter ? 0.5 : 2.0,
            _ => 0.6,
        };
        if (IsNight(hour)) w *= s.Night ? 2.5 : 0.6;
        return w;
    }

    /// <summary>Every species that may bite here, with its weight, heaviest first.</summary>
    public static List<(FishSpecies Species, double Weight)> Odds(FishSpot spot, Bait bait, double hour)
    {
        var list = new List<(FishSpecies, double)>();
        foreach (var s in FishCatalog.All)
        {
            double w = Weight(s, spot, bait, hour);
            if (w > 0) list.Add((s, w));
        }
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }

    /// <summary>Draws the fish that bites, or null where nothing lives.</summary>
    public static FishSpecies? Pick(FishSpot spot, Bait bait, double hour, Random rng)
    {
        var odds = Odds(spot, bait, hour);
        double total = odds.Sum(o => o.Weight);
        if (total <= 0) return null;
        double roll = rng.NextDouble() * total;
        foreach (var (s, w) in odds)
            if ((roll -= w) < 0) return s;
        return odds[^1].Species;
    }

    /// <summary>
    /// Seconds until the next bite: an exponential wait round the mean, shorter at dawn and dusk (when fish
    /// feed), longer with no bait, on a stormy lake and where little lives.
    /// </summary>
    public static double MeanBiteSeconds(FishSpot spot, Bait bait, double hour, double seaState)
    {
        double mean = 14;
        if (bait == Bait.None) mean *= 2.5;
        bool dawn = hour >= 5 && hour < 8.5, dusk = hour >= 18 && hour < 21.5;
        if (dawn || dusk) mean *= 0.65;
        else if (hour >= 11 && hour < 15) mean *= 1.25;
        if (seaState > 0.6) mean *= 1.5;
        if (spot.Kind == WaterKind.MountainStream || spot.Kind == WaterKind.AlpineLake) mean *= 1.3;
        return mean;
    }

    public static double BiteSeconds(FishSpot spot, Bait bait, double hour, double seaState, Random rng)
    {
        double mean = MeanBiteSeconds(spot, bait, hour, seaState);
        // never at once, never for ever
        return Math.Clamp(-Math.Log(1 - rng.NextDouble()) * mean, 3, mean * 4);
    }

    /// <summary>A length in cm: mostly middling, sometimes small (a few under the limit), now and then a trophy.</summary>
    public static float Length(FishSpecies s, Random rng)
    {
        // a triangular draw over the range, its peak a third of the way up, stretched 15 % at both ends
        double u = rng.NextDouble(), peak = 1.0 / 3;
        double t = u < peak ? Math.Sqrt(u * peak) : 1 - Math.Sqrt((1 - u) * (1 - peak));
        double lo = s.CmMin * 0.85, hi = s.CmMax * 1.15;
        return (float)Math.Round(lo + (hi - lo) * t, 1);
    }

    /// <summary>The law on this fish, here, this month.</summary>
    public static Verdict Judge(FishSpecies s, float cm, int month, FishSpot spot)
    {
        if (s.Culled) return Verdict.Culled;
        if (s.Protected) return Verdict.Protected;
        if (s.ClosedIn(month)) return Verdict.ClosedSeason;
        if (s.Item == ItemId.Whitefish && spot.Lake?.WhitefishBan == true) return Verdict.Moratorium;
        if (cm < s.MinCm) return Verdict.Undersized;
        return Verdict.Keep;
    }

    public static Catch Land(FishSpecies s, FishSpot spot, int month, Random rng)
    {
        float cm = Length(s, rng);
        return new Catch(s, cm, s.KgAt(cm), Judge(s, cm, month, spot));
    }

    /// <summary>What the toast says when a fish goes back.</summary>
    public static string Why(Catch c) => c.Verdict switch
    {
        Verdict.Protected => c.Species.RedList.StartsWith("extinct")
            ? $"{c.Species.RedList}: you put it back, and nobody will believe you"
            : $"protected ({c.Species.RedList}): released",
        Verdict.ClosedSeason => $"closed season ({c.Species.ClosedText}): released",
        Verdict.Undersized => $"under {c.Species.MinCm:0} cm: released",
        Verdict.Moratorium => "whitefish ban on Lake Constance until 2027: released",
        Verdict.Culled => "invasive: killed, as the rules want, and thrown away",
        _ => "",
    };
}

/// <summary>
/// The fight once a fish is hooked: the line's tension, how much line is out, how tired the fish is.
/// Reeling pulls the tension up toward what the fish pulls; letting go eases it while a strong fish
/// takes line. Over 1 the line snaps; at the bank the fish is landed. Surges come every few seconds
/// from anything over half a kilo. Plain arithmetic, stepped by the caller (unit-tested).
/// </summary>
public sealed class FishFight
{
    public const float SnapTension = 1f, LandDistance = 1.5f, MaxLine = 45f;

    public float Distance { get; private set; }
    public float Tension { get; private set; }
    /// <summary>1 fresh, down to 0 worn out: a tired fish pulls less than half as hard.</summary>
    public float Stamina { get; private set; } = 1f;
    public bool Surging => _surgeLeft > 0;
    /// <summary>A surge is coming in <see cref="SurgeWarning"/> seconds or less: the rod bends, the pad rumbles, let go.</summary>
    public bool SurgeComing => !Surging && Pull > 0.2f && _nextSurge <= SurgeWarning;
    public const float SurgeWarning = 0.6f;
    public bool Snapped { get; private set; }
    public bool Landed { get; private set; }
    public bool Over => Snapped || Landed;

    /// <summary>The fish's pull, 0.1 (a minnow) to 0.95 (a wels), from its weight.</summary>
    public readonly float Pull;
    private readonly Random _rng;
    private float _surgeLeft, _nextSurge;

    public static float PullOf(float kg) => Math.Clamp(0.1f + 0.17f * MathF.Log(1 + 2 * kg), 0.1f, 0.95f);

    public FishFight(float kg, float distance, Random rng)
    {
        Pull = PullOf(kg);
        Distance = Math.Clamp(distance, LandDistance + 0.5f, MaxLine);
        _rng = rng;
        _nextSurge = 1.5f + (float)rng.NextDouble() * 2;
    }

    /// <summary>The pull right now: stamina and any surge included.</summary>
    public float Effort => Pull * (0.4f + 0.6f * Stamina) * (Surging ? 1.6f : 1f);

    public void Step(float dt, bool reeling)
    {
        if (Over || dt <= 0) return;
        if (Pull > 0.2f)
        {
            if (_surgeLeft > 0) _surgeLeft -= dt;
            else if ((_nextSurge -= dt) <= 0)
            {
                _surgeLeft = 1.2f;
                _nextSurge = 2f + (float)_rng.NextDouble() * 3f;
            }
        }
        float effort = Effort;
        float target = reeling ? effort * 1.2f : effort * 0.35f;
        Tension += (target - Tension) * Math.Min(1f, 1.5f * dt);
        // a fish fighting a tight line tires; a slack one gets its breath back
        Stamina = Math.Clamp(Stamina - dt * (Tension > 0.3f ? 0.07f * Tension : -0.02f), 0f, 1f);
        if (reeling) Distance -= 2.5f * (1 - 0.5f * effort) * dt;
        else if (effort > 0.3f) Distance += 2f * (effort - 0.3f) * dt;

        if (Tension > SnapTension || Distance > MaxLine) Snapped = true;
        else if (Distance <= LandDistance) Landed = true;
    }
}
