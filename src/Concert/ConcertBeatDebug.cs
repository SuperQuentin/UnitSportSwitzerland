using Godot;
using UnitSport.Audio.Cd;

namespace UnitSport.Concert;

/// <summary>
/// <c>/concert debug</c> (#885): what the light desk hears and decides, live, over the nearest concert.
/// Eight seconds of the song's analysis scrolling (loudness, kick, snare, hats, bass, brightness) with the
/// beats and bars ruled across, the whole song below it (its sections, builds and drops, the playhead),
/// and the desk's choices now: section, bar, phrase pattern, how hard the dimmers pump, the effects
/// firing. Drawn, not built of controls; its text changes five times a second.
/// </summary>
public partial class ConcertBeatDebug : CanvasLayer
{
    public static ConcertBeatDebug? Instance { get; private set; }

    private const int History = 240;   // 8 s at 30 Hz
    private const float SampleDt = 1f / 30f;
    private static readonly string[] LaneNames = { "level", "kick", "snare", "hats", "bass", "bright", "voice", "melody" };
    private const int Lanes = 8;
    private static readonly Color[] LaneColors =
    {
        new(0.85f, 0.85f, 0.85f), new(1f, 0.35f, 0.3f), new(1f, 0.85f, 0.3f), new(0.4f, 0.9f, 1f), new(0.6f, 0.45f, 1f), new(1f, 0.6f, 0.95f),
        new(0.5f, 1f, 0.5f), new(1f, 0.7f, 0.3f),
    };
    private static readonly Color[] SectionColors =
    {
        new(0.2f, 0.35f, 0.7f), new(0.2f, 0.6f, 0.35f), new(0.85f, 0.35f, 0.2f), new(0.9f, 0.75f, 0.2f),
    };

    private static readonly float[] LegendX = { 0f, 43f, 96f, 131f };
    private readonly float[][] _lanes = new float[Lanes][];
    private readonly float[] _beats = new float[History];
    private readonly Vector2[][] _lines = new Vector2[Lanes][];
    private int _head, _count;
    private double _lastSample, _lastText;
    private Panel _panel = null!;
    private Control _canvas = null!;
    private readonly string[] _text = { "", "", "", "", "" };
    private ConcertRig? _rig;
    private Fixture[][] _fx = Array.Empty<Fixture[]>();
    private static readonly RigGroup[] FxGroups =
    {
        RigGroup.Co2, RigGroup.Flames, RigGroup.Gerbs, RigGroup.Confetti, RigGroup.Airbursts, RigGroup.Strobes, RigGroup.Blinders,
        RigGroup.IrHeads, RigGroup.IrFloods, RigGroup.Wristbands,
    };

    public ConcertBeatDebug()
    {
        Name = "ConcertBeatDebug";
        Layer = 12;
        Visible = false;
        for (int i = 0; i < Lanes; i++)
        {
            _lanes[i] = new float[History];
            _lines[i] = new Vector2[History];
        }
    }

