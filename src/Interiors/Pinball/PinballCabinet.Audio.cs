using Godot;

namespace UnitSport.Interiors.Pinball;

// A cabinet's voice (#1325): the table's events become its sounds, from the playfield (a few voices shared
// round robin), the ball's roll a loop pitched by its speed; the electro-mechanical table rings its chimes
// and steps its reels, the solid-state tables play their jingles on the rules' cues.
public partial class PinballCabinet
{
    private const int Voices = 4;
    private AudioStreamPlayer3D[]? _voices;
    private AudioStreamPlayer3D? _roll;
    private int _next;

    /// <summary>The flippers at rest (for a machine nobody plays).</summary>
    public float[] RestAngles => _rest ??= Table.Flippers.Select(f => f.Rest).ToArray();
    private float[]? _rest;

    private void Play(AudioStream stream, float db = 0, float pitch = 1, Vector3? at = null)
    {
        if (_voices == null)
        {
            _voices = new AudioStreamPlayer3D[Voices];
            for (int i = 0; i < Voices; i++)
            {
                _voices[i] = new AudioStreamPlayer3D { Name = $"Voice{i}", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 30f };
                AddChild(_voices[i]);
            }
        }
        var v = _voices[_next++ % Voices];
        v.Stream = stream;
        v.VolumeDb = db;
        v.PitchScale = pitch;
        v.Position = at ?? FieldFront + new Vector3(0, 0.05f, -0.4f);
        v.Play();
    }

    /// <summary>One of the table's events heard: a switch, a drain, a launch, a flipper press.</summary>
    public void Hear(PinballSolver.Event e)
    {
        switch (e.Kind)
        {
            case PinballSolver.EventKind.Drain: Play(PinballSounds.Drain, -2); return;
            case PinballSolver.EventKind.Launch: Play(PinballSounds.Plunge, -4, 1, new Vector3(Table.ShooterX, 0.83f, Front)); return;
            case PinballSolver.EventKind.TiltWarn: Play(PinballSounds.Knock, -8, 1.6f); return;
            case PinballSolver.EventKind.RampFail: return;
        }
        if (e.Id == PinballGame.LeftPress || e.Id == PinballGame.RightPress)
        {
            var f = Table.Flippers[e.Id == PinballGame.LeftPress ? 0 : 1];
            Play(PinballSounds.Flip, -3, 1, ToPiece(new Vector3(f.Pivot.X, f.Pivot.Y, 0)));
            return;
        }
        if (e.Id < 0 || e.Id >= Table.Switches.Count) return;
        var kind = Table.Switches[e.Id].Kind;
        var stream = kind switch
        {
            SwitchKind.Bumper => PinballSounds.Bumper, SwitchKind.Sling => PinballSounds.Sling,
            SwitchKind.Target => PinballSounds.Target, SwitchKind.Drop => PinballSounds.Drop,
            SwitchKind.Spinner => PinballSounds.Spin, SwitchKind.Scoop => PinballSounds.Kick,
            SwitchKind.Ramp => PinballSounds.Reel, _ => null,
        };
        if (stream != null) Play(stream, kind == SwitchKind.Spinner ? -10 : -4);
        if (Table.Era != PinballEra.Reels) return;
        // the electro-mechanical table: a chime by the points, and the reel stepping
        var chime = kind switch
        {
            SwitchKind.Sling or SwitchKind.Rollover or SwitchKind.Spinner => PinballSounds.Chime10,
            SwitchKind.Bumper or SwitchKind.Inlane or SwitchKind.TopLane => PinballSounds.Chime100,
            SwitchKind.Target or SwitchKind.Drop or SwitchKind.Ramp or SwitchKind.Orbit or SwitchKind.Scoop or SwitchKind.Outlane => PinballSounds.Chime1000,
            _ => null,
        };
        if (chime != null) Play(chime, -9, 1, FieldFront + new Vector3(0, 0.6f, -1.1f));
    }

    /// <summary>A rules cue: "mission", "multiball", "jackpot", "knock", "save", "tilt", "warn".</summary>
    public void HearCue(string cue)
    {
        var top = FieldFront + new Vector3(0, 0.7f, -1.15f);
        switch (cue)
        {
            case "knock": Play(PinballSounds.Knock, -1); break;
            case "tilt": Play(PinballSounds.Tilt, -4, 1, top); break;
            case "save": Play(Table.Era == PinballEra.Reels ? PinballSounds.Chime1000 : PinballSounds.Save, -6, 1, top); break;
            case "mission": Play(Table.Era == PinballEra.Reels ? PinballSounds.Chime100 : PinballSounds.Jingle, -6, 1, top); break;
            case "multiball" or "jackpot": Play(Table.Era == PinballEra.Reels ? PinballSounds.Chime1000 : PinballSounds.Fanfare, -5, 1, top); break;
        }
    }

    /// <summary>The ball's roll, pitched and swelled by its speed (0 stops it).</summary>
    public void Rolling(float speed, bool onWire)
    {
        if (_roll == null)
        {
            if (speed < 0.05f) return;
            _roll = new AudioStreamPlayer3D { Name = "Roll", Bus = Audio.SfxBus.Name, UnitSize = 3f, MaxDistance = 20f, Stream = PinballSounds.Roll, Position = FieldFront + new Vector3(0, 0.05f, -0.5f) };
            AddChild(_roll);
        }
        var want = onWire ? PinballSounds.Wire : PinballSounds.Roll;
        if (_roll.Stream != want) _roll.Stream = want;
        if (speed < 0.05f)
        {
            if (_roll.Playing) _roll.Stop();
            return;
        }
        _roll.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(speed / 3f, 0.05f, 1f)) - 8f;
        _roll.PitchScale = Mathf.Clamp(0.6f + speed * 0.25f, 0.6f, 1.6f);
        if (!_roll.Playing) _roll.Play();
    }
}
