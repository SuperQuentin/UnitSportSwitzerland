using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertsoundprobe</c> (#984), windowed, offline at a stadium with a forced show
/// (<c>--chunks &lt;main&gt;/terrain_chunks --at 2498751,1114890 --concert 13</c>): what the stadium sound
/// does where a listener stands. First the model alone at seven spots (pitch front, front of house, the
/// far end, the far stand, a side stand, behind the stage, outside the bowl): each hang's delay, level and
/// cutoff, the room's slaps and wet, the crowd's place. Then the local body is put at each spot and the
/// real chain is read: the speakers' volume and cutoff, what Hearing's rays say, the Concert and Crowd
/// buses' peaks, the master's level and spectral centroid. Then a timeline at front of house through the
/// next gap between songs (the crowd's roar and applause), the main-thread cost a frame, a RESULT line.
/// </summary>
public partial class ConcertSoundProbe : Node
{
    public static bool Requested => CmdArgs.Has("--concertsoundprobe");

    private static bool _started;
    private int _failed;
    private AudioEffectSpectrumAnalyzerInstance? _spectrum;

    /// <summary>Once per run, from the first concert's sound: the probe hangs off the root, so a rebuilt rig does not end it.</summary>
    public static void Start(Node from)
    {
        if (_started) return;
        _started = true;
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, new ConcertSoundProbe { Name = "ConcertSoundProbe" });
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private static void Log(FormattableString line) => GD.Print("[concertsound] " + FormattableString.Invariant(line));

    private void Check(bool ok, FormattableString what)
    {
        Log($"{(ok ? "ok  " : "FAIL")} {FormattableString.Invariant(what)}");
        if (!ok) _failed++;
    }

    private static ConcertSound? Playing()
    {
        foreach (var rig in ConcertRig.Live.Values)
            if (GodotObject.IsInstanceValid(rig) && rig.GetNodeOrNull<ConcertSound>("Sound") is { } s && s.Music.Song != null
                && s.Music.Speakers.Count > 0 && s.Music.Speakers[0].LoadedCd != 0)
                return s;
        return null;
    }

    private async Task Run()
    {
        ConcertSound? sound = null;
        for (int i = 0; i < 3000 && (sound == null || Ears.Body is not { } b0 || !b0.IsOnFloor()); i++)
        {
            sound = Playing();
            // a free camera at start: into the body, as the desk probe does
            if (Ears.Body == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (sound == null || Ears.Body == null || sound.GetParent() is not ConcertRig rig || rig.Plan is not { } plan)
        {
            Finish($"no player ({Ears.Body != null}), or no concert playing a CD ({sound != null})");
            return;
        }
        var e = rig.Event;
        var st = e.Stadium;
        Log($"{st.Name} ({st.Kind}), {e.Layout}, {sound.Music.Hangs.Count} hangs, room size {sound.Tone.Size} damp {sound.Tone.Damping} wet {sound.Tone.Wet} predelay {sound.Tone.PredelayMs} ms");

        // the spots, stadium frame
        var o = new Vector3(plan.Origin.X, 0f, plan.Origin.Y);
        var fwd = new Vector3(plan.Forward.X, 0f, plan.Forward.Y);
        var right = new Vector3(-fwd.Z, 0f, fwd.X);
        var front = new Vector2(st.FrontHalfU, st.FrontHalfV);
        var outer = ConcertMusic.OuterHalf(st);
        float ToEdge(Vector3 from, Vector3 dir, Vector2 half)
        {
            float tx = MathF.Abs(dir.X) > 1e-3f ? (MathF.Sign(dir.X) * half.X - from.X) / dir.X : float.MaxValue;
            float tz = MathF.Abs(dir.Z) > 1e-3f ? (MathF.Sign(dir.Z) * half.Y - from.Z) / dir.Z : float.MaxValue;
            return MathF.Min(tx, tz);
        }
        float farFront = ToEdge(o, fwd, front), farOuter = ToEdge(o, fwd, outer), sideFront = ToEdge(o + fwd * 35f, right, front);
        var spots = new (string Name, Vector3 At)[]
        {
            ("pitch front", o + fwd * 6f + Vector3.Up * 1.7f),
            ("front of house", o + fwd * 45f + Vector3.Up * 1.7f),
            ("far end", o + fwd * (farFront - 4f) + Vector3.Up * 1.7f),
            ("far stand", o + fwd * (farFront + 22f) + Vector3.Up * 14f),
            ("side stand", o + fwd * 35f + right * (sideFront + 18f) + Vector3.Up * 12f),
            ("behind stage", o - fwd * (e.Depth + 6f) + Vector3.Up * 1.7f),
            ("outside", o + fwd * (farOuter + 40f) + Vector3.Up * 1.7f),
        };

        Log($"-- the model (spot: per hang delay ms / dB / cutoff Hz; slaps ms@dB; wet; crowd dB)");
        var echoes = new Echo[4];
        var hangs = sound.Music.Hangs;
        Vector3 centre = Vector3.Zero, facing = Vector3.Zero;
        foreach (var h in hangs) { centre += h.Hang; facing += h.Facing; }
        centre /= Math.Max(1, hangs.Count);
        facing = facing.LengthSquared() > 0.1f ? facing.Normalized() : Vector3.Zero;
        var model = new Dictionary<string, PaHeard>();
        foreach (var (name, at) in spots)
        {
            var parts = new List<string>();
            PaHeard best = default;
            float bestDb = -999f;
            foreach (var h in hangs)
            {
                var heard = ConcertAcoustics.Hear(h, at, st.Kind == Terrain.Stadiums.VenueKind.OpenAir ? Vector2.Zero : outer);
                parts.Add(FormattableString.Invariant($"{heard.Delay * 1000f:F0}/{heard.Db:F1}/{heard.CutoffHz:F0}"));
                if (heard.Db > bestDb) { bestDb = heard.Db; best = heard; }
            }
            model[name] = best;
            int n = ConcertAcoustics.Echoes(centre, facing, at, st.Kind == Terrain.Stadiums.VenueKind.OpenAir ? Vector2.Zero : front, sound.Tone.ReflectDb, right, echoes);
            int taps = ConcertAcoustics.Taps(echoes.AsSpan(0, n), out var t1, out var t2);
            string slaps = taps == 0 ? "none" : taps == 1 ? FormattableString.Invariant($"{t1.DelayMs:F0}@{t1.Db:F1}") : FormattableString.Invariant($"{t1.DelayMs:F0}@{t1.Db:F1} {t2.DelayMs:F0}@{t2.Db:F1}");
            var (crowdDb, crowdCut) = ConcertAcoustics.CrowdAt(at, front, st.Kind == Terrain.Stadiums.VenueKind.OpenAir ? Vector2.Zero : outer);
            Log($"{name,-15} {string.Join("  ", parts)}  slaps {slaps}  wet {ConcertAcoustics.Wet(sound.Tone, bestDb):F3}  crowd {crowdDb:F1} dB <{crowdCut:F0} Hz");
        }
        Check(model["far stand"].Delay > model["front of house"].Delay && model["front of house"].Delay > model["pitch front"].Delay, $"the delay grows with the distance from the PA");
        Check(model["far end"].CutoffHz < model["pitch front"].CutoffHz, $"the air takes the highs on the way to the far end");
        Check(model["behind stage"].Db < model["pitch front"].Db - 6f, $"behind the stage is quieter ({model["behind stage"].Db:F1} against {model["pitch front"].Db:F1} dB)");
        Check(model["outside"].CutoffHz < 1000f && model["outside"].Db < model["far end"].Db, $"outside the bowl is muffled ({model["outside"].Db:F1} dB, {model["outside"].CutoffHz:F0} Hz)");

        // the real chain, the body at each spot
        int analyzer = AudioServer.GetBusEffectCount(0);
        AudioServer.AddBusEffect(0, new AudioEffectSpectrumAnalyzer { BufferLength = 0.5f, FftSize = AudioEffectSpectrumAnalyzer.FftSizeEnum.Size2048 });
        _spectrum = AudioServer.GetBusEffectInstance(0, analyzer) as AudioEffectSpectrumAnalyzerInstance;
        Log($"-- in the game (spot: per hang volume dB / cutoff / path; Concert and Crowd bus peaks; master level and centroid)");
        var levels = new Dictionary<string, (float Pa, float Level, float Centroid)>();
        foreach (var (name, at) in spots)
        {
            if (!Place(rig, at)) { Log($"{name,-15} could not be stood on"); continue; }
            await Wait(5);
            float pa = 0f, crowd = 0f, level = 0f, centroid = 0f;
            const int samples = 12;
            for (int i = 0; i < samples; i++)
            {
                pa += Peak(ConcertSound.BusName) / samples;
                crowd += Peak(ConcertSound.CrowdBus) / samples;
                var (l, c) = Spectrum();
                level += l / samples;
                centroid += c / samples;
                await Wait(0.25);
            }
            var parts = sound.Music.Speakers.Select(s => FormattableString.Invariant($"{s.VolumeDb:F1}/{s.AttenuationFilterCutoffHz:F0}/{s.HeardThrough}")).ToArray();
            var ear = Ears.Of(sound) is { } g ? sound.ToLocal(g) : Vector3.Zero;
            levels[name] = (pa, level, centroid);
            Log($"{name,-15} ear ({ear.X:F0},{ear.Y:F0},{ear.Z:F0})  {string.Join("  ", parts)}  slaps {sound.Slaps}: {sound.Slap1.DelayMs:F0}@{sound.Slap1.Db:F1} {sound.Slap2.DelayMs:F0}@{sound.Slap2.Db:F1}  wet {sound.Wet:F3}  bus Concert {pa:F1} dB Crowd {crowd:F1} dB  master {level:F1} dB, centroid {centroid:F0} Hz");
        }
        if (levels.TryGetValue("pitch front", out var near) && levels.TryGetValue("outside", out var outside))
        {
            Check(near.Pa > -50f, $"the PA is heard at the pitch front ({near.Pa:F1} dB on the Concert bus)");
            Check(outside.Centroid < near.Centroid, $"outside the bowl is darker (centroid {outside.Centroid:F0} against {near.Centroid:F0} Hz)");
        }

        // a jog from front of house towards the far end, 2.5 m/s: the delay grows ~7 ms a second, which
        // the speakers follow by pitch (a Doppler shift) instead of seeking
        Place(rig, spots[1].At);
        await Wait(4);
        float worst = 0f, pitchLo = 1f, pitchHi = 1f;
        int over = 0;
        var speaker0 = sound.Music.Speakers[0];
        float delayFrom = speaker0.Heard.Delay;
        for (int i = 0; i < 100 && Ears.Body is { } jogger; i++)
        {
            jogger.GlobalPosition += rig.GlobalBasis * (fwd * 0.25f);
            await Wait(0.1);
            if (double.IsNaN(speaker0.HeardPosition)) continue;
            float err = (float)Math.Abs(speaker0.WantedPosition - speaker0.HeardPosition);
            worst = MathF.Max(worst, err);
            if (err > 0.08f) over++;
            pitchLo = MathF.Min(pitchLo, speaker0.PitchScale);
            pitchHi = MathF.Max(pitchHi, speaker0.PitchScale);
        }
        Log($"jog 25 m: delay {delayFrom * 1000f:F0} -> {speaker0.Heard.Delay * 1000f:F0} ms, worst drift {worst * 1000f:F0} ms, {over} samples past the 80 ms seek line, pitch {pitchLo:F3}..{pitchHi:F3}");
        Check(over <= 2, $"a jog's growing delay is followed without seeking");

        // a timeline at front of house through the next gap between songs
        Place(rig, spots[1].At);
        Log($"-- timeline at front of house (song s or gap s, section, since drop; crowd roar/applause target and now, dB; buses)");
        float gapClap = -80f, songRoar = -80f, dropRoar = -80f;
        bool gap = false;
        for (int i = 0; i < 160; i++)
        {
            var m = sound.Music;
            var mo = m.Moment;
            string where = m.GapTime >= 0 ? FormattableString.Invariant($"gap {m.GapTime:F1}") : FormattableString.Invariant($"song {m.SongTime:F1}");
            Log($"{where,-11} {mo?.Section,-8} drop {(mo is { HasBands: true } b ? b.SinceDrop : float.PositiveInfinity):F1}  roar {sound.CrowdTarget.RoarDb:F1}/{sound.CrowdNow.RoarDb:F1} applause {sound.CrowdTarget.ApplauseDb:F1}/{sound.CrowdNow.ApplauseDb:F1}  Concert {Peak(ConcertSound.BusName):F1} Crowd {Peak(ConcertSound.CrowdBus):F1}");
            if (m.GapTime >= 0) { gap = true; gapClap = MathF.Max(gapClap, sound.CrowdNow.ApplauseDb); }
            else if (mo is { HasBands: true } d && d.SinceDrop < 3f) dropRoar = MathF.Max(dropRoar, sound.CrowdNow.RoarDb);
            else songRoar = MathF.Max(songRoar, sound.CrowdNow.RoarDb);
            if (gap && m.SongTime > 6) break;
            await Wait(2);
        }
        if (gap) Check(gapClap > -12f, $"the crowd applauds between songs ({gapClap:F1} dB)");
        else Log($"no gap between songs in the window");
        if (dropRoar > -80f) Check(dropRoar > songRoar, $"a drop gets a roar ({dropRoar:F1} against {songRoar:F1} dB)");
        double us = ConcertSound.CostFrames > 0 ? ConcertSound.CostTicks * 1e6 / Stopwatch.Frequency / ConcertSound.CostFrames : 0;
        double radio = ConcertSound.CostFrames > 0 ? ConcertSound.RadioTicks * 1e6 / Stopwatch.Frequency / ConcertSound.CostFrames : 0;
        Log($"main thread: {us:F1} us a frame in the concerts' sound and speakers, of which {radio:F1} us the radio speakers' own work ({ConcertSound.CostFrames} frames, {ConcertRig.Live.Count} concerts)");
        Finish(null);
    }

    /// <summary>The local body at a spot (stadium frame), stood on whatever is under it.</summary>
    private static bool Place(ConcertRig rig, Vector3 at)
    {
        if (Ears.Body is not { } body) return false;
        // down from just over the spot (a stand's row, not the roof over it), else from high up
        var space = body.GetWorld3D().DirectSpaceState;
        var exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
        var want = rig.ToGlobal(at);
        Vector3? ground = null;
        foreach (var from in new[] { want + Vector3.Up * 2f, want + Vector3.Up * 40f })
        {
            var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 80f);
            query.Exclude = exclude;
            var hit = space.IntersectRay(query);
            if (hit.Count > 0) { ground = hit["position"].AsVector3(); break; }
        }
        if (ground == null) return false;
        body.GlobalPosition = ground.Value + Vector3.Up * 0.2f;
        body.Velocity = Vector3.Zero;
        return true;
    }

    private static float Peak(string bus)
    {
        int idx = AudioServer.GetBusIndex(bus);
        return idx < 0 ? -200f : MathF.Max(AudioServer.GetBusPeakVolumeLeftDb(idx, 0), AudioServer.GetBusPeakVolumeRightDb(idx, 0));
    }

    /// <summary>The master's level (dB) and spectral centroid (Hz), from twelve log bands 50 Hz..16 kHz.</summary>
    private (float Level, float Centroid) Spectrum()
    {
        if (_spectrum == null) return (-200f, 0f);
        float sum = 0f, weighted = 0f;
        for (int i = 0; i < 12; i++)
        {
            float lo = 50f * MathF.Pow(320f, i / 12f), hi = 50f * MathF.Pow(320f, (i + 1) / 12f);
            var m = _spectrum.GetMagnitudeForFrequencyRange(lo, hi);
            float energy = m.LengthSquared();
            sum += energy;
            weighted += energy * MathF.Sqrt(lo * hi);
        }
        return (10f * MathF.Log10(MathF.Max(sum, 1e-20f)), sum > 0f ? weighted / sum : 0f);
    }

    private void Finish(string? error)
    {
        if (error != null) { GD.PrintErr($"[concertsound] {error}"); _failed++; }
        GD.Print($"[concertsound] RESULT: {(_failed == 0 ? "ok" : $"FAILED ({_failed})")}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }
}
