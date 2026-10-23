using System.Text.Json.Serialization;

namespace UnitSport.Audio.Cd;

/// <summary>The two halves of a CD's voice/music mix (#1153), each its own Ogg beside the CD.</summary>
public enum CdStem { Voice, Music }

/// <summary>What a stretch of a track feels like, from <see cref="BeatAnalyzer"/>. Stored as an int: append only.</summary>
public enum SectionKind { Calm = 0, Groove = 1, Peak = 2, Chorus = 3 }

/// <summary>
/// The detailed analysis of a CD (#725), computed once when it is burnt (or backfilled at start by
/// <see cref="CdLibrary"/> when missing or older than <see cref="CurrentVersion"/>), carried on
/// <see cref="CdInfo.Analysis"/> and played back from the shared clock with
/// <see cref="CdAnalysisRuntime"/>: nothing crosses the network per frame.
/// </summary>
public sealed class CdAnalysis
{
    /// <summary>Bump when the analyser changes what it stores: older CDs are re-analysed at start.</summary>
    public const int CurrentVersion = 3;

    /// <summary>Envelope frames per second.</summary>
    public const int EnvRate = 20;

    /// <summary>Analyser version that made this block; 0 when missing.</summary>
    [JsonPropertyName("av")] public int Version { get; init; }

    /// <summary>
    /// Base64 of <see cref="EnvRate"/> Hz frames, 2 bytes each: loudness (RMS, normalised per track,
    /// 0..255), then low-band (&lt;150 Hz) onset strength, the kick (normalised per track, 0..255).
    /// </summary>
    [JsonPropertyName("env")] public string Env { get; init; } = "";

    /// <summary>
    /// Base64 of <see cref="EnvRate"/> Hz frames, 4 bytes each (v2, #885): the snare band's onsets
    /// (1.5..5 kHz), the hi-hats' (above 6 kHz), the bass level (&lt;150 Hz), all three normalised per
    /// track, then the brightness (spectral centroid on a log scale, 200 Hz = 0 .. 5 kHz = 255).
    /// </summary>
    [JsonPropertyName("bands")] public string Bands { get; init; } = "";

    /// <summary>Seconds into the file of each drop (v2): a bar where the bass and the loudness come back hard after a break.</summary>
    [JsonPropertyName("drops")] public float[] Drops { get; init; } = Array.Empty<float>();

    /// <summary>Where the build into each drop starts, s, same length as <see cref="Drops"/>.</summary>
    [JsonPropertyName("builds")] public float[] BuildStarts { get; init; } = Array.Empty<float>();

    /// <summary>0..3: the beat of the grid (counted from beat 0 at <see cref="CdInfo.BeatOffset"/>) that starts a 4/4 bar.</summary>
    [JsonPropertyName("downbeat")] public int Downbeat { get; init; }

    /// <summary>Seconds into the file where each section starts, ascending; the first is 0.</summary>
    [JsonPropertyName("sections")] public float[] SectionStarts { get; init; } = Array.Empty<float>();

