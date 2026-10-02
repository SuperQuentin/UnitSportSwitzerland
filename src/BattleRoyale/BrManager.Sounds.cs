using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The match heard on a client (#231): the stings (<see cref="BrSounds"/>) when the plane's doors open,
/// when each circle starts to close and the final one, when you win or are out; a heartbeat, faster
/// as you weaken, while the zone hurts you; a whoosh as you leave the ramp. The stings and the
/// heartbeat follow <see cref="BrPrefs.Stings"/>.
/// </summary>
public partial class BrManager
{
    private AudioStreamPlayer? _stingPlayer, _heartPlayer, _fxPlayer;
    private int _stungPhase = -1;
    private bool _doorsStung;
    private double _nextBeat;

    private AudioStreamPlayer Player(ref AudioStreamPlayer? slot, string name)
    {
        if (slot == null) AddChild(slot = new AudioStreamPlayer { Name = name, Bus = Audio.SfxBus.Name });
        return slot;
    }

    /// <summary>A sting, over whatever sting was playing.</summary>
    private void Sting(AudioStream stream, float db = -2f)
    {
        if (!BrPrefs.Current.Stings || DisplayServer.GetName() == "headless") return;
        var p = Player(ref _stingPlayer, "Sting");
        p.Stream = stream;
        p.VolumeDb = db;
        p.Play();
    }

    /// <summary>A plain effect on this client (not a sting: always heard).</summary>
    private void Effect(AudioStream stream, float db = 0f, float pitch = 1f)
    {
        if (DisplayServer.GetName() == "headless") return;
        var p = Player(ref _fxPlayer, "Effect");
        p.Stream = stream;
        p.VolumeDb = db;
        p.PitchScale = pitch;
        p.Play();
    }

    /// <summary>Every frame on a client, after the zone is known.</summary>
    private void SoundTick(FootPlayer? me, ZoneState? zone)
    {
        if (!InMatch || _state.Phase != BrPhase.Playing)
        {
            _stungPhase = -1;
            _doorsStung = false;
            return;
        }
        double now = ClockSync.ServerNow;
        if (_aboard && _state.Flight is { } flight && flight.DoorsOpen(now) && !_doorsStung)
        {
            _doorsStung = true;
            Sting(BrSounds.Doors);
        }
        if (zone is { Shrinking: true } z && z.Phase != _stungPhase)
        {
            _stungPhase = z.Phase;
            Sting(z.Phase == ZoneSchedule.Phases ? BrSounds.Final : BrSounds.Closing);
        }

        // the heartbeat: only where the zone actually hurts, quicker as health runs out
        bool hurting = me != null && MeAlive && !me.Eliminated && zone is { Dps: > 0 } zh && zh.Outside(ZonePoint(me.GlobalPosition));
        double t = Time.GetTicksMsec() / 1000.0;
        if (hurting && t >= _nextBeat && BrPrefs.Current.Stings && DisplayServer.GetName() != "headless")
        {
            float weak = 1f - Mathf.Clamp(me!.Health / FootPlayer.MaxHealth, 0f, 1f);
            _nextBeat = t + Mathf.Lerp(0.95f, 0.5f, weak);
            var p = Player(ref _heartPlayer, "Heart");
            p.Stream = BrSounds.Heart;
            p.VolumeDb = Mathf.Lerp(-6f, 2f, weak);
            p.Play();
        }
    }
}