    public override void _Ready()
    {
        Instance = this;
        _panel = new Panel
        {
            Position = new Vector2(16, 70), Size = new Vector2(760, 580), MouseFilter = Control.MouseFilterEnum.Ignore,
            SelfModulate = new Color(1, 1, 1, 0.88f),
        };
        AddChild(_panel);
        _canvas = new Control { Size = _panel.Size, MouseFilter = Control.MouseFilterEnum.Ignore };
        _canvas.Draw += Paint;
        _panel.AddChild(_canvas);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary><c>/concert debug [on|off]</c>.</summary>
    public string Command(string[] args)
    {
        bool on = args.Length > 1 ? args[1] != "off" : !Visible;
        Visible = on;
        _count = 0;
        return on ? "Beat analysis window on (/concert debug off to close)." : "Beat analysis window off.";
    }

    private ConcertRig? NearestRig()
    {
        var eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        ConcertRig? best = null;
        float bestD = float.MaxValue;
        foreach (var r in ConcertRig.Live.Values)
        {
            if (!IsInstanceValid(r) || !r.IsInsideTree()) continue;
            float d = r.GlobalPosition.DistanceSquaredTo(eye);
            if (d < bestD) { bestD = d; best = r; }
        }
        return best;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var rig = NearestRig();
        if (rig != _rig)
        {
            _rig = rig;
            _count = 0;
            _fx = rig?.Plan is { } plan
                ? FxGroups.Select(g => plan.Patch.Fixtures.Where(f => f.Group == g).ToArray()).ToArray()
                : Array.Empty<Fixture[]>();
        }
        double now = Time.GetTicksMsec() / 1000.0;   // presentation: the window's own scroll
        if (rig != null && now - _lastSample >= SampleDt)
        {
            _lastSample = now;
            var m = rig.Desk.Music;
            _lanes[0][_head] = m?.Level ?? 0f;
            _lanes[1][_head] = m?.Kick ?? 0f;
            _lanes[2][_head] = m?.Snare ?? 0f;
            _lanes[3][_head] = m?.Hat ?? 0f;
            _lanes[4][_head] = m is { HasBands: true } ? m.Value.Bass : 0f;
            _lanes[5][_head] = m is { HasBands: true } ? m.Value.Bright : 0f;
            // the singer (#1153): the voice's level, the sung note from MIDI 40 to 85 (nothing when unvoiced)
            _lanes[6][_head] = m?.Melody.Vocal ?? 0f;
            _lanes[7][_head] = m is { Melody.Pitch: > 0f } mp ? Math.Clamp((mp.Melody.Pitch - 40f) / 45f, 0f, 1f) : 0f;
            _beats[_head] = rig.Desk.Beat;
            _head = (_head + 1) % History;
            _count = Math.Min(History, _count + 1);
        }
        if (now - _lastText >= 0.2)
        {
            _lastText = now;
            UpdateText(rig);
        }
        _canvas.QueueRedraw();
    }

    private void UpdateText(ConcertRig? rig)
    {
        if (rig == null)
        {
            _text[0] = "No concert near.";
            _text[1] = _text[2] = _text[3] = _text[4] = "";
            return;
        }
        var d = rig.Desk;
        var song = rig.Music.Song;
        double t = Net.ClockSync.ServerNow - rig.Music.SongStartedAt;
        var m = d.Music;
        _text[0] = song == null
            ? FormattableString.Invariant($"{rig.Event.Artist}: no song (the desk makes its own at {rig.Event.Bpm:F0} bpm)")
            : FormattableString.Invariant($"{song.Title}  {song.Bpm:F1} bpm  {(int)t / 60}:{(int)t % 60:D2} / {(int)song.Duration / 60}:{(int)song.Duration % 60:D2}  analysis v{song.Analysis?.Version ?? 0}");
        int beatN = (int)MathF.Floor(d.Beat);
        _text[1] = FormattableString.Invariant(
            $"section {d.Section}{(d.HeldSection != null ? " (held)" : "")}  bar {d.SectionBar}{(d.LastSection ? " (last)" : "")}  beat {((beatN % 4) + 4) % 4 + 1}/4  phrase {d.PhraseNow}  pump {d.PumpNow:F2}{(d.Strobing ? "  STROBE" : "")}");
        _text[2] = m is { } mm
            ? FormattableString.Invariant($"build {(mm.Build > 0 ? FormattableString.Invariant($"{mm.Build * 100:F0}%") : "-")}  since drop {(float.IsInfinity(mm.SinceDrop) ? "-" : FormattableString.Invariant($"{mm.SinceDrop:F1} s"))}  burst {mm.Burst:F2}{(mm.HasBands ? "" : "  (no bands: an older analysis)")}")
            : "no music: made-up songs, the beat grid only";
        var p = rig.Plan!.Patch;
        var firing = new System.Text.StringBuilder("firing:");
        for (int g = 0; g < _fx.Length; g++)
        {
            bool on = false;
            foreach (var f in _fx[g])
                on |= FxGroups[g] switch
                {
                    RigGroup.Strobes or RigGroup.IrHeads or RigGroup.IrFloods or RigGroup.Wristbands => p.Get01(f, Attr.Dimmer) > 0.05f,
                    RigGroup.Blinders => p.Get(f, 0) > 12,
                    _ => p.Get01(f, Attr.Fire) > 0.5f,
                };
            if (on) firing.Append(' ').Append(FxGroups[g]);
        }
        _text[3] = firing.Length > 7 ? firing.ToString() : "firing: nothing";
        _text[4] = m is { Melody: { Has: true } ml }
            ? FormattableString.Invariant($"voice {ml.Vocal:F2}  note {(ml.Pitch > 0 ? FormattableString.Invariant($"{ml.Pitch:F1}") : "-")}  run {ml.Rise:+0.00;-0.00;0}  {(ml.Hold > 0 ? "LONG NOTE  " : "")}since in {(float.IsInfinity(ml.SinceIn) ? "-" : FormattableString.Invariant($"{ml.SinceIn:F1} s"))}{(d.OnSinger ? "  SPOT ON SINGER" : "")}  ({song?.Analysis?.VocalSource})")
            : "no vocal analysis (older than v3)";
    }

    private void Paint()
    {
        var font = ThemeDB.FallbackFont;
        const int fs = 14;
        float w = _panel.Size.X - 24f, x0 = 12f;
        for (int i = 0; i < 5; i++) _canvas.DrawString(font, new Vector2(x0, 22 + i * 18), _text[i], HorizontalAlignment.Left, -1, fs, i == 0 ? Colors.White : new Color(0.85f, 0.85f, 0.85f));

        // the lanes: 8 s scrolling right to left, the newest on the right
        float top = 114f, laneH = 40f, gap = 4f, graphW = w - 70f;
        int n = _count;
        for (int l = 0; l < Lanes; l++)
        {
            float y0 = top + l * (laneH + gap);
            _canvas.DrawRect(new Rect2(x0 + 60, y0, graphW, laneH), new Color(0, 0, 0, 0.35f));
            _canvas.DrawString(font, new Vector2(x0, y0 + 24), LaneNames[l], HorizontalAlignment.Left, -1, fs, LaneColors[l]);
            if (n < 2) continue;
            var pts = _lines[l];
            for (int k = 0; k < n; k++)
            {
                int idx = (_head - n + k + History) % History;
                pts[k] = new Vector2(x0 + 60 + graphW * (History - n + k) / (History - 1), y0 + laneH - _lanes[l][idx] * (laneH - 2));
            }
            // the full buffer once 8 s have been heard: no allocation from then on
            _canvas.DrawPolyline(n == History ? pts : pts[..n], LaneColors[l], 1.5f);
        }
        // the beats ruled across every lane, the bars brighter
        for (int k = 1; k < n; k++)
        {
            int a = (_head - n + k - 1 + History) % History, b = (_head - n + k + History) % History;
            int ba = (int)MathF.Floor(_beats[a]), bb = (int)MathF.Floor(_beats[b]);
            if (bb == ba) continue;
            float x = x0 + 60 + graphW * (History - n + k) / (History - 1);
            bool barLine = ((bb % 4) + 4) % 4 == 0;
            _canvas.DrawLine(new Vector2(x, top), new Vector2(x, top + Lanes * (laneH + gap) - gap), barLine ? new Color(1, 1, 1, 0.55f) : new Color(1, 1, 1, 0.15f), barLine ? 2f : 1f);
        }

        // the whole song: its sections, builds and drops, and where it is
        float ty = top + Lanes * (laneH + gap) + 14f, th = 22f;
        if (_rig?.Music.Song is { } song && song.Duration > 0)
        {
            float scale = (w - 60f) / song.Duration, sx = x0 + 60;
            _canvas.DrawString(font, new Vector2(x0, ty + 16), "song", HorizontalAlignment.Left, -1, fs, Colors.White);
            if (song.Analysis is { } a)
            {
                for (int s = 0; s < a.SectionStarts.Length; s++)
                {
                    float s0 = a.SectionStarts[s], s1 = s + 1 < a.SectionStarts.Length ? a.SectionStarts[s + 1] : song.Duration;
                    int kind = s < a.SectionKinds.Length ? Math.Clamp(a.SectionKinds[s], 0, 3) : 1;
                    _canvas.DrawRect(new Rect2(sx + s0 * scale, ty, (s1 - s0) * scale - 1, th), SectionColors[kind]);
                }
                for (int k = 0; k < a.Drops.Length; k++)
                {
                    float b0 = k < a.BuildStarts.Length ? a.BuildStarts[k] : a.Drops[k];
                    _canvas.DrawRect(new Rect2(sx + b0 * scale, ty + th + 2, (a.Drops[k] - b0) * scale, 5), new Color(1f, 0.9f, 0.3f));
                    _canvas.DrawRect(new Rect2(sx + a.Drops[k] * scale - 1.5f, ty - 4, 3, th + 12), new Color(1f, 0.2f, 0.2f));
                }
                // the melody under the sections: climbing runs orange, falling blue, long notes white
                Runs(a.Rises, new Color(1f, 0.6f, 0.2f), 9);
                Runs(a.Falls, new Color(0.3f, 0.5f, 1f), 9);
                Runs(a.Holds, Colors.White, 16);
                void Runs(float[] pairs, Color c, float dy)
                {
                    for (int k = 0; k + 1 < pairs.Length; k += 2)
                        _canvas.DrawRect(new Rect2(sx + pairs[k] * scale, ty + th + dy, Math.Max(1f, (pairs[k + 1] - pairs[k]) * scale), 4), c);
                }
            }
            float t = (float)(Net.ClockSync.ServerNow - _rig.Music.SongStartedAt);
            _canvas.DrawRect(new Rect2(sx + Math.Clamp(t, 0, song.Duration) * scale - 1, ty - 6, 2, th + 14), Colors.White);
            _canvas.DrawString(font, new Vector2(sx, ty + th + 36), "calm  groove  peak  chorus   yellow: build   red: drop   orange/blue: melody up/down   white: long note", HorizontalAlignment.Left, -1, 12, new Color(0.75f, 0.75f, 0.75f));
            for (int c = 0; c < 4; c++) _canvas.DrawRect(new Rect2(sx + LegendX[c], ty + th + 40, 6, 6), SectionColors[c]);
        }
    }
}
