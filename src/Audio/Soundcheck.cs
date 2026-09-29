using Godot;

namespace UnitSport.Audio;

/// <summary>Offline render of every sound bank and engine voice to WAVs (<c>--soundcheck dir</c>).</summary>
public static class Soundcheck
{
    public static int Run(string outDir)
    {
        System.IO.Directory.CreateDirectory(outDir);
        int bad = 0;

        var banks = new[]
        {
            SfxSynth.StepsBank, SfxSynth.LandingBank, SfxSynth.WhooshBank, SfxSynth.TickBank,
            SfxSynth.ImpactBank, SfxSynth.ChimeBank, SfxSynth.BoomBank, SfxSynth.GunBank,
        };
        foreach (var bank in banks)
            for (int i = 0; i < bank.Variants.Length; i++)
                bad += Save(System.IO.Path.Combine(outDir, $"{bank.Name}_{i}.wav"), Decode(bank.Variants[i]));

        var profiles = new (string name, EngineProfile p)[] { ("plane", EngineProfile.PistonAero), ("heli", EngineProfile.Turboshaft) };
        foreach (var (pname, profile) in profiles)
            foreach (EngineVoice voice in Enum.GetValues<EngineVoice>())
            {
                var synth = new EngineSynth(profile, spatial: false) { VoiceOverride = voice };
                var all = new List<float>();
                const float chunkS = 0.05f;
                int chunkN = (int)(chunkS * Dsp.Rate);
                for (float t = 0; t < 7f - 1e-4f; t += chunkS)
                {
                    float rpm, thr, load;
                    if (t < 1f) { rpm = 0f; thr = 0.1f; load = 0.1f; }
                    else if (t < 4f) { rpm = thr = load = (t - 1f) / 3f; }
                    else if (t < 5f) { rpm = thr = load = 1f; }
                    else { float k = (t - 5f) / 2f; rpm = Mathf.Lerp(1f, 0.2f, k); thr = load = 0f; }
                    var buf = new float[chunkN];
                    synth.Render(buf, rpm, thr, load, 1f);
                    all.AddRange(buf);
                }
                bad += Save(System.IO.Path.Combine(outDir, $"engine_{pname}_{voice.ToString().ToLowerInvariant()}.wav"), all.ToArray());
                synth.Free();
            }
        return bad > 0 ? 1 : 0;
    }

    private static float[] Decode(AudioStreamWav w)
    {
        var d = w.Data;
        var s = new float[d.Length / 2];
        for (int i = 0; i < s.Length; i++) s[i] = (short)(d[i * 2] | (d[i * 2 + 1] << 8)) / 32767f;
        return s;
    }

    private static int Save(string path, float[] s)
    {
        float peak = 0; double sum = 0; bool nan = false;
        foreach (float v in s)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { nan = true; continue; }
            peak = Math.Max(peak, Math.Abs(v)); sum += v * v;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, s.Length));
        bool fail = nan || peak > 1.0001f;
        Dsp.WriteWav(path, s);
        GD.Print($"{System.IO.Path.GetFileName(path)} peak={peak:F3} rms={rms:F3}{(fail ? " FAIL" : "")}");
        return fail ? 1 : 0;
    }
}
