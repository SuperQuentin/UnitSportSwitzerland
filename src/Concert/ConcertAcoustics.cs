using Godot;

namespace UnitSport.Concert;

// Godot maths only, no engine call: linked into the unit tests (tests/UnitSportSwitzerland.Tests, ConcertAcousticsTests).

/// <summary>One PA hang as the ears meet it, stadium frame: where it hangs and faces, and its delay tower if it has one.</summary>
public readonly record struct PaSource(Vector3 Hang, Vector3 Facing, Vector3? Tower = null, Vector3 TowerFacing = default);

/// <summary>
/// One hang heard from a spot: how late (s), how loud against the hang at 40 m (dB, before walls),
/// its highs' cutoff (Hz), and how far outside the bowl the spot is (0 in it .. 1 well out).
/// </summary>
public readonly record struct PaHeard(float Delay, float Db, float CutoffHz, float Outside);

/// <summary>An echo off one stand's front: how much later than the direct sound (ms), how far under it (dB), from which side (-1 left .. 1 right).</summary>
public readonly record struct Echo(float DelayMs, float Db, float Pan);

/// <summary>A venue's room, as Godot's reverb takes it, and what a stand's front sends back (dB; crowd and seats soak most of it).</summary>
public readonly record struct RoomTone(float Size, float Damping, float Wet, float PredelayMs, float PredelayFeedback, float Hipass, float ReflectDb);

/// <summary>Where the show is, for the crowd: the song (or the gap after one), as the ears hear it.</summary>
/// <param name="Section">0 calm, 1 groove, 2 peak, 3 chorus.</param>
/// <param name="GapTime">Seconds into the gap after a song; negative while a song plays.</param>
public readonly record struct CrowdCue(bool Show, float SongTime, float GapTime, float GapLeft, bool Finale, int Section, float Level, float Build, float SinceDrop);

/// <summary>The crowd's two voices, dB under their full level.</summary>
public readonly record struct CrowdMix(float RoarDb, float ApplauseDb);

/// <summary>
/// A concert's sound, worked out cheaply (#984): what a stadium does to the PA between the hangs and the
/// ears, and what the crowd adds. Each hang is late by its distance at the speed of sound (a delay tower
/// is timed to the main hangs, so it adds level and highs and never a second arrival); a line array
/// loses 3 dB a doubling in its near field and 6 beyond; the air takes the highs; behind a hang only its
/// lows are left; the stands' fronts send a slap back; outside the bowl it is the thump over the roof.
/// Pure: the speakers (<see cref="ConcertSpeaker"/>) and the room (<see cref="ConcertSound"/>) ask here.
/// </summary>
public static class ConcertAcoustics
{
    public const float SpeedOfSound = 343f;

    /// <summary>The level reference (0 dB) and where a line array's cylinder turns into a sphere, m.</summary>
    public const float RefM = 40f, NearField = 80f;

    /// <summary>A delay tower's few boxes against a main hang's twenty, dB.</summary>
    public const float TowerDb = -6f;

    /// <summary>Well outside the bowl, with nothing on the rays: what comes over the roof, dB and Hz.</summary>
    public const float OutsideDb = -9f, OutsideCutoff = 650f;

    /// <summary>How far past the facade the muffle takes to set in fully, m.</summary>
    public const float OutsideRamp = 12f;

    public static readonly RoomTone Stadium = new(0.5f, 0.6f, 0.07f, 55f, 0.15f, 0.2f, -9f);
    public static readonly RoomTone Arena = new(0.82f, 0.42f, 0.16f, 28f, 0.3f, 0.12f, -6f);
    public static readonly RoomTone OpenAir = new(0.22f, 0.85f, 0.025f, 8f, 0f, 0.3f, -40f);

    /// <summary>The falloff Godot already gives the speaker (inverse distance, full at its unit size, at most +3 dB).</summary>
    public static float PointDb(float d, float unit) => MathF.Min(3f, 20f * MathF.Log10(unit / MathF.Max(d, 0.1f)));

    /// <summary>A line array's throw against its level at <see cref="RefM"/>: -3 dB a doubling to <see cref="NearField"/>, -6 beyond; at most +4 dB right under it.</summary>
    public static float ThrowDb(float d)
    {
        d = MathF.Max(d, 1f);
        float db = d <= NearField ? -3f * MathF.Log2(d / RefM) : -3f * MathF.Log2(NearField / RefM) - 6f * MathF.Log2(d / NearField);
        return MathF.Min(db, 4f);
    }

    /// <summary>
    /// The air's low-pass after <paramref name="d"/> m: where ~6 dB of the highs is lost (ISO 9613-1 at
    /// 20 °C, 50 %: 3.3 dB per 100 m at 4 kHz, 11.7 at 8 kHz, rising about as f^1.8). 8 kHz at 50 m, 4.4 at 150.
    /// </summary>
    public static float AirCutoffHz(float d) => Math.Clamp(4000f * MathF.Pow(182f / MathF.Max(d, 1f), 0.556f), 1500f, 20000f);

