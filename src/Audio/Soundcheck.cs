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
            SfxSynth.DoorOpenBank, SfxSynth.DoorCloseBank,
        };
        bad += Save(System.IO.Path.Combine(outDir, "tyre_squeal.wav"), Decode(SfxSynth.Squeal));
        bad += Save(System.IO.Path.Combine(outDir, "street.wav"), Decode(SfxSynth.Street));
        foreach (var bank in banks)
            for (int i = 0; i < bank.Variants.Length; i++)
                bad += Save(System.IO.Path.Combine(outDir, $"{bank.Name}_{i}.wav"), Decode(bank.Variants[i]));

        // the footsteps and landings of every surface (#375): heel, ball of the foot, weight
        foreach (Surface surf in Enum.GetValues<Surface>())
        {
            var step = Surfaces.Steps(surf);
            for (int i = 0; i < 3; i++)
                bad += Save(System.IO.Path.Combine(outDir, $"step_{surf.ToString().ToLowerInvariant()}_{i}.wav"), Decode(step.Variants[i]));
            bad += Save(System.IO.Path.Combine(outDir, $"land_{surf.ToString().ToLowerInvariant()}_0.wav"), Decode(Surfaces.Landing(surf).Variants[0]));
        }
        // the air bed (#375), left channel then right
        foreach (var (name, wav) in new[] { ("wind", AirBed.Wind), ("leaves", AirBed.Leaves) })
        {
            var (l, r) = DecodeStereo(wav);
            bad += Save(System.IO.Path.Combine(outDir, $"air_{name}_l.wav"), l);
            bad += Save(System.IO.Path.Combine(outDir, $"air_{name}_r.wav"), r);
        }

        // the occasions' sounds (#18): owl, howl, wind, toll, jingles
        foreach (var (name, samples) in Occasions.OccasionSounds.All())
            bad += Save(System.IO.Path.Combine(outDir, $"occasion_{name}.wav"), samples);

        var profiles = new (string name, EngineProfile p)[] { ("plane", EngineProfile.PistonAero), ("heli", EngineProfile.Turboshaft),
            ("inline4", EngineProfile.Inline4Na), ("rotary", EngineProfile.Rotary), ("boxer", EngineProfile.Boxer4Turbo),
            ("crossplane4", EngineProfile.Crossplane4), ("vtwin90", EngineProfile.VTwin90),
            ("twin270", EngineProfile.ParallelTwin270), ("vtwin52", EngineProfile.VTwin52) };
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

    private static (float[] L, float[] R) DecodeStereo(AudioStreamWav w)
    {
        var d = w.Data;
        int n = d.Length / 4;
        float[] l = new float[n], r = new float[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = (short)(d[i * 4] | (d[i * 4 + 1] << 8)) / 32767f;
            r[i] = (short)(d[i * 4 + 2] | (d[i * 4 + 3] << 8)) / 32767f;
        }
        return (l, r);
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
