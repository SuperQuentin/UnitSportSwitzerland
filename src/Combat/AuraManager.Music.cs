using Godot;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// The music's beat (#914): a battle that starts within <see cref="RadioManager.DanceRadius"/> of a
/// radio playing a shared CD (one in the world, a church's, or one a player carries) keeps that CD's
/// beat, folded into <see cref="AuraRules.MinBpm"/>..<see cref="AuraRules.MaxBpm"/>, its bars counted
/// from the CD's downbeat. The grid is taken once, at the start, and sent with <c>Begin</c>: every
/// peer then counts it on <see cref="ClockSync.ServerNow"/>, as the radio itself does.
/// </summary>
public partial class AuraManager
{
    private void PickBeat(Match m)
    {
        double now = ClockSync.ServerNow;
        // the server's NearestMusic leaves out carried radios (no speakers here): those are checked below
        var best = RadioManager.Instance?.NearestMusic(m.CentreWorld, RadioManager.DanceRadius, heard: false);
        float bestDist = best is { } b && IsInstanceValid(b.Source) ? b.Source.GlobalPosition.DistanceTo(m.CentreWorld) : float.MaxValue;
        int cdId = best?.CdId ?? 0;
        double startedAt = best?.StartedAt ?? 0;
        if (GetTree() is { } tree)
            foreach (var s in PlayerSnapshot.Of(tree))
            {
                if (RadioPlay.Decode(s.Player.HeldRadio) is not { } play || !play.Sounding(now)) continue;
                float d = s.Pos.DistanceTo(m.CentreWorld);
                if (d >= bestDist || d > RadioManager.DanceRadius) continue;
                bestDist = d;
                cdId = play.CdId;
                startedAt = play.StartedAt;
            }
        if (cdId == 0 || Audio.Cd.CdLibrary.Instance?.Find(cdId) is not { } cd || !(cd.Bpm > 1f)) return;
        if (now - startedAt > cd.Duration) return;
        float bpm = AuraRules.FoldBpm(cd.Bpm);
        double native = 60.0 / cd.Bpm;
        // the CD's first downbeat: its first beat, moved on to the bar's one; folding keeps it a bar line
        m.Rules.Anchor = startedAt + cd.BeatOffset + (cd.Analysis?.Downbeat ?? 0) * native;
        m.Rules.Beat = 60.0 / bpm;
        m.Music = cd.Title;
        GD.Print($"[aura] #{m.Id} keeps the beat of '{cd.Title}': {cd.Bpm:0.#} BPM, played at {bpm:0.#}");
    }
}
