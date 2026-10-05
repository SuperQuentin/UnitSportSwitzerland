using Godot;

namespace UnitSport.Audio;

/// <summary>Offline render of every sound bank and engine voice to WAVs (<c>--soundcheck dir</c>).</summary>
public static class Soundcheck
{
    public static int Run(string outDir)
    {
        System.IO.Directory.CreateDirectory(outDir);
        int bad = Water(outDir);
        // --water-sounds: only those (#380), for listening to the water by its numbers
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--water-sounds") >= 0) return Verdict(bad);

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

        var profiles = new (string name, EngineProfile p)[] { ("plane", EngineProfile.PistonAero), ("heli", EngineProfile.Turboshaft), ("turbofan", EngineProfile.Turbofan), ("turboprop", EngineProfile.Turboprop),
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
        return Verdict(bad);
    }

    /// <summary>The RESULT line (tools/test.sh reads it) and the exit code: a NaN or a clipped sample fails.</summary>
    private static int Verdict(int bad)
    {
        GD.Print(bad == 0 ? "[soundcheck] RESULT: ok" : $"[soundcheck] RESULT: FAILED ({bad} files clip or are not finite)");
        return bad > 0 ? 1 : 0;
    }

    /// <summary>
    /// The water's sounds as they are played (#301, #303, #380): every variant of the swim, wade and
    /// hull banks; the steamer's paddles at the pitch the rig gives them at quarter, half and full
    /// shaft; its whistle; its engine through each voice from STOP up to full ahead and back to
    /// slow, at the level <c>PlayerFeel</c> gives it.
    /// </summary>
    private static int Water(string outDir)
    {
        int bad = 0;
        string Path(string name) => System.IO.Path.Combine(outDir, $"water_{name}.wav");
        foreach (var bank in new[] { SfxSynth.SplashBank, SfxSynth.StrokeBank, SfxSynth.GaspBank, SfxSynth.WadeBank, SfxSynth.HullSlapBank })
            for (int i = 0; i < bank.Variants.Length; i++)
                bad += Save(Path($"{bank.Name}_{i}"), Decode(bank.Variants[i]));
        var paddles = Decode(SfxSynth.Paddles);
        foreach (float shaft in new[] { 0.25f, 0.5f, 1f })
            bad += Save(Path($"paddles_{Mathf.RoundToInt(shaft * 100)}"), Repitch(paddles, SfxSynth.PaddlePitch(shaft), 4f));
        bad += Save(Path("whistle"), Blast(Decode(SfxSynth.Whistle), 2f, 3f));
        foreach (EngineVoice voice in Enum.GetValues<EngineVoice>())
        {
            var synth = new EngineSynth(Player.Steamer.SteamEngine, spatial: false, seed: 3) { VoiceOverride = voice };
            var all = new List<float>();
            const float chunkS = 0.05f;
            var buf = new float[(int)(chunkS * Dsp.Rate)];
            for (float t = 0; t < 26f - 1e-4f; t += chunkS)
            {
                // the shaft as the telegraph spools it (0.08/s): stop, up to full ahead, held, eased to slow
                float shaft = t < 1f ? 0f : t < 13.5f ? (t - 1f) * 0.08f : t < 19f ? 1f : Mathf.Max(0.25f, 1f - (t - 19f) * 0.08f);
                synth.Render(buf, shaft, shaft > 0.02f ? 1f : 0f, shaft, shaft > 0.02f ? 0.18f + 0.25f * shaft : 0f);
                all.AddRange(buf);
            }
            bad += Save(Path($"steam_engine_{voice.ToString().ToLowerInvariant()}"), all.ToArray());
            synth.Free();
        }
        return bad;
    }

    /// <summary>
    /// A blast of the whistle as the rig plays it: the valve open <paramref name="held"/> seconds,
    /// with <see cref="SfxSynth.WhistleShape"/>'s rise and dying fall (gain and pitch per 1/60 s frame).
    /// </summary>
    private static float[] Blast(float[] loop, float held, float seconds)
    {
        var s = new float[(int)(seconds * Dsp.Rate)];
        double at = 0;
        for (int i = 0; i < s.Length; i++)
        {
            float t = (float)i / Dsp.Rate, frame = Mathf.Floor(t * 60f) / 60f;
            var (gain, pitch) = SfxSynth.WhistleShape(frame < held ? frame : -1f, frame < held ? 0f : frame - held);
            at = (at + pitch) % loop.Length;
            int a = (int)at;
            float f = (float)(at - a);
            s[i] = (loop[a] * (1f - f) + loop[(a + 1) % loop.Length] * f) * gain;
        }
        return s;
    }

    /// <summary>A loop played at <paramref name="pitch"/> for <paramref name="seconds"/> (linear interpolation, as a player resamples).</summary>
    private static float[] Repitch(float[] loop, float pitch, float seconds)
    {
        var s = new float[(int)(seconds * Dsp.Rate)];
        for (int i = 0; i < s.Length; i++)
        {
            double at = i * (double)pitch % loop.Length;
            int a = (int)at;
            float f = (float)(at - a);
            s[i] = loop[a] * (1f - f) + loop[(a + 1) % loop.Length] * f;
        }
        return s;
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
