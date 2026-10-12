using System.Text.Json.Serialization;

namespace UnitSport.Audio.Cd;

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
    public const int CurrentVersion = 1;

    /// <summary>Envelope frames per second.</summary>
    public const int EnvRate = 20;

    /// <summary>Analyser version that made this block; 0 when missing.</summary>
    [JsonPropertyName("av")] public int Version { get; init; }

    /// <summary>
    /// Base64 of <see cref="EnvRate"/> Hz frames, 2 bytes each: loudness (RMS, normalised per track,
    /// 0..255), then low-band (&lt;150 Hz) onset strength, the kick (normalised per track, 0..255).
    /// </summary>
    [JsonPropertyName("env")] public string Env { get; init; } = "";

    /// <summary>0..3: the beat of the grid (counted from beat 0 at <see cref="CdInfo.BeatOffset"/>) that starts a 4/4 bar.</summary>
    [JsonPropertyName("downbeat")] public int Downbeat { get; init; }

    /// <summary>Seconds into the file where each section starts, ascending; the first is 0.</summary>
    [JsonPropertyName("sections")] public float[] SectionStarts { get; init; } = Array.Empty<float>();

    /// <summary><see cref="SectionKind"/> of each section, as ints, same length as <see cref="SectionStarts"/>.</summary>
    [JsonPropertyName("kinds")] public int[] SectionKinds { get; init; } = Array.Empty<int>();

    private byte[]? _env;

    /// <summary>The decoded envelope, decoded once and kept (no allocation after the first call).</summary>
    public byte[] EnvBytes()
    {
        if (_env != null) return _env;
        try { _env = Convert.FromBase64String(Env); }
        catch (FormatException) { _env = Array.Empty<byte>(); }
        return _env;
    }

    /// <summary>Whether this CD needs (re-)analysing: no block, or one from an older analyser.</summary>
    public static bool IsStale(CdInfo cd) => cd.Analysis is not { } a || a.Version < CurrentVersion;

    /// <summary>For an RPC argument: the envelope as its base64 string, sections as packed arrays.</summary>
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["av"] = Version, ["env"] = Env, ["down"] = Downbeat, ["ss"] = SectionStarts, ["sk"] = SectionKinds,
    };

    /// <summary>Tolerates missing keys (an older or newer server).</summary>
    public static CdAnalysis FromDict(Godot.Collections.Dictionary d) => new()
    {
        Version = d.TryGetValue("av", out var v) ? v.AsInt32() : 0,
        Env = d.TryGetValue("env", out var e) ? e.AsString() : "",
        Downbeat = d.TryGetValue("down", out var b) ? b.AsInt32() : 0,
        SectionStarts = d.TryGetValue("ss", out var s) ? s.AsFloat32Array() : Array.Empty<float>(),
        SectionKinds = d.TryGetValue("sk", out var k) ? k.AsInt32Array() : Array.Empty<int>(),
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

    /// <summary>Position of beat <paramref name="beatIndex"/> (from beat 0 at the CD's offset) in its 4/4 bar: 0 = bar start.</summary>
    public static int BeatInBar(CdInfo cd, int beatIndex)
    {
        int down = cd.Analysis?.Downbeat ?? 0;
        return ((beatIndex - down) % 4 + 4) % 4;
    }
}
