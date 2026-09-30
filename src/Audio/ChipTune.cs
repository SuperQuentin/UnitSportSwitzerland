using Godot;

namespace UnitSport.Audio;

/// <summary>
/// A two-voice chip tune in the NES 2A03's idiom: a 25% pulse for the tune and the 4-bit
/// stepped triangle for the bass, volumes quantised to 16 levels. For the occasions' menu jingles
/// (#18) — short, and in the same synthetic register as the rest of the game's sound.
/// </summary>
public static class ChipTune
{
    /// <summary>One note: MIDI number (negative for a rest) and length in beats.</summary>
    public readonly record struct Note(int Midi, float Beats);

    public static Note N(int midi, float beats) => new(midi, beats);

    public static float[] Render(IReadOnlyList<Note> lead, IReadOnlyList<Note> bass, float bpm)
    {
        float beat = 60f / bpm;
        float total = Math.Max(lead.Sum(n => n.Beats), bass.Sum(n => n.Beats)) * beat + 0.4f;
        var mix = new float[(int)(total * Dsp.Rate)];
        Voice(mix, lead, beat, Pulse, 0.42f);
        Voice(mix, bass, beat, Triangle, 0.55f);
        return mix;
    }

    private static float Pulse(float phase) => phase < 0.25f ? 1f : -1f;

    /// <summary>The 2A03 triangle: 32 steps, no volume control of its own.</summary>
    private static float Triangle(float phase)
    {
        int step = (int)(phase * 32f);
        int level = step < 16 ? 15 - step : step - 16;
        return level / 7.5f - 1f;
    }

    private static void Voice(float[] into, IReadOnlyList<Note> notes, float beat, Func<float, float> wave, float gain)
    {
        int at = 0;
        foreach (var note in notes)
        {
            int len = (int)(note.Beats * beat * Dsp.Rate);
            if (note.Midi >= 0)
            {
                float hz = 440f * Mathf.Pow(2f, (note.Midi - 69) / 12f);
                float phase = 0f;
                int attack = (int)(0.004f * Dsp.Rate), release = (int)(0.03f * Dsp.Rate);
                for (int i = 0; i < len && at + i < into.Length; i++)
                {
                    // a quick decay to a held level, then a short release: quantised to 4 bits
                    float env = i < attack ? i / (float)attack
                        : Mathf.Lerp(0.6f, 1f, Mathf.Exp(-(i - attack) / (0.08f * Dsp.Rate)));
                    if (i > len - release) env *= (len - i) / (float)release;
                    env = Mathf.Round(env * 15f) / 15f;
                    into[at + i] += wave(phase) * env * gain;
                    phase += hz / Dsp.Rate;
                    phase -= Mathf.Floor(phase);
                }
            }
            at += len;
        }
    }
}