    /// <summary>
    /// A hang's horizontal coverage: full within 50° of its axis (a line array covers ~100°), then down to
    /// -12 dB and only the lows (500 Hz) straight behind it: behind the stage is the bass and the room.
    /// </summary>
    public static (float Db, float CutoffHz) Directivity(Vector3 facing, Vector3 toListener)
    {
        var f = new Vector2(facing.X, facing.Z);
        var l = new Vector2(toListener.X, toListener.Z);
        if (f.LengthSquared() < 1e-6f || l.LengthSquared() < 1e-6f) return (0f, 20000f);
        float c = f.Normalized().Dot(l.Normalized());
        float t = Math.Clamp((0.64f - c) / 1.64f, 0f, 1f);
        return (-12f * t, LogLerp(20000f, 500f, t));
    }

    /// <summary>How far outside a bowl's footprint (half extents, stadium frame; zero = no bowl) a point is, 0..1.</summary>
    public static float Outside(Vector3 p, Vector2 outerHalf)
    {
        if (outerHalf.X <= 0f) return 0f;
        float beyond = MathF.Max(MathF.Abs(p.X) - outerHalf.X, MathF.Abs(p.Z) - outerHalf.Y);
        return Math.Clamp(beyond / OutsideRamp, 0f, 1f);
    }

    /// <summary>
    /// One hang from <paramref name="ear"/> (stadium frame). <paramref name="blocked"/> is the share of the
    /// speaker's occlusion rays already stopped (<see cref="Audio.Hearing"/>): a wall it has already
    /// muffled is not muffled twice.
    /// </summary>
    public static PaHeard Hear(in PaSource s, Vector3 ear, Vector2 outerHalf, float blocked = 0f)
    {
        var to = ear - s.Hang;
        float d = to.Length();
        var dir = Directivity(s.Facing, to);
        float db = ThrowDb(d) + dir.Db, airM = d;
        if (s.Tower is { } tower)
        {
            // the tower is timed to the mains: one arrival, louder and brighter where it covers
            var tt = ear - tower;
            float dt = tt.Length();
            float tdb = ThrowDb(dt) + TowerDb + Directivity(s.TowerFacing, tt).Db;
            float p1 = MathF.Pow(10f, db / 10f), p2 = MathF.Pow(10f, tdb / 10f);
            db = 10f * MathF.Log10(p1 + p2);
            airM = d + (dt - d) * (p2 / (p1 + p2));
        }
        float cut = MathF.Min(AirCutoffHz(airM), dir.CutoffHz);
        float outside = Outside(ear, outerHalf) * (1f - Math.Clamp(blocked, 0f, 1f));
        db += OutsideDb * outside;
        cut = MathF.Min(cut, LogLerp(20000f, OutsideCutoff, outside));
        return new PaHeard(d / SpeedOfSound, db, cut, outside);
    }

    /// <summary>
    /// The strongest echoes off the four stands' fronts (half extents <paramref name="half"/>): the
    /// source mirrored behind each front, its path's extra length the delay, its extra distance and the
    /// stand's soak the level, the PA's coverage towards the wall against towards the ears. Sorted
    /// loudest first into <paramref name="into"/>; returns how many. None from outside the bowl.
    /// </summary>
    public static int Echoes(Vector3 source, Vector3 facing, Vector3 ear, Vector2 half, float reflectDb, Vector3 earRight, Span<Echo> into)
    {
        if (half.X <= 0f || MathF.Abs(ear.X) >= half.X || MathF.Abs(ear.Z) >= half.Y) return 0;
        float direct = MathF.Max(1f, (ear - source).Length());
        float directDir = Directivity(facing, ear - source).Db;
        int n = 0;
        for (int w = 0; w < 4; w++)
        {
            bool alongX = w < 2;
            float plane = (w % 2 == 0 ? 1f : -1f) * (alongX ? half.X : half.Y);
            float s = alongX ? source.X : source.Z, l = alongX ? ear.X : ear.Z;
            if (MathF.Abs(s) >= MathF.Abs(plane) && MathF.Sign(s) == MathF.Sign(plane)) continue;   // a hang out over the stand
            var image = source;
            if (alongX) image.X = 2f * plane - s; else image.Z = 2f * plane - s;
            float r = (ear - image).Length();
            float il = alongX ? image.X : image.Z;
            float k = MathF.Abs(l - il) < 1e-4f ? 0f : (plane - il) / (l - il);
            var hit = image + (ear - image) * k;
            float db = 20f * MathF.Log10(direct / r) + reflectDb + Directivity(facing, hit - source).Db - directDir;
            var toWall = new Vector3(hit.X - ear.X, 0f, hit.Z - ear.Z);
            float pan = toWall.LengthSquared() > 1e-4f ? toWall.Normalized().Dot(earRight) : 0f;
            var e = new Echo((r - direct) / SpeedOfSound * 1000f, db, pan);
            // keep the loudest few, in order
            if (into.Length == 0) continue;
            int i = n < into.Length ? n : into.Length - 1;
            if (n == into.Length && into[i].Db >= e.Db) continue;
            if (n < into.Length) n++;
            for (; i > 0 && into[i - 1].Db < e.Db; i--) into[i] = into[i - 1];
            into[i] = e;
        }
        return n;
    }

