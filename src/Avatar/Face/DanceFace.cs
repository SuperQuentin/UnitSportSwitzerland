using System;

namespace UnitSport.Avatar.Face;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests, DanceFaceTests).

/// <summary>
/// The music as a dancer's face hears it (#728): what the CD's analysis says is happening now,
/// on the shared clock, so every peer draws the same face with nothing on the wire.
/// </summary>
/// <param name="Style">The CD's <c>MusicStyle</c> number (0 Pop, 1 Rock, 2 Electronic, 3 HipHop, 4 Chill, 5 Folk, 6 RatDance).</param>
/// <param name="Section">The section's kind: 0 calm, 1 groove, 2 peak, 3 chorus.</param>
/// <param name="Level">Loudness now, 0..1 of the song's own range.</param>
/// <param name="Kick">The hit now, 0..1, decaying.</param>
/// <param name="BarKick">1 on a bar's first beat, decaying; 0 on the others.</param>
/// <param name="Burst">1 as a new section starts, decaying over a second or two.</param>
/// <param name="Beat">The beat count from the bar's one, for alternating shapes.</param>
/// <param name="Bar">The bar count, for the once-in-a-while gestures.</param>
/// <param name="Floor">0 standing, 1 a breakdance power move or footwork, 2 the freeze.</param>
public readonly record struct DanceHearing(int Style, int Section, float Level, float Kick, float BarKick, float Burst,
    int Beat, int Bar, int Floor);

/// <summary>
/// A dancing face (#728): the expression follows the music instead of one fixed "happy". Calm
/// parts dream with the eyes half shut, a groove smiles, a peak grins wider and the mouth opens
/// on the kicks, a chorus sings along (the mouth shaping "oh" and "ee" a beat each); a new section
/// is a "wow", the bar's one lifts the brows. Each style has its own temper (rock snarls and
/// squints, hip-hop is cool and smirks, folk beams, chill floats), and each dancer a personality
/// from its seed (one sings along, one winks on the bar now and then, one blushes). Breaking, the
/// face concentrates, and the freeze holds a cool smirk.
/// </summary>
public static class DanceFace
{
    public static FaceState Target(in DanceHearing h, uint seed)
    {
        var s = FaceState.Neutral;
        float kick = Clamp01(h.Kick), level = Clamp01(h.Level);
        bool singer = (seed & 3u) == 0u, winker = (seed & 3u) == 1u, blusher = (seed >> 2 & 3u) == 0u;

        // the section sets the mood
        switch (h.Section)
        {
            case 0: // calm: dreaming along
                s.OpenL = s.OpenR = 0.45f; s.Smile = 0.35f; s.Brow = 0.1f; s.GazeY = 0.2f;
                break;
            case 2: // peak: a grin, the mouth opening on the hits
                s.Smile = 0.85f; s.Jaw = 0.15f + 0.45f * kick; s.Brow = 0.25f; s.Wide = 0.3f;
                break;
            case 3: // chorus: singing along, an "oh" on one beat and an "ee" on the next
                s.Smile = 0.6f; s.Brow = 0.45f;
                s.Jaw = 0.2f + 0.5f * level * (0.5f + 0.5f * kick);
                s.Wide = (h.Beat & 1) == 0 ? -0.5f : 0.6f;
                if (!singer) s.Jaw *= 0.5f;
                break;
            default: // groove
                s.Smile = 0.65f; s.Brow = 0.15f; s.Jaw = 0.08f * kick;
                break;
        }

        // each style's temper
        switch (h.Style)
        {
            case 1: // rock: a snarl and a squint on the big parts, the jaw dropping on the hits
                if (h.Section >= 2) { s.Smile = 0.1f; s.Brow = -0.6f; s.Squint = 0.6f; s.Jaw = 0.4f + 0.4f * kick; s.Wide = 0.4f; }
                break;
            case 2: // electronic: eyes shut in bliss on the kicks of a peak
                if (h.Section >= 2) { s.Special = kick > 0.6f ? FaceEyes.Happy : FaceEyes.Normal; s.Brow += 0.2f; }
                break;
            case 3: // hip-hop: cool, half-lidded, a smirk; the head nods do the rest
                s.Squint = Math.Max(s.Squint, 0.35f); s.Wide = Math.Max(s.Wide, 0.4f); s.Smile = Math.Min(s.Smile, 0.55f);
                if (h.Section < 3) s.Jaw *= 0.4f;
                break;
            case 4: // chill: floating, the eyes mostly closed
                s.OpenL = s.OpenR = Math.Min(s.OpenL, 0.35f); s.Jaw *= 0.3f;
                break;
            case 5: // folk: beaming
                s.Smile = Math.Max(s.Smile, 0.9f); s.Blush = Math.Max(s.Blush, 0.3f);
                break;
            case 6: // the rat dance: in love with the chess type beat
                s.Special = FaceEyes.Hearts; s.Smile = 0.8f; s.Blush = 0.6f;
                break;
        }

        // the bar's one lifts the brows; a hit narrows the eyes a little
        s.Brow = Math.Clamp(s.Brow + 0.35f * Clamp01(h.BarKick), -1f, 1f);
        s.Squint = Clamp01(s.Squint + 0.15f * kick);

        // personality
        if (blusher && h.Section >= 2) s.Blush = Math.Max(s.Blush, 0.5f);
        if (winker && s.Special == FaceEyes.Normal && h.Bar % 4 == 3 && h.Beat % 4 == 3) s.Special = FaceEyes.Wink;

        // breaking: concentration, then the freeze's cool smirk
        if (h.Floor == 1) { s.Special = FaceEyes.Normal; s.Squint = 0.5f; s.Brow = -0.3f; s.Smile = 0.1f; s.Jaw = 0.1f; s.Wide = 0.5f; }
        else if (h.Floor == 2) { s.Special = FaceEyes.Normal; s.Squint = 0.3f; s.Brow = 0.2f; s.Smile = 0.5f; s.Jaw = 0f; s.Wide = 0.7f; }

        // a new section: "wow", eyes wide, brows up, mouth open, fading as the burst does
        float wow = h.Floor == 0 ? Clamp01(h.Burst) : 0f;   // breaking, the concentration wins
        if (wow > 0.01f)
        {
            var surprised = FaceState.Neutral;
            surprised.Jaw = 0.6f; surprised.Wide = -0.6f; surprised.Brow = 1f;
            var special = s.Special;
            s = FaceState.Lerp(s, surprised, wow * 0.8f);
            if (wow < 0.5f) s.Special = special;
        }

        // nearly silent: the face rests
        if (level < 0.06f && h.Floor == 0) s = FaceState.Lerp(s, Calm(), 0.7f);
        return s;
    }

    private static FaceState Calm()
    {
        var c = FaceState.Neutral;
        c.Smile = 0.2f;
        c.OpenL = c.OpenR = 0.7f;
        return c;
    }

    private static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}