    /// <summary><see cref="SectionKind"/> of each section, as ints, same length as <see cref="SectionStarts"/>.</summary>
    [JsonPropertyName("kinds")] public int[] SectionKinds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Base64 of <see cref="EnvRate"/> Hz frames, 4 bytes each (v3, #1153), from the separated vocal:
    /// its level (normalised per track, 0 = no voice), syllable onsets (0..255), the vowel (0 = "oo"
    /// .. 255 = "ee", 128 when unsure) and the sung pitch as a MIDI note (0 = unvoiced).
    /// </summary>
    [JsonPropertyName("voc")] public string Vocal { get; init; } = "";

    /// <summary>What separated the vocal: the model's name (<see cref="VocalSeparator.ModelName"/>) or "centre" (the mid/side fallback); empty before v3.</summary>
    [JsonPropertyName("vs")] public string VocalSource { get; init; } = "";

    /// <summary>Seconds where the melody climbs (>= 4 semitones within a sung phrase) or falls; each start and end, packed in pairs.</summary>
    [JsonPropertyName("rises")] public float[] Rises { get; init; } = Array.Empty<float>();
    [JsonPropertyName("falls")] public float[] Falls { get; init; } = Array.Empty<float>();

    /// <summary>Long sung notes (>= 1.2 s on one pitch): start and end, in pairs.</summary>
    [JsonPropertyName("holds")] public float[] Holds { get; init; } = Array.Empty<float>();

    /// <summary>Where the voice comes back after at least two bars without it.</summary>
    [JsonPropertyName("vin")] public float[] VocalIns { get; init; } = Array.Empty<float>();

    /// <summary>The voice and music stems were written beside the CD (<see cref="CdBurner.StemPath"/>): its mix can be changed.</summary>
    [JsonPropertyName("st")] public bool Stems { get; init; }

    private byte[]? _env, _bands, _vocal;

    /// <summary>The decoded envelope, decoded once and kept (no allocation after the first call).</summary>
    public byte[] EnvBytes()
    {
        if (_env != null) return _env;
        try { _env = Convert.FromBase64String(Env); }
        catch (FormatException) { _env = Array.Empty<byte>(); }
        return _env;
    }

    /// <summary>The decoded <see cref="Bands"/>, decoded once and kept.</summary>
    public byte[] BandBytes()
    {
        if (_bands != null) return _bands;
        try { _bands = Convert.FromBase64String(Bands); }
        catch (FormatException) { _bands = Array.Empty<byte>(); }
        return _bands;
    }

    /// <summary>This block with the singer added (v3), from <see cref="VocalAnalyzer"/> on what <paramref name="source"/> separated.</summary>
    public CdAnalysis WithVocal(VocalAnalyzer.Result v, string source, bool stems) => new()
    {
        Version = Version, Env = Env, Bands = Bands, Drops = Drops, BuildStarts = BuildStarts, Downbeat = Downbeat,
        SectionStarts = SectionStarts, SectionKinds = SectionKinds,
        Vocal = Convert.ToBase64String(v.Frames), VocalSource = source,
        Rises = v.Rises, Falls = v.Falls, Holds = v.Holds, VocalIns = v.VocalIns, Stems = stems,
    };

    /// <summary>The decoded <see cref="Vocal"/>, decoded once and kept.</summary>
    public byte[] VocalBytes()
    {
        if (_vocal != null) return _vocal;
        try { _vocal = Convert.FromBase64String(Vocal); }
        catch (FormatException) { _vocal = Array.Empty<byte>(); }
        return _vocal;
    }

    /// <summary>
    /// Whether this CD needs (re-)analysing: no block, one from an older analyser, or a vocal from
    /// the fallback while the model is here now.
    /// </summary>
    public static bool IsStale(CdInfo cd) => cd.Analysis is not { } a || a.Version < CurrentVersion
        || VocalSeparator.ModelName is { } model && a.VocalSource != model;

    /// <summary>For an RPC argument: the envelope as its base64 string, sections as packed arrays.</summary>
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["av"] = Version, ["env"] = Env, ["down"] = Downbeat, ["ss"] = SectionStarts, ["sk"] = SectionKinds,
        ["bands"] = Bands, ["drops"] = Drops, ["builds"] = BuildStarts,
        ["voc"] = Vocal, ["vs"] = VocalSource, ["rises"] = Rises, ["falls"] = Falls, ["holds"] = Holds, ["vin"] = VocalIns, ["st"] = Stems,
    };

    /// <summary>Tolerates missing keys (an older or newer server).</summary>
    public static CdAnalysis FromDict(Godot.Collections.Dictionary d) => new()
    {
        Version = d.TryGetValue("av", out var v) ? v.AsInt32() : 0,
        Env = d.TryGetValue("env", out var e) ? e.AsString() : "",
        Downbeat = d.TryGetValue("down", out var b) ? b.AsInt32() : 0,
        SectionStarts = d.TryGetValue("ss", out var s) ? s.AsFloat32Array() : Array.Empty<float>(),
        SectionKinds = d.TryGetValue("sk", out var k) ? k.AsInt32Array() : Array.Empty<int>(),
        Bands = d.TryGetValue("bands", out var bd) ? bd.AsString() : "",
        Drops = d.TryGetValue("drops", out var dr) ? dr.AsFloat32Array() : Array.Empty<float>(),
        BuildStarts = d.TryGetValue("builds", out var bu) ? bu.AsFloat32Array() : Array.Empty<float>(),
        Vocal = d.TryGetValue("voc", out var vo) ? vo.AsString() : "",
        VocalSource = d.TryGetValue("vs", out var vs) ? vs.AsString() : "",
        Rises = d.TryGetValue("rises", out var ri) ? ri.AsFloat32Array() : Array.Empty<float>(),
        Falls = d.TryGetValue("falls", out var fa) ? fa.AsFloat32Array() : Array.Empty<float>(),
        Holds = d.TryGetValue("holds", out var ho) ? ho.AsFloat32Array() : Array.Empty<float>(),
        VocalIns = d.TryGetValue("vin", out var vi) ? vi.AsFloat32Array() : Array.Empty<float>(),
        Stems = d.TryGetValue("st", out var st) && st.AsBool(),
    };
}

