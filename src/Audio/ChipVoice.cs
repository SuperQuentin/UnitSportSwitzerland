namespace UnitSport.Audio;

/// <summary>Which sound "chip" renders the engines. Saved in settings; replicated nowhere.</summary>
public enum EngineVoice { Realistic, Ps1, Nes, Sid, Genesis }

/// <summary>
/// What the engine is doing at one sample, computed once by <see cref="EngineSynth"/>'s
/// chip-independent model and handed to whichever <see cref="IChipVoice"/> is selected.
///
/// <para>
/// A voice may use the finished <see cref="Core"/> sample (the PS1 voice degrades it, the way a
/// console played back a recording), or ignore it and build its own oscillators from the
/// parameters (NES, SID and FM have to: they are synthesisers, not samplers).
/// </para>
/// </summary>
public struct EngineFrame
{
    /// <summary>The realistic engine model's own output for this sample, about ±1.</summary>
    public float Core;
    /// <summary>Main tonal frequency, Hz: firing frequency for a piston, turbine whine for a turboshaft.</summary>
    public float ToneHz;
    /// <summary>A sub tone, Hz: half the firing frequency for a piston, the rotor thump's body for a helicopter.</summary>
    public float SubHz;
    /// <summary>0..1 envelope of the current pulse: the combustion stroke, or the rotor blade passing.</summary>
    public float Pulse;
    /// <summary>1 on the sample a new pulse begins, else 0. Retrigger envelopes here.</summary>
    public float PulseStart;
    /// <summary>How much of the sound is noise (exhaust, intake, rotor wash), 0..1.</summary>
    public float Noise;
    /// <summary>0..1 engine speed between idle and redline.</summary>
    public float Rpm01;
    /// <summary>0..1 how hard the engine is working. Brighter, harsher, more distorted.</summary>
    public float Load;
    /// <summary>0..1 throttle opening.</summary>
    public float Throttle;
    /// <summary>1 on a decel backfire pop, else 0.</summary>
    public float Crackle;
    /// <summary>Overall level 0..1, already including the volume setting. Multiply your output by it.</summary>
    public float Level;
    /// <summary>True for a turboshaft (helicopter), false for a piston.</summary>
    public bool Turbine;
}

/// <summary>
/// Turns an <see cref="EngineFrame"/> into one output sample, in the manner of a particular
/// sound chip. One instance per engine; state (oscillator phases, filters, LFSRs) lives in it.
/// Called <see cref="Dsp.Rate"/> times a second, on the main thread: no allocation in <see cref="Next"/>.
/// </summary>
public interface IChipVoice
{
    /// <summary>Returns a sample, roughly within ±1.</summary>
    float Next(in EngineFrame f);
}

/// <summary>The engine model itself, unprocessed.</summary>
public sealed class RealisticVoice : IChipVoice
{
    public float Next(in EngineFrame f) => f.Core * f.Level;
}

public static class ChipVoices
{
    public static IChipVoice Create(EngineVoice kind, int seed = 0) => kind switch
    {
        EngineVoice.Ps1 => new PsxSpuVoice(seed),
        EngineVoice.Nes => new Nes2A03Voice(seed),
        EngineVoice.Sid => new Sid6581Voice(seed),
        EngineVoice.Genesis => new Ym2612Voice(seed),
        _ => new RealisticVoice(),
    };
}
