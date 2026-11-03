using Godot;

// Plain C# over Godot maths: linked into the unit tests (docs/notes/general/testing.md).

namespace UnitSport.Occasions;

/// <summary>What a UFO sighting does (#863).</summary>
public enum SightingKind : byte
{
    /// <summary>Two to four saucers cross the sky in formation, high up.</summary>
    Flyover = 0,
    /// <summary>A saucer comes down over a herd, beams a cow up, lets it down again and leaves; an escort circles.</summary>
    Abduction = 1,
    /// <summary>A saucer lands in a meadow, lamps on and ramp down, waits, then flies off. It can be boarded.</summary>
    Landing = 2,
    /// <summary>The mothership hangs high over a valley, its beam on now and then.</summary>
    Mothership = 3,
}

/// <summary>
/// One sighting (#863): planned by the authority (the server, or the game offline), sent to
/// every peer, and played back by each from the shared simulation clock, so everyone sees the same
/// saucer at the same place at the same moment. <see cref="E"/>/<see cref="N"/> (LV95) and
/// <see cref="Ground"/> (m above the sea) are the anchor: the hover point of an abduction (its cow),
/// the landing spot, the point a flyover crosses, the spot under the mothership. <see cref="Heading"/>
/// is the compass bearing they come in on, rad (0 north, clockwise).
/// </summary>
public sealed record Sighting(int Id, SightingKind Kind, double Start, float Length, double E, double N, float Ground,
    float Heading, int Count, uint Seed)
{
    public double End => Start + Length;
}

/// <summary>
/// One craft of a sighting at a moment: <see cref="Offset"/> from the anchor in world axes (X east,
/// Y up from <see cref="Sighting.Ground"/>, Z south), the Godot yaw it faces, and its look.
/// <see cref="Shown"/> is false before it arrives and after it has gone.
/// </summary>
public readonly record struct CraftPose(Vector3 Offset, float Yaw, float Hum, float Legs, float Ramp, bool Beam, bool Lights, bool Shown)
{
    public static readonly CraftPose Hidden = new(Vector3.Zero, 0f, 0f, 0f, 0f, false, false, false);
}

/// <summary>
/// The scripted paths of every kind of sighting: pure functions of the plan and the time, tested
/// in tier 0 (<c>UfoSightingTests</c>). The timings are public so the authority, the bots and the
/// boarding agree on when a saucer is low, beaming or standing on its legs.
/// </summary>
public static class SightingPath
{
    // ---- an abduction ----------------------------------------------------------------------------
    public const float AbductArrive = 18f, AbductBeamOn = 24f, AbductBeamOff = 44f, AbductLeave = 56f, AbductLength = 80f;
    /// <summary>Its hover height over the cow, m: well inside the beam's reach.</summary>
    public const float AbductHover = 24f;

    // ---- a landing ---------------------------------------------------------------------------------
    public const float LandTouch = 22f, LandRampOpen = 26f, LandRampShut = 108f, LandLift = 111f, LandLength = 130f;
    /// <summary>When the saucer standing in the meadow is a real parked UFO anyone can board, s after the start.</summary>
    public const float BoardFrom = 28f, BoardTo = 104f;

    // ---- the others --------------------------------------------------------------------------------
    public const float FlyoverLength = 60f, FlyoverHeight = 170f, FlyoverHalf = 1100f;
    public const float MothershipLength = 420f, MothershipHeight = 420f;

    public static float LengthOf(SightingKind kind) => kind switch
    {
        SightingKind.Flyover => FlyoverLength,
        SightingKind.Abduction => AbductLength,
        SightingKind.Landing => LandLength,
        _ => MothershipLength,
    };

    /// <summary>Craft <paramref name="craft"/> of <paramref name="s"/> at simulation time <paramref name="t"/>.</summary>
    public static CraftPose Pose(Sighting s, int craft, double t)
    {
        float tau = (float)(t - s.Start);
        if (tau < 0f || tau > s.Length || craft < 0 || craft >= s.Count) return CraftPose.Hidden;
        return s.Kind switch
        {
            SightingKind.Flyover => Flyover(s, craft, tau),
            SightingKind.Abduction => craft == 0 ? Abductor(s, tau) : Escort(s, craft, tau),
            SightingKind.Landing => Lander(s, tau),
            _ => Mothership(s, tau),
        };
    }

    /// <summary>The velocity of a craft, m/s, world axes (the beam carries what it holds with it).</summary>
    public static Vector3 Velocity(Sighting s, int craft, double t)
    {
        var a = Pose(s, craft, t - 0.1);
        var b = Pose(s, craft, t + 0.1);
        return a.Shown && b.Shown ? (b.Offset - a.Offset) / 0.2f : Vector3.Zero;
    }

    /// <summary>The direction a heading points in, world axes.</summary>
    public static Vector3 Dir(float heading) => new(Mathf.Sin(heading), 0f, -Mathf.Cos(heading));