/// <summary>
/// Reads a CD's <see cref="CdAnalysis"/> at a time into the track, for radios and dancers. Every
/// call is allocation free (the envelope is decoded once per <see cref="CdAnalysis"/>).
/// </summary>
public static class CdAnalysisRuntime
{
    /// <summary>
    /// Loudness and kick at <paramref name="t"/> seconds into the file, 0..1. The level is
    /// interpolated between frame centres; the kick holds a frame's onset and lets the previous
    /// frame's fade over this one, so a 50 ms hit is seen at any frame rate. False when the CD has
    /// no envelope; past either end both are 0.
    /// </summary>
    public static bool Sample(CdInfo cd, double t, out float level, out float kick)
    {
        level = 0f;
        kick = 0f;
        if (cd.Analysis is not { } a) return false;
        byte[] e = a.EnvBytes();
        int n = e.Length / 2;
        if (n == 0) return false;
        double x = t * CdAnalysis.EnvRate;
        if (x < 0 || x >= n) return true;

        int i = (int)x;
        float fr = (float)(x - i);
        double xc = Math.Max(0.0, x - 0.5);
        int i0 = Math.Min((int)xc, n - 1), i1 = Math.Min(i0 + 1, n - 1);
        float fc = (float)(xc - i0);
        level = (e[2 * i0] * (1f - fc) + e[2 * i1] * fc) / 255f;

        float cur = e[2 * i + 1] / 255f;
        float prev = i > 0 ? e[2 * i - 1] / 255f * (1f - fr) : 0f;
        kick = Math.Max(cur, prev);
        return true;
    }

    /// <summary>
    /// The finer bands at <paramref name="t"/> seconds into the file, 0..1 (v2): snare and hi-hat
    /// onsets (held a frame and fading over the next, as the kick), the bass level and the brightness
    /// (interpolated). False when the CD has no bands (an older analysis); past either end all are 0.
    /// </summary>
    public static bool SampleBands(CdInfo cd, double t, out float snare, out float hat, out float bass, out float bright)
    {
        snare = hat = bass = bright = 0f;
        if (cd.Analysis is not { } a) return false;
        byte[] e = a.BandBytes();
        int n = e.Length / 4;
        if (n == 0) return false;
        double x = t * CdAnalysis.EnvRate;
        if (x < 0 || x >= n) return true;
        int i = (int)x, j = Math.Min(i + 1, n - 1);
        float fr = (float)(x - i);
        snare = Math.Max(e[4 * i] / 255f, i > 0 ? e[4 * i - 4] / 255f * (1f - fr) : 0f);
        hat = Math.Max(e[4 * i + 1] / 255f, i > 0 ? e[4 * i - 3] / 255f * (1f - fr) : 0f);
        bass = (e[4 * i + 2] * (1f - fr) + e[4 * j + 2] * fr) / 255f;
        bright = (e[4 * i + 3] * (1f - fr) + e[4 * j + 3] * fr) / 255f;
        return true;
    }

    /// <summary>
    /// Where <paramref name="t"/> is against the song's drops: seconds since the last one (infinity
    /// before the first) and 0..1 through the build into the next (0 outside a build).
    /// </summary>
    public static void DropAt(CdInfo cd, double t, out float sinceDrop, out float build)
    {
        sinceDrop = float.PositiveInfinity;
        build = 0f;
        if (cd.Analysis is not { Drops: { Length: > 0 } drops } a) return;
        for (int i = 0; i < drops.Length; i++)
        {
            if (t >= drops[i]) { sinceDrop = (float)(t - drops[i]); continue; }
            float b0 = i < a.BuildStarts.Length ? a.BuildStarts[i] : drops[i];
            if (t >= b0 && drops[i] > b0) build = (float)((t - b0) / (drops[i] - b0));
            break;
        }
    }