    /// <summary>
    /// The reverb's wet for a listener the PA reaches at <paramref name="directDb"/>: the room's field is
    /// about even through the bowl while the direct sound falls, so the far stand hears more room (capped).
    /// </summary>
    public static float Wet(in RoomTone room, float directDb) => Math.Clamp(room.Wet * MathF.Pow(10f, -directDb / 40f), 0f, 0.45f);

    /// <summary>
    /// What the crowd does at this moment of the show: a bed under the music that follows its level, a
    /// singalong swell in a chorus, a lift through a build, a roar on the drop, a cheer of recognition as a
    /// song starts; between songs cheers and applause that fade through the gap and swell again as the
    /// next song is due; the finale's applause longer and louder.
    /// </summary>
    public static CrowdMix Crowd(in CrowdCue c)
    {
        if (!c.Show) return new CrowdMix(-80f, -80f);
        float roar, clap = -80f;
        if (c.GapTime < 0f)
        {
            roar = -24f + 6f * Math.Clamp(c.Level, 0f, 1f);
            if (c.Section == 3) roar += 7f;        // singing along
            else if (c.Section == 2) roar += 3f;
            roar += 8f * Math.Clamp(c.Build, 0f, 1f);
            if (c.SinceDrop >= 0f && c.SinceDrop < 6f) roar = MathF.Max(roar, -3f - 3f * c.SinceDrop);
            if (c.SongTime >= 0f && c.SongTime < 5f)
            {
                roar = MathF.Max(roar, -9f - 3f * c.SongTime);
                clap = -14f - 4f * c.SongTime;
            }
        }
        else
        {
            float g = c.GapTime;
            roar = c.Finale ? -2f - 1.2f * g : -5f - 3f * g;
            clap = c.Finale ? -2f - 0.6f * g : -4f - 2f * g;
            // the next song is due: a swell of expectation
            if (!c.Finale && c.GapLeft < 1.5f) roar = MathF.Max(roar, -10f - 4f * c.GapLeft);
            roar = MathF.Max(roar, -24f);
        }
        return new CrowdMix(MathF.Max(roar, -80f), MathF.Max(clap, -80f));
    }

    /// <summary>
    /// The crowd's level and the muffle's cutoff where the ears stand: all of it in the bowl (or in an open
    /// air's field, <paramref name="field"/>); past the facade (<paramref name="outer"/>, zero for an open
    /// air) dull and falling away; nothing past 600 m.
    /// </summary>
    public static (float Db, float CutoffHz) CrowdAt(Vector3 ear, Vector2 field, Vector2 outer)
    {
        var edge = outer.X > 0f ? outer : field;
        float beyond = MathF.Max(MathF.Abs(ear.X) - edge.X, MathF.Abs(ear.Z) - edge.Y);
        if (beyond > 600f) return (-80f, 500f);
        float past = MathF.Max(beyond, 0f);
        if (outer.X <= 0f) return (-6f * MathF.Log2(1f + past / 30f), past < 1f ? 20000f : AirCutoffHz(past));
        float o = Math.Clamp(beyond / OutsideRamp, 0f, 1f);
        return (-12f * o - 6f * MathF.Log2(1f + past / 40f), LogLerp(20000f, 500f, o));
    }

    public static float LogLerp(float a, float b, float t) =>
        t <= 0f ? a : t >= 1f ? b : MathF.Exp(MathF.Log(a) + (MathF.Log(b) - MathF.Log(a)) * t);

    /// <summary>
    /// Two delay taps from the echoes (loudest first): the loudest, and the loudest one clearly later or
    /// earlier than it. Twins (the two side stands at the same distance) are one echo from the middle.
    /// </summary>
    public static int Taps(ReadOnlySpan<Echo> echoes, out Echo first, out Echo second)
    {
        first = second = default;
        if (echoes.Length == 0) return 0;
        first = echoes[0];
        int n = 1;
        for (int i = 1; i < echoes.Length; i++)
        {
            var e = echoes[i];
            if (MathF.Abs(e.DelayMs - first.DelayMs) < 30f)
            {
                if (n == 1 && e.Db > first.Db - 3f) first = first with { Pan = (first.Pan + e.Pan) / 2f, Db = first.Db + 1.5f };
                continue;
            }
            second = e;
            return 2;
        }
        return n;
    }
}