    /// <summary>A beam is on under some craft of <paramref name="s"/> at <paramref name="t"/>.</summary>
    public static bool Beaming(Sighting s, double t)
    {
        for (int i = 0; i < s.Count; i++)
            if (Pose(s, i, t) is { Shown: true, Beam: true }) return true;
        return false;
    }

    /// <summary>The landed saucer may be boarded now (it is a parked UFO in the world).</summary>
    public static bool Boardable(Sighting s, double t) =>
        s.Kind == SightingKind.Landing && t - s.Start >= BoardFrom && t - s.Start <= BoardTo;

    private static float Smooth(float f) { f = Mathf.Clamp(f, 0f, 1f); return f * f * (3f - 2f * f); }

    private static float Unit(Sighting s, int a, int salt) => Farming.HerdPlan.Unit(s.Seed, (uint)a, (uint)salt);

    private static CraftPose Flyover(Sighting s, int craft, float tau)
    {
        var dir = Dir(s.Heading);
        var side = new Vector3(-dir.Z, 0f, dir.X);
        float mid = (s.Count - 1) * 0.5f;
        float lateral = (craft - mid) * 26f, back = Mathf.Abs(craft - mid) * 20f;
        float along = Mathf.Lerp(-FlyoverHalf, FlyoverHalf, tau / s.Length) - back;
        float y = FlyoverHeight + 12f * craft + Mathf.Sin(tau * 0.7f + craft * 1.9f) * 4f;
        float weave = Mathf.Sin(tau * 0.35f + craft) * 10f;
        var p = dir * along + side * (lateral + weave) + Vector3.Up * y;
        return new CraftPose(p, -s.Heading, 0.75f, 0f, 0f, false, true, true);
    }

    private static CraftPose Abductor(Sighting s, float tau)
    {
        var dir = Dir(s.Heading);
        var hover = Vector3.Up * AbductHover;
        Vector3 p;
        const float legs = 0f;
        if (tau < AbductArrive)
        {
            float f = tau / AbductArrive;
            // in along the heading, slowing to the hover, coming down from 140 m
            var from = -dir * 700f + Vector3.Up * 140f;
            p = new Vector3(from.X + (hover.X - from.X) * EaseOut(f), Mathf.Lerp(140f, AbductHover, Smooth(f)),
                from.Z + (hover.Z - from.Z) * EaseOut(f));
        }
        else if (tau < AbductLeave)
            p = hover + new Vector3(Mathf.Sin(tau * 0.6f) * 0.6f, Mathf.Sin(tau * 1.1f) * 0.4f, Mathf.Cos(tau * 0.5f) * 0.6f);
        else
        {
            float f = (tau - AbductLeave) / (s.Length - AbductLeave);
            p = hover + Vector3.Up * (280f * f * f + 40f * f) + dir * (900f * f * f);
        }
        bool beam = tau >= AbductBeamOn && tau < AbductBeamOff;
        return new CraftPose(p, -s.Heading + 0.2f * Mathf.Sin(tau * 0.3f), beam ? 0.6f : 0.8f, legs, 0f, beam, true, true);
    }

    private static CraftPose Escort(Sighting s, int craft, float tau)
    {
        // circles the abductor's spot at 45 m out and 70 m up, in and out with it
        var dir = Dir(s.Heading);
        float spin = (Unit(s, craft, 3) < 0.5f ? 1f : -1f) * 0.18f;
        float a = tau * spin + craft * Mathf.Tau / s.Count;
        var ring = new Vector3(Mathf.Cos(a) * 45f, 70f + craft * 8f, Mathf.Sin(a) * 45f);
        Vector3 p;
        if (tau < AbductArrive)
        {
            float f = EaseOut(tau / AbductArrive);
            var from = -dir * 760f + Vector3.Up * 170f + new Vector3(-dir.Z, 0, dir.X) * 40f * craft;
            p = from.Lerp(ring, f);
        }
        else if (tau < AbductLeave) p = ring;
        else
        {
            float f = (tau - AbductLeave) / (s.Length - AbductLeave);
            p = ring + Vector3.Up * (260f * f * f) + dir * (900f * f * f);
        }
        var tangent = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a)) * Mathf.Sign(spin);
        float yaw = tau < AbductArrive || tau >= AbductLeave ? -s.Heading : Mathf.Atan2(-tangent.X, -tangent.Z);
        return new CraftPose(p, yaw, 0.7f, 0f, 0f, false, true, true);
    }

    private static CraftPose Lander(Sighting s, float tau)
    {
        var dir = Dir(s.Heading);
        Vector3 p;
        float legs, ramp, hum;
        if (tau < LandTouch)
        {
            float f = tau / LandTouch;
            var from = -dir * 800f + Vector3.Up * 200f;
            p = new Vector3(from.X * (1f - EaseOut(f)), 200f * (1f - Smooth(Mathf.Min(1f, f * 1.08f))), from.Z * (1f - EaseOut(f)));
            legs = Smooth((tau - (LandTouch - 8f)) / 5f);
            ramp = 0f;
            hum = 0.8f - 0.4f * f;
        }
        else if (tau < LandLift)
        {
            p = Vector3.Zero;
            legs = 1f;
            ramp = tau < LandRampShut ? Smooth((tau - LandTouch - 1f) / 3f) : 1f - Smooth((tau - LandRampShut) / 2.5f);
            hum = 0.1f;
        }
        else
        {
            float f = (tau - LandLift) / (s.Length - LandLift);
            p = Vector3.Up * (40f * Smooth(f * 3f) + 300f * f * f) + dir * (800f * f * f);
            legs = 1f - Smooth(f * 4f);
            ramp = 0f;
            hum = 0.8f;
        }
        return new CraftPose(p, -s.Heading, hum, legs, ramp, false, true, true);
    }

    private static CraftPose Mothership(Sighting s, float tau)
    {
        // down out of the dark over the first half minute, up again over the last
        float come = Smooth(tau / 30f), go = Smooth((s.Length - tau) / 30f);
        float y = MothershipHeight + 1500f * (1f - Mathf.Min(come, go));
        var p = Vector3.Up * (y + Mathf.Sin(tau * 0.15f) * 3f);
        // the beam a minute in every two
        bool beam = tau > 40f && tau < s.Length - 40f && (int)((tau - 40f) / 60f) % 2 == 0;
        return new CraftPose(p, -s.Heading + tau * 0.01f, 0.5f, 0f, 0f, beam, true, true);
    }

    private static float EaseOut(float f) { f = Mathf.Clamp(f, 0f, 1f); return 1f - (1f - f) * (1f - f); }
}