    /// <summary>The section playing at <paramref name="t"/> seconds into the file; Groove and index 0 when unknown.</summary>
    public static SectionKind SectionAt(CdInfo cd, double t, out int sectionIndex)
    {
        sectionIndex = 0;
        if (cd.Analysis is not { } a || a.SectionStarts.Length == 0) return SectionKind.Groove;
        float[] starts = a.SectionStarts;
        int idx = 0;
        for (int i = 1; i < starts.Length && t >= starts[i]; i++) idx = i;
        sectionIndex = idx;
        return idx < a.SectionKinds.Length ? (SectionKind)a.SectionKinds[idx] : SectionKind.Groove;
    }

    /// <summary>
    /// The voice at <paramref name="t"/> seconds into the file (v3): level 0..1 (interpolated),
    /// syllable 0..1 (held a frame and fading over the next, as the kick), vowel -1 "oo" .. +1 "ee",
    /// pitch as a MIDI note (interpolated while sung, 0 unvoiced). False without vocal data.
    /// </summary>
    public static bool SampleVocal(CdInfo cd, double t, out float level, out float syllable, out float vowel, out float pitch)
    {
        level = syllable = vowel = pitch = 0f;
        if (cd.Analysis is not { } a) return false;
        byte[] e = a.VocalBytes();
        int n = e.Length / 4;
        if (n == 0) return false;
        double x = t * CdAnalysis.EnvRate;
        if (x < 0 || x >= n) return true;
        int i = (int)x, j = Math.Min(i + 1, n - 1);
        float fr = (float)(x - i);
        level = (e[4 * i] * (1f - fr) + e[4 * j] * fr) / 255f;
        syllable = Math.Max(e[4 * i + 1] / 255f, i > 0 ? e[4 * i - 3] / 255f * (1f - fr) : 0f);
        vowel = (e[4 * i + 2] * (1f - fr) + e[4 * j + 2] * fr) / 127.5f - 1f;
        byte p0 = e[4 * i + 3], p1 = e[4 * j + 3];
        pitch = p0 == 0 ? 0f : p1 == 0 ? p0 : p0 * (1f - fr) + p1 * fr;
        return true;
    }

    /// <summary>
    /// The melody around <paramref name="t"/> (v3): <paramref name="rise"/> +1 climbing .. -1 falling
    /// (ramped through each run, then let go), <paramref name="hold"/> 0..1 through a long note (0 outside
    /// one), and seconds since the voice last came back (infinity before the first entry).
    /// </summary>
    public static void MelodyAt(CdInfo cd, double t, out float rise, out float hold, out float sinceVocalIn)
    {
        rise = hold = 0f;
        sinceVocalIn = float.PositiveInfinity;
        if (cd.Analysis is not { } a) return;
        rise = Run(a.Rises, t) - Run(a.Falls, t);
        for (int i = 0; i + 1 < a.Holds.Length; i += 2)
            if (t >= a.Holds[i] && t < a.Holds[i + 1]) { hold = (float)((t - a.Holds[i]) / (a.Holds[i + 1] - a.Holds[i])); break; }
        foreach (float vin in a.VocalIns)
            if (t >= vin) sinceVocalIn = (float)(t - vin);
            else break;
    }

    /// <summary>Seconds a run's value takes to let go after its last note.</summary>
    private const float RunRelease = 1f;

    /// <summary>
    /// A start/end pair as a ramp: 0 -> 1 through the run (the lights climb with the tune, top note
    /// at the top), then back to 0 over <see cref="RunRelease"/>; 0 elsewhere.
    /// </summary>
    private static float Run(float[] pairs, double t)
    {
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (t < pairs[i]) break;
            if (t < pairs[i + 1]) return (float)((t - pairs[i]) / (pairs[i + 1] - pairs[i]));
            if (t < pairs[i + 1] + RunRelease) return 1f - (float)(t - pairs[i + 1]) / RunRelease;
        }
        return 0f;
    }

    /// <summary>Position of beat <paramref name="beatIndex"/> (from beat 0 at the CD's offset) in its 4/4 bar: 0 = bar start.</summary>
    public static int BeatInBar(CdInfo cd, int beatIndex)
    {
        int down = cd.Analysis?.Downbeat ?? 0;
        return ((beatIndex - down) % 4 + 4) % 4;
    }
}