/// <summary>
/// When and what the authority plans (#863): the night is cut into slots of simulated time, and for
/// each 2 km cell a player is in, each slot rolls a kind from a hash of the cell and the slot (so two
/// players in one cell share one sighting). Every sixth slot a 6 km cell may also get the mothership.
/// Pure; the anchor (a cow, a flat meadow) is the authority's to find, as only it knows the herds.
/// </summary>
public static class SightingPlanner
{
    /// <summary>Simulated seconds per slot: six or so in a night at the default 24-minute day.</summary>
    public const double SlotLength = 75;
    public const double CellSize = 2000, MothershipCell = 6000;
    /// <summary>The game hours sightings happen in: 21:00 to 05:00.</summary>
    public const double NightFrom = 21, NightTo = 5;

    public static long Slot(double sim) => (long)Math.Floor(sim / SlotLength);

    public static bool Night(double hour) => hour >= NightFrom || hour < NightTo;

    /// <summary>The cell of an LV95 point on a grid of <paramref name="size"/> m.</summary>
    public static (long E, long N) Cell(double e, double n, double size = CellSize) =>
        ((long)Math.Floor(e / size), (long)Math.Floor(n / size));

    /// <summary>A sighting's id: the cell and the slot (and the kind, so the mothership never shares one).</summary>
    public static int Id(long cellE, long cellN, long slot, SightingKind kind) =>
        (int)(Farming.HerdPlan.Hash((uint)(cellE * 7919 + cellN), (uint)slot, 0x51C7u + (uint)kind) & 0x7FFFFFFF);

    /// <summary>What a cell gets in a slot: a quarter nothing, then flyovers, abductions and landings; null for nothing.</summary>
    public static SightingKind? Roll(long cellE, long cellN, long slot, out uint seed)
    {
        seed = Farming.HerdPlan.Hash((uint)(cellE * 7919 + cellN), (uint)slot, 0x0F0);
        float u = Farming.HerdPlan.Unit(seed, 1, 2);
        return u < 0.25f ? null : u < 0.55f ? SightingKind.Flyover : u < 0.8f ? SightingKind.Abduction : SightingKind.Landing;
    }

    /// <summary>Whether a 6 km cell gets the mothership from this slot (every sixth slot, one time in three).</summary>
    public static bool RollMothership(long cellE, long cellN, long slot, out uint seed)
    {
        seed = Farming.HerdPlan.Hash((uint)(cellE * 104729 + cellN), (uint)slot, 0x3A7);
        return slot % 6 == 0 && Farming.HerdPlan.Unit(seed, 3, 4) < 0.34f;
    }

    /// <summary>How many craft: a flyover two to four, an abduction one or two, a landing and the mothership one.</summary>
    public static int CountOf(SightingKind kind, uint seed) => kind switch
    {
        SightingKind.Flyover => 2 + (int)(Farming.HerdPlan.Unit(seed, 5, 6) * 3f),
        SightingKind.Abduction => 1 + (int)(Farming.HerdPlan.Unit(seed, 7, 8) * 2f),
        _ => 1,
    };

    /// <summary>A heading in from a hash, rad.</summary>
    public static float HeadingOf(uint seed) => Farming.HerdPlan.Unit(seed, 9, 10) * Mathf.Tau;
}
