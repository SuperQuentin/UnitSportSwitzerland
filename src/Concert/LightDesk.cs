using Godot;

namespace UnitSport.Concert;

/// <summary>What the show is doing now: the section of a song, or between them.</summary>
public enum ShowSection : byte { WalkIn, Calm, Groove, Chorus, Peak, Blackout }

/// <summary>
/// The virtual light desk (#885): every frame it writes the whole rig's DMX from the show's clock,
/// the way a programmed console plays back a show: groups, palettes, effects with a phase spread
/// across a group, a tempo master, cues by song section. It only ever writes the patch's universes;
/// the rig only reads them (<see cref="ConcertRig"/>), so a real console over Art-Net/sACN can take
/// its place later. A pure function of (event, show time): the same picture on every peer.
///
/// <para>
/// Until the music drives it (the CD analysis, a later phase), the songs are made up from the seed:
/// four minutes each at the event's tempo, sections of 16 bars in an order that builds (calm, groove,
/// chorus, groove, peak, chorus), a bar of blackout between songs.
/// </para>
/// </summary>
public sealed class LightDesk
{
    private readonly StagePlan _plan;
    private readonly ConcertEvent _e;
    private readonly Fixture[][] _groups;
    private readonly Color[] _pal;

    private static readonly ShowSection[] SongForm =
    {
        ShowSection.Calm, ShowSection.Groove, ShowSection.Chorus, ShowSection.Groove, ShowSection.Peak, ShowSection.Chorus,
    };
    private const int BarsPerSection = 16;

    public ShowSection Section { get; private set; }
    public float Beat { get; private set; }
    /// <summary>Bars since the section began, and whether it is the song's last section: what the effects key on.</summary>
    public int SectionBar { get; private set; }
    public bool LastSection { get; private set; }

    // the moment being written, for the delegates below
    private int _beatN;
    private int _n;

    // made once (perf-no-per-frame-allocations): each reads the moment from the fields above
    private readonly Func<int, Color> _walkInWash, _walkInPixels, _calmWash, _calmPixels, _grooveWash, _groovePixels,
        _chorusWash, _chorusPixels, _peakWash, _peakPixels;
    private readonly Func<int, float> _grooveChase, _full, _floorGroove;
    private readonly Func<int, Color> _buildPixels;
    /// <summary>The pixel bars' level now: the hats sparkle them (1 without the song's bands).</summary>
    private float _pixelGain = 1f, _buildFill;
    private bool _finale;

    /// <summary>
    /// How the heads of a group play together through a phrase (two bars), picked from the seed: all in
    /// sync, a wave across them, mirrored from the centre, odd against even, one in three, or two alone.
    /// The chorus's first bars, a build and a drop always sync everything up.
    /// </summary>
    public enum Phrase { Sync, Stagger, Mirror, Alternate, Sparse, Solo, Cycle, PingPong }
    private Phrase _phrase;

    /// <summary>The phrase playing (for the debug window), and how hard the dimmers pump.</summary>
    public Phrase PhraseNow => _phrase;
    public float PumpNow => _pump;
    private uint _phraseHash;

    /// <summary>
    /// How hard the dimmers pump with the rhythm (0 steady .. 1 dark between the hits) and the song's
    /// kick now: each head flashes on its beat and decays, on its own lag in a staggered phrase, on the
    /// song's kick drum when the rig plays in sync.
    /// </summary>
    private float _pump, _kick;

    public LightDesk(StagePlan plan)
    {
        _plan = plan;
        _e = plan.Event;
        _pal = _e.Palette;
        int groups = Enum.GetValues<RigGroup>().Length;
        _groups = new Fixture[groups][];
        for (int g = 0; g < groups; g++)
            _groups[g] = plan.Patch.Fixtures.Where(f => (int)f.Group == g).ToArray();
        var walkIn = _pal[0].Lerp(new Color(0.15f, 0.25f, 1f), 0.6f);
        _walkInWash = i => walkIn;
        _walkInPixels = i => _pal[0] * 0.25f;
        _calmWash = i => (i & 1) == 0 ? _pal[0] : _pal[2];
        _calmPixels = i => _pal[0] * (0.3f + 0.2f * MathF.Sin(Beat * 0.5f + i * 0.4f));
        _grooveWash = i => (i + _beatN) % 2 == 0 ? _pal[0] : _pal[1];
        _groovePixels = i => Chase(i, 8, Beat * 2f, 0.3f) > 0.5f ? _pal[1] : _pal[0] * 0.2f;
        _chorusWash = i => _pal[0];
        _chorusPixels = i => _pal[0];
        _peakWash = i => (_beatN & 1) == 0 ? _pal[1] : Colors.White;
        _peakPixels = i => (_beatN + i) % 2 == 0 ? Colors.White : _pal[1];
        _grooveChase = i => Chase(i, _n, Beat, 0.35f);
        _full = i => 1f;
        _floorGroove = i => 0.8f;
        _buildPixels = i => (i % 8) < _buildFill * 8f ? Colors.White : _pal[0] * 0.1f;
        // each IR head's place across the crowd: an end stage's heads by where they hang, left to
        // right (front truss and delay towers mixed), so a sweep draws one line; a round's along its side
        var ir = G(RigGroup.IrHeads);
        _irLateral = new float[ir.Length];
        var right = new Vector2(-_plan.Forward.Y, _plan.Forward.X);
        var order = Enumerable.Range(0, ir.Length)
            .OrderBy(i => (new Vector2(ir[i].Position.X, ir[i].Position.Z) - _plan.Origin).Dot(right)).ThenBy(i => i).ToArray();
        for (int k = 0; k < order.Length; k++)
        {
            var f = ir[order[k]];
            var h = new Vector2(f.Home.X, f.Home.Z).Normalized();
            _irLateral[order[k]] = _plan.Round
                ? Math.Clamp(new Vector2(f.Position.X, f.Position.Z).Dot(new Vector2(-h.Y, h.X)) / 12f, -1f, 1f)
                : (k + 0.5f) / order.Length * 2f - 1f;
        }
    }

    /// <summary>The section playing at a moment of the show (seconds since it started; negative before).</summary>
    /// <summary>
    /// What an operator's cues set (OSC from Chataigne or TouchDesigner, <see cref="ShowCue"/>), the
    /// same on every peer: a section held instead of the song's, a tempo from an instant of the shared
    /// clock (the beat counts on from where it was), a blackout, a master fader.
    /// </summary>
    public ShowSection? HeldSection { get; set; }
    /// <summary>The camera the operator holds on air, or -1 for the auto-director (<see cref="ConcertCameras"/>).</summary>
    public int Camera { get; set; } = -1;
    /// <summary>The song playing (<see cref="ConcertMusic"/>), or null: the desk then makes its songs up.</summary>
    public MusicMoment? Music { get; set; }
    public bool Blackout { get; set; }
    public float Master { get; set; } = 1f;
    private float? _bpm;
    private double _bpmAt;
    private float _beatAt;

    /// <summary>A new tempo from a moment of the show on: the beat goes on from where it was then.</summary>
    public void SetTempo(float bpm, double showSeconds)
    {
        _beatAt = BeatAt(showSeconds);
        _bpmAt = showSeconds;
        _bpm = bpm > 0 ? bpm : null;
    }

    private float BeatAt(double showSeconds) => _bpm is { } bpm
        ? _beatAt + (float)((showSeconds - _bpmAt) * bpm / 60.0)
        : (float)(showSeconds * _e.Bpm / 60.0);

    public ShowSection SectionAt(double showSeconds, out float beat)
    {
        if (Music is { } m && showSeconds >= 0)
        {
            // the music decides: its sections, its beat grid; a held section or a blackout still wins
            beat = m.Beat;
            _finale = m.Finale;
            SectionBar = m.SectionBar;
            LastSection = m.LastSection;
            if (Blackout) return ShowSection.Blackout;
            return HeldSection ?? m.Section;
        }
        beat = BeatAt(showSeconds);
        int bar = (int)MathF.Floor(beat / 4f);
        SectionBar = bar;
        LastSection = false;
        if (Blackout) return ShowSection.Blackout;
        if (HeldSection is { } held) return held;
        if (showSeconds < 0) return ShowSection.WalkIn;
        int songBars = SongForm.Length * BarsPerSection + 1;
        int inSong = bar % songBars;
        _finale = (bar / songBars) % 4 == 3;
        if (inSong == songBars - 1) return ShowSection.Blackout;
        SectionBar = inSong % BarsPerSection;
        LastSection = inSong / BarsPerSection == SongForm.Length - 1;
        // each song shuffles its form a little, from the seed
        int song = bar / songBars;
        int k = inSong / BarsPerSection;
        if (((_e.Seed >> (song % 16)) & 1) == 1 && k is 1 or 3) k = 4 - k;   // groove and chorus swap places
        return SongForm[k];
    }

    /// <summary>Writes every channel for this moment of the show.</summary>
    public void Write(double showSeconds)
    {
        _now = showSeconds;
        Section = SectionAt(showSeconds, out float beat);
        Beat = beat;
        float ph = beat - MathF.Floor(beat);           // within the beat
        float bar = beat / 4f, barPh = bar - MathF.Floor(bar);
        // a hit on every beat, decaying; the song's own kick drum when it plays
        float kick = Music is { } song ? Math.Max(song.Kick, 0.25f * MathF.Exp(-ph * 5f)) : MathF.Exp(-ph * 5f);
        int beatN = _beatN = (int)MathF.Floor(beat);
        _n = G(RigGroup.MidTruss).Length;
        // the song's finer bands (CdAnalysis v2): the bass under the washes, the brightness lifting the beams
        var bands = Music is { HasBands: true } mb ? mb : (MusicMoment?)null;
        float bass = bands?.Bass ?? 0.5f, lift = ((bands?.Bright ?? 0.5f) - 0.5f) * 20f;
        _pixelGain = bands is { } hb ? 0.45f + 1.1f * hb.Hat : 1f;
        // the tune (#1153): a climbing run lifts the heads and warms them, a falling one sinks and cools them;
        // a build or a drop owns the rig, the melody waits
        var melody = Music is { Melody.Has: true, Build: <= 0f, SinceDrop: >= 2.5f } mm ? mm.Melody : default;
        _melodyLift = MelodyLift * melody.Rise;
        _melodyTint = melody.Rise;
        uint ph32 = (uint)(_e.Seed * 7919 + (SectionBar / 2) * 104729 + (int)Section * 15485863 + (int)MathF.Floor(beat / 64f) * 32452843);
        ph32 ^= ph32 >> 13; ph32 *= 0x5bd1e995; ph32 ^= ph32 >> 15;
        _phraseHash = ph32;
        bool syncUp = Section == ShowSection.Chorus && SectionBar < 2 || Music is { Build: > 0f } || Music is { SinceDrop: < 4f };
        _phrase = syncUp || Section == ShowSection.Calm ? Phrase.Sync : (Phrase)(ph32 % 8);
        _kick = kick;
        _pump = Section switch
        {
            ShowSection.Calm => 0.2f, ShowSection.Groove => 0.65f, ShowSection.Chorus => 0.4f, ShowSection.Peak => 0.8f, _ => 0f,
        };
        var p = _plan.Patch;

        // the haze is always on during a show: it is what makes beams visible
        foreach (var f in G(RigGroup.Haze))
        {
            p.Set01(f, Attr.Pump, Section == ShowSection.WalkIn ? 0.25f : 0.6f);
            p.Set01(f, Attr.Fan, 0.5f);
        }

        switch (Section)
        {
            case ShowSection.WalkIn:
                Washes(0.35f, _walkInWash, tilt: 25f, sway: 6f, speed: 0.125f, beat);
                Off(RigGroup.FrontTruss); Off(RigGroup.MidTruss); Off(RigGroup.Floor);
                Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_walkInPixels);
                Screens(clip: 0, speed: 0.1f, _pal[0], _pal[1], 0.6f);
                break;

            case ShowSection.Blackout:
                foreach (var g in _groups) foreach (var f in g) if (f.Group != RigGroup.Haze) Dark(f);
                break;

            case ShowSection.Calm:
                Washes(0.55f, _calmWash, tilt: 20f, sway: 12f, speed: 0.5f, beat);
                // the spots pick out the artist: aimed at the stage's centre, warm
                Spots(0.8f, new Color(1f, 0.85f, 0.65f), aimCentre: true, beat, zoom: 0.25f);
                Off(RigGroup.MidTruss); Off(RigGroup.Floor); Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_calmPixels);
                Screens(clip: 1, speed: 0.2f, _pal[0], _pal[2], 0.8f);
                break;

            case ShowSection.Groove:
                Washes(0.45f + 0.25f * bass + 0.3f * kick, _grooveWash, tilt: 30f, sway: 15f, speed: 1f, beat);
                // some phrases a group sits out: the spots, or the floor
                if ((_phraseHash >> 8) % 4 == 0) Off(RigGroup.FrontTruss);
                else Spots(0.7f, _pal[2], aimCentre: false, beat, zoom: 0.4f);
                // the beams: a fan that breathes with the bar, a chase over it on every beat
                Beams(RigGroup.MidTruss, _phrase == Phrase.Sync ? _grooveChase : _full, _pal[1],
                    fan: 35f + 15f * MathF.Sin(bar * MathF.Tau), tilt: 25f + lift + 15f * MathF.Sin(beat * MathF.PI / 2f), open: true);
                if ((_phraseHash >> 12) % 3 == 0) Off(RigGroup.Floor);
                else Beams(RigGroup.Floor, _floorGroove, _pal[0], fan: 20f, tilt: 20f + 10f * MathF.Sin(beat * MathF.PI / 2f), open: true);
                Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_groovePixels);
                Screens(clip: 2, speed: _e.Bpm / 240f, _pal[0], _pal[1], 1f);
                break;

            case ShowSection.Chorus:
                Washes(1f, _chorusWash, tilt: 35f, sway: 20f, speed: 1f, beat);
                Spots(1f, Colors.White, aimCentre: false, beat, zoom: 0.6f);
                Beams(RigGroup.MidTruss, _full, _pal[(beatN / 4) % 2 == 0 ? 0 : 2], fan: 55f, tilt: 40f + lift, open: true);
                if (_phrase != Phrase.Sync && (_phraseHash >> 12) % 3 == 0) Off(RigGroup.Floor);
                else Beams(RigGroup.Floor, _full, Colors.White, fan: 40f, tilt: 10f + 25f * barPh, open: true);
                // the blinders hit the crowd on every downbeat
                Blinders(barPh < 0.25f ? MathF.Exp(-barPh * 12f) : 0f);
                Off(RigGroup.Strobes);
                Pixels(_chorusPixels);
                Screens(clip: 3, speed: _e.Bpm / 120f, _pal[0], Colors.White, 1f);
                break;

            case ShowSection.Peak:
                Washes(kick, _peakWash, tilt: 40f, sway: 30f, speed: 2f, beat);
                Spots(1f, _pal[2], aimCentre: false, beat, zoom: 0.8f);
                // a ballyhoo: every beat each beam jumps somewhere new, strobing
                Ballyhoo(RigGroup.MidTruss, beatN, _pal[1]);
                Ballyhoo(RigGroup.Floor, beatN + 7, Colors.White);
                Blinders((beatN & 3) == 0 ? kick : 0f);
                Strobes(0.7f, rate: 0.15f);
                Pixels(_peakPixels);
                Screens(clip: 4, speed: _e.Bpm / 60f, Colors.White, _pal[1], 1f);
                break;
        }
        if (bands is { } m && Section is not (ShowSection.Blackout or ShowSection.WalkIn)) Accents(m, beat);
        if (melody.Has && Section is not (ShowSection.Blackout or ShowSection.WalkIn)) Singer(melody, beat);
        StrobeBursts();
        Effects(beat);
        Bands(beat);
    }

    // ---- the crowd's wristbands (#986) ---------------------------------------------------------------

    private readonly float[] _irLateral;

    /// <summary>A slow song: its bands twinkle through the verses instead of staying dark.</summary>
    private bool Ballad => _e.Genre == Genre.Ballad || (_bpm ?? _e.Bpm) < 90f;

    /// <summary>
    /// The crowd's wristbands, by section: dark through the walk-in, between songs and in a song's calm
    /// verse (a ballad's verse twinkles slowly, at random, in the artist's colour); in a groove the IR
    /// heads sweep a line of colour across the floor and up the far stand and back, every eight bars; a
    /// chorus opens with every band on in the artist's colour (two bars), then chases the floods stand
    /// by stand on the beat; at a peak the heads jump to a new spot of the crowd on every beat while
    /// half the bands, a different half each time, flash on it. A build twinkles faster as it rises;
    /// the drop lights every band at once.
    /// </summary>
    private void Bands(float beat)
    {
        var heads = G(RigGroup.IrHeads);
        var floods = G(RigGroup.IrFloods);
        foreach (var f in heads) Dark(f);
        foreach (var f in floods) Dark(f);
        foreach (var f in G(RigGroup.Wristbands)) Dark(f);
        if (Section is ShowSection.WalkIn or ShowSection.Blackout) return;
        int beatN = (int)MathF.Floor(beat);
        if (Music is { SinceDrop: < 4f })
        {
            Transmit(_pal[0], 1f, Wristbands.Solid, 0f);
            return;
        }
        if (Music is { Build: > 0f } m)
        {
            Transmit(_pal[1], 0.4f + 0.6f * m.Build, Wristbands.Twinkle, m.Build);
            return;
        }
        switch (Section)
        {
            case ShowSection.Calm:
                if (Ballad) Transmit(_pal[0], 0.8f, Wristbands.Twinkle, 0.1f);
                break;
            case ShowSection.Groove:
            {
                // there and back in eight bars, a little past the floor's end: up the far stand
                float u = beat / 16f - MathF.Floor(beat / 32f) * 2f;
                float depth = (u < 1f ? u : 2f - u) * 1.15f;
                for (int i = 0; i < heads.Length; i++) IrSpot(heads[i], CrowdPoint(heads[i], depth, _irLateral[i]), _pal[1], 0.25f);
                break;
            }
            case ShowSection.Chorus:
                // a chorus held by the operator counts the bar on from the show's start: it opens again every 16 bars
                if ((HeldSection != null ? SectionBar % BarsPerSection : SectionBar) < 2)
                {
                    Transmit(_pal[0], 1f, Wristbands.Solid, 0f);
                    break;
                }
                // one stand a beat, the colour turning every two bars
                if (floods.Length > 0)
                {
                    var f = floods[(beatN % floods.Length + floods.Length) % floods.Length];
                    Paint(f, (SectionBar / 2) % 2 == 0 ? _pal[0] : _pal[2]);
                    _plan.Patch.Set01(f, Attr.Dimmer, 1f);
                    _plan.Patch.Set01(f, Attr.Zoom, 0.7f);
                }
                break;
            case ShowSection.Peak:
                Transmit(_pal[1], 1f, Wristbands.BeatFlash, 0f);
                for (int i = 0; i < heads.Length; i++)
                {
                    uint h = (uint)(beatN * 7919 + i * 104729 + _e.Seed);
                    h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                    IrSpot(heads[i], CrowdPoint(heads[i], (h % 1000) / 1000f, ((h >> 10) % 1000) / 500f - 1f), _pal[2], 0.35f);
                }
                break;
        }
    }

    /// <summary>
    /// A point of the crowd for an IR head: <paramref name="depth"/> 0 at the barrier, 1 at the floor's
    /// far end, past it up the stand; <paramref name="lateral"/> -1..1 across. In the round, out from
    /// the head's own side.
    /// </summary>
    private Vector3 CrowdPoint(Fixture f, float depth, float lateral)
    {
        if (!_plan.Round)
        {
            float z = 3f + depth * (_plan.CrowdAhead - 3f);
            return _plan.StagePoint(lateral * _plan.CrowdHalf * 0.8f, 1.3f + Math.Max(0f, z - _plan.CrowdAhead) * 0.6f, z);
        }
        var h = new Vector2(f.Home.X, f.Home.Z).Normalized();
        float front = MathF.Abs(h.X) > 0.5f ? _e.Stadium.FrontHalfU : _e.Stadium.FrontHalfV;
        float from = MathF.Abs(new Vector2(f.Position.X, f.Position.Z).Dot(h)) + 3f;
        float d = from + depth * (front - from);
        var p = h * d + new Vector2(-h.Y, h.X) * lateral * front * 0.8f;
        return new Vector3(p.X, 1.3f + Math.Max(0f, d - front) * 0.6f, p.Y);
    }

    /// <summary>An IR head on, aimed at a point, in a colour code, its beam <paramref name="zoom"/> open.</summary>
    private void IrSpot(Fixture f, Vector3 target, Color c, float zoom)
    {
        var d = (target - f.Position).Normalized();
        var up = f.Up;
        var fwd = f.Home - up * f.Home.Dot(up);
        fwd = fwd.LengthSquared() < 1e-6f ? up.Cross(Vector3.Right).Normalized() : fwd.Normalized();
        var right = up.Cross(fwd);
        // the inverse of ConcertRig.Direction; the pan the turn nearest to where the yoke is, never the long way round
        float tilt = Mathf.RadToDeg(MathF.Acos(Math.Clamp(d.Dot(up), -1f, 1f)));
        float pan = Mathf.RadToDeg(MathF.Atan2(d.Dot(right), d.Dot(fwd)));
        float was = (_plan.Patch.Get16(f, Attr.Pan, Attr.PanFine) - 0.5f) * f.Type.PanRange;
        pan += 360f * MathF.Round((was - pan) / 360f);
        if (MathF.Abs(pan) > f.Type.PanRange / 2f) pan -= 360f * MathF.Sign(pan);
        Aim(f, pan, Math.Min(tilt, f.Type.TiltRange / 2f));
        Paint(f, c);
        _plan.Patch.Set01(f, Attr.Dimmer, 1f);
        _plan.Patch.Set01(f, Attr.Zoom, zoom);
    }

    /// <summary>Every band at once, through the transmitter: a colour, a level, an effect the bands play.</summary>
    private void Transmit(Color c, float level, int effect, float rate)
    {
        foreach (var f in G(RigGroup.Wristbands))
        {
            Paint(f, c);
            _plan.Patch.Set01(f, Attr.Dimmer, level);
            _plan.Patch.Set(f, f.Type.Offset(Attr.Effect), Wristbands.EffectValue(effect));
            _plan.Patch.Set01(f, Attr.Rate, rate);
        }
    }

    /// <summary>
    /// What the song's finer analysis adds over the section's look: a snare hit pops the strobes, a
    /// build into a drop sinks the washes, lifts the beams to the sky and closes them, speeds the
    /// strobes up and fills the pixel bars; the drop hits the crowd with the blinders (and fires the
    /// CO2, the flames and the gerbs, <see cref="Effects"/>).
    /// </summary>
    private void Accents(MusicMoment m, float beat)
    {
        if (m.Build > 0f)
        {
            float b = m.Build;
            Washes(0.5f * (1f - b) + 0.05f, _grooveWash, tilt: 30f, sway: 15f * (1f - b), speed: 1f + b, beat);
            Beams(RigGroup.MidTruss, _full, Colors.White, fan: 30f * (1f - b) + 3f, tilt: 20f + 60f * b, open: true);
            Beams(RigGroup.Floor, _full, _pal[1], fan: 20f * (1f - b) + 2f, tilt: 10f + 70f * b, open: true);
            Strobes(b, rate: 0.15f + 0.85f * b);
            _buildFill = b;
            Pixels(_buildPixels);
            Off(RigGroup.Blinders);
        }
        else if (Section != ShowSection.Peak && m.Snare > 0.45f)
            Strobes(m.Snare, rate: 1f);
        if (m.SinceDrop < 2.5f)
            Blinders(MathF.Exp(-m.SinceDrop * 1.5f));
    }

    /// <summary>How far a climbing run lifts the washes and beams at its top note, degrees of tilt.</summary>
    private const float MelodyLift = 25f;
    /// <summary>The colours a run leans to: up warm, down cool.</summary>
    private static readonly Color RiseTint = new(1f, 0.55f, 0.15f), FallTint = new(0.2f, 0.35f, 1f);
    private float _melodyLift, _melodyTint;

    /// <summary>Whether the front spots are on the singer now (a long note, or the voice coming back), for the debug window.</summary>
    public bool OnSinger { get; private set; }

    /// <summary>
    /// The front spots find the singer (centre stage): through a long note, full and narrow; as the
    /// voice comes back after a break, snapped on and fading over four seconds.
    /// </summary>
    private void Singer(in MelodyMoment m, float beat)
    {
        float entry = m.SinceIn < 4f ? MathF.Exp(-m.SinceIn * 0.6f) : 0f;
        float on = Math.Max(m.Hold > 0f ? 1f : 0f, entry);
        OnSinger = on > 0.05f;
        if (OnSinger) Spots(on, new Color(1f, 0.92f, 0.8f), aimCentre: true, beat, zoom: 0.12f);
    }

    /// <summary><paramref name="c"/> leaned towards the melody's colour: warm up a run, cool down one.</summary>
    private Color Tinted(Color c) => _melodyTint switch
    {
        > 0f => c.Lerp(RiseTint, 0.45f * _melodyTint),
        < 0f => c.Lerp(FallTint, -0.45f * _melodyTint),
        _ => c,
    };

    /// <summary>Whether the rig is in a strobe burst now (the debug window shows it).</summary>
    public bool Strobing { get; private set; }

    /// <summary>A strobe burst's rate: 12 flashes a second (1 + 24 x rate), the heads' shutters as close as their scale allows.</summary>
    private const float BurstRate = 11f / 24f;
    private const byte BurstShutter = 83;

    /// <summary>
    /// Strobe bursts, where the music pushes hardest: the last quarter of a build, the drop's first
    /// moments, one bar in four at a peak. The strobes flash at 12 Hz and the moving heads' shutters
    /// with them; the screens sink and flash (the rig), the dancers jump pose on each flash.
    /// </summary>
    private void StrobeBursts()
    {
        bool live = Section is not (ShowSection.Blackout or ShowSection.WalkIn);
        Strobing = live && (Music is { Build: > 0.75f } || Music is { SinceDrop: < 0.8f } || Section == ShowSection.Peak && SectionBar % 4 == 3);
        if (!Strobing) return;
        Strobes(1f, rate: BurstRate);
        var p = _plan.Patch;
        foreach (var grp in new[] { RigGroup.FrontTruss, RigGroup.MidTruss, RigGroup.Floor })
            foreach (var f in G(grp))
                if (f.Type.Offset(Attr.Shutter) is int o and >= 0 && p.Get01(f, Attr.Dimmer) > 0.05f) p.Set(f, o, BurstShutter);
    }

    /// <summary>
    /// The special effects, cued by the song: CO2 on the first downbeats of a chorus, flames every two
    /// bars in it and on every beat (left, right) at a peak, gerbs as a peak begins, confetti as the
    /// song's last chorus begins, airbursts in the blackout that ends it, low fog through calm verses.
    /// Armed for the whole show.
    /// </summary>
    private void Effects(float beat)
    {
        var p = _plan.Patch;
        float ph = beat - MathF.Floor(beat), barPh = beat / 4f - MathF.Floor(beat / 4f);
        int beatN = (int)MathF.Floor(beat);
        bool live = Section is not (ShowSection.WalkIn);
        bool chorus = Section == ShowSection.Chorus, peak = Section == ShowSection.Peak;
        void Fire(RigGroup g, Func<int, bool> on)
        {
            var f = G(g);
            bool hand = Manual(g);
            for (int i = 0; i < f.Length; i++)
            {
                p.Set01(f[i], Attr.Arm, live || hand ? 1f : 0f);
                p.Set01(f[i], Attr.Fire, hand || live && on(i) ? 1f : 0f);
            }
        }
        _fx.Beat = beatN; _fx.BeatPh = ph; _fx.BarPh = barPh; _fx.Bar = SectionBar; _fx.Chorus = chorus; _fx.Peak = peak;
        _fx.Last = LastSection; _fx.Blackout = Section == ShowSection.Blackout; _fx.Finale = _finale;
        _fx.Drop = live && Music is { SinceDrop: < 0.6f };
        Fire(RigGroup.Co2, _fx.Co2);
        Fire(RigGroup.Flames, _fx.Flames);
        Fire(RigGroup.Gerbs, _fx.Gerbs);
        Fire(RigGroup.Confetti, _fx.Confetti);
        Fire(RigGroup.Airbursts, _fx.Airbursts);
        foreach (var f in G(RigGroup.LowFog))
        {
            p.Set01(f, Attr.Pump, Section == ShowSection.Calm ? 0.8f : 0f);
            p.Set01(f, Attr.Fan, 0.6f);
        }
    }

    /// <summary>The moment, for the effects' cues (made once: nothing per frame allocates).</summary>
    private sealed class FxMoment
    {
        public int Beat, Bar;
        public float BeatPh, BarPh;
        public bool Chorus, Peak, Last, Blackout, Drop, Finale;
        public readonly Func<int, bool> Co2, Flames, Gerbs, Confetti, Airbursts;

        public FxMoment()
        {
            // kept for the moments: a drop fires the CO2, the flames and the gerbs together; a chorus opens on
            // one CO2 shot and a flame every four bars; a peak, one flame a bar, left then right; the set's
            // last song ends on the fireworks
            Co2 = i => Drop || Chorus && Bar == 0 && BarPh < 0.06f;
            Flames = i => Drop && BeatPh < 0.3f || (Chorus && Bar % 4 == 0 && BarPh < 0.06f) || (Peak && BarPh < 0.06f && (Bar + i) % 2 == 0);
            Gerbs = i => Drop || Chorus && Last && Bar == 0 && BarPh < 0.1f;
            Confetti = i => Chorus && Last && Bar == 0;
            Airbursts = i => Blackout && Finale;
        }
    }

    private readonly FxMoment _fx = new();

    /// <summary>Effects fired by hand (a cue from the operator), each until a moment of the show.</summary>
    private readonly double[] _manual = new double[Enum.GetValues<RigGroup>().Length];
    private double _now;

    /// <summary>Fires an effect group by hand from a moment of the show: a held shot for the flames and CO2, one shot for the rest.</summary>
    public void FireNow(RigGroup group, double showSeconds)
    {
        double hold = group is RigGroup.Flames or RigGroup.Co2 ? 0.8 : 0.3;
        _manual[(int)group] = showSeconds + hold;
    }

    private bool Manual(RigGroup g) => _now < _manual[(int)g];

    // ---- palettes and effects, written as DMX ------------------------------------------------------

    private Fixture[] G(RigGroup g) => _groups[(int)g];

    /// <summary>A chase across a group: each fixture lit as the wave passes, <paramref name="width"/> of the group wide.</summary>
    private static float Chase(int i, int n, float beat, float width)
    {
        if (n <= 1) return 1f;
        // the wave crosses the whole group in two beats: a 28-head rig as fast as a 6-head one
        float pos = beat * 0.5f - MathF.Floor(beat * 0.5f), me = (float)i / n;
        float d = MathF.Abs(pos - me);
        d = MathF.Min(d, 1f - d);
        return MathF.Max(0f, 1f - d / width);
    }

    private void Dark(Fixture f)
    {
        var p = _plan.Patch;
        p.Set01(f, Attr.Dimmer, 0f);
        p.Set01(f, Attr.DimmerFine, 0f);
        foreach (int o in f.Type.CellOffsets) p.Set(f, o, 0);
        // a fixture with no dimmer (an imported LED par) goes dark by its colours
        if (f.Type.Offset(Attr.Dimmer) < 0)
            foreach (var a in DarkColours) p.Set01(f, a, 0f);
    }

    private static readonly Attr[] DarkColours = { Attr.Red, Attr.Green, Attr.Blue, Attr.White };

    private void Off(RigGroup g)
    {
        foreach (var f in G(g)) Dark(f);
    }

    /// <summary>Position as degrees: pan about the yoke, tilt away from its axis towards the fixture's home side.</summary>
    private void Aim(Fixture f, float panDeg, float tiltDeg)
    {
        var p = _plan.Patch;
        float pan = 0.5f + panDeg / f.Type.PanRange, tilt = 0.5f + tiltDeg / f.Type.TiltRange;
        // an inverted head (an MVR's DMXInvertPan/Tilt, #959) wants the mirrored value for the same angle
        p.Set16(f, Attr.Pan, Attr.PanFine, f.InvertPan ? 1f - pan : pan);
        p.Set16(f, Attr.Tilt, Attr.TiltFine, f.InvertTilt ? 1f - tilt : tilt);
    }

    private void Paint(Fixture f, Color c)
    {
        var p = _plan.Patch;
        switch (f.Type.Kind)
        {
            case FixtureKind.Wash:
                // RGBW: white takes what the three share
                float w = MathF.Min(c.R, MathF.Min(c.G, c.B));
                p.Set01(f, Attr.Red, c.R - w); p.Set01(f, Attr.Green, c.G - w); p.Set01(f, Attr.Blue, c.B - w); p.Set01(f, Attr.White, w);
                break;
            case FixtureKind.IrHead or FixtureKind.IrFlood or FixtureKind.Wristbands:
                // a band's colour code, plain RGB
                p.Set01(f, Attr.Red, c.R); p.Set01(f, Attr.Green, c.G); p.Set01(f, Attr.Blue, c.B);
                break;
            case FixtureKind.Spot:
                // CMY subtracts from white
                p.Set01(f, Attr.Cyan, 1f - c.R); p.Set01(f, Attr.Magenta, 1f - c.G); p.Set01(f, Attr.Yellow, 1f - c.B);
                p.Set01(f, Attr.ColorWheel, 0f);
                break;
            default:
                // a wheel: the nearest slot, in the middle of its 8 values
                if (f.Type.Offset(Attr.ColorWheel) is int o and >= 0) p.Set(f, o, (byte)(FixtureType.WheelSlot(c) * 8 + 4));
                break;
        }
    }

    private void Open(Fixture f, float dim, float zoom = 0.5f, float strobe = 0f)
    {
        var p = _plan.Patch;
        p.Set01(f, Attr.Dimmer, dim);
        // shutter: 8..15 open, 16..131 strobe slow to fast
        if (f.Type.Offset(Attr.Shutter) is int o and >= 0) p.Set(f, o, strobe <= 0 ? (byte)12 : (byte)(16 + strobe * 115));
        p.Set01(f, Attr.Zoom, zoom);
    }

    private void Washes(float dim, Func<int, Color> color, float tilt, float sway, float speed, float beat)
    {
        var g = G(RigGroup.BackTruss);
        for (int i = 0; i < g.Length; i++)
        {
            float a = (beat - Lag(i, g.Length)) * speed * MathF.Tau;
            Aim(g[i], MathF.Sin(a) * sway * Mirror(i, g.Length), tilt + _melodyLift + MathF.Cos(a) * sway * 0.5f);
            Paint(g[i], Tinted(color(i)));
            // the washes never go fully dark with a pattern: they light the stage
            Open(g[i], dim * (0.35f + 0.65f * Mask(i, g.Length)) * Rhythm(i, g.Length, 0.5f), zoom: 0.7f);
        }
    }

    private void Spots(float dim, Color color, bool aimCentre, float beat, float zoom)
    {
        var g = G(RigGroup.FrontTruss);
        for (int i = 0; i < g.Length; i++)
        {
            float off = g.Length <= 1 ? 0 : (i / (float)(g.Length - 1) - 0.5f);
            // a hung head over the deck's front: tilting back aims onto the stage
            if (aimCentre) Aim(g[i], -off * 50f, -30f);
            else
            {
                float w = (beat - Lag(i, g.Length)) * MathF.PI / 2f;
                Aim(g[i], off * 60f + MathF.Sin(w) * 14f * Mirror(i, g.Length), 30f + MathF.Cos(w * 0.5f) * 15f);
            }
            Paint(g[i], color);
            Open(g[i], aimCentre ? dim : dim * Mask(i, g.Length) * Rhythm(i, g.Length, 0.8f), zoom);
        }
    }

    private void Beams(RigGroup grp, Func<int, float> dim, Color color, float fan, float tilt, bool open)
    {
        var g = G(grp);
        for (int i = 0; i < g.Length; i++)
        {
            float off = g.Length <= 1 ? 0 : (i / (float)(g.Length - 1) - 0.5f);
            // each head swings on its own lag: a wave, a mirror, or all together
            float w = (Beat - Lag(i, g.Length)) * MathF.PI / 2f;
            float wave = _phrase == Phrase.Sync ? 6f : 12f;
            Aim(g[i], off * fan * 2f + MathF.Sin(w * 0.5f) * wave * Mirror(i, g.Length), tilt + _melodyLift + MathF.Sin(w) * wave * 0.6f);
            Paint(g[i], Tinted(color));
            Open(g[i], open ? dim(i) * Mask(i, g.Length) * Rhythm(i, g.Length, 1f) : 0f, zoom: 0.2f);
        }
    }

    /// <summary>How far behind the beat head <paramref name="i"/> of <paramref name="n"/> moves in this phrase, in beats.</summary>
    private float Lag(int i, int n) => n <= 1 ? 0f : _phrase switch
    {
        Phrase.Sync => 0f,
        Phrase.Stagger => 2f * i / n,
        Phrase.Mirror => 2f * MathF.Abs(i - (n - 1) / 2f) / n,
        Phrase.Cycle or Phrase.PingPong => 0f,
        _ => (i & 1) * 1f,
    };

    /// <summary>
    /// Head <paramref name="i"/>'s dimmer against the rhythm, 0..1: a flash on its beat (behind it by its
    /// lag) decaying to the next, or the song's kick when in sync; <paramref name="depth"/> of the
    /// section's pump (washes less than beams).
    /// </summary>
    private float Rhythm(int i, int n, float depth)
    {
        float d = _pump * depth;
        if (d <= 0f) return 1f;
        float env;
        if (_phrase == Phrase.Sync) env = _kick;
        else
        {
            float b = Beat - Lag(i, n);
            env = MathF.Exp(-(b - MathF.Floor(b)) * 3.5f);
        }
        return 1f - d + d * env;
    }

    private static int PingPong(int step, int n)
    {
        int k = step % (2 * n - 2);
        return k < n ? k : 2 * n - 2 - k;
    }

    /// <summary>The left half turns the other way in a mirrored phrase.</summary>
    private float Mirror(int i, int n) => _phrase == Phrase.Mirror && i < n / 2 ? -1f : 1f;

    /// <summary>Whether head <paramref name="i"/> of <paramref name="n"/> is lit in this phrase, on this beat.</summary>
    private float Mask(int i, int n) => n <= 1 ? 1f : _phrase switch
    {
        Phrase.Alternate => ((i + _beatN) & 1) == 0 ? 1f : 0f,
        Phrase.Sparse => i % 3 == ((_beatN % 3) + 3) % 3 ? 1f : 0f,
        Phrase.Solo => i == (int)(_phraseHash % (uint)n) || i == n - 1 - (int)(_phraseHash % (uint)n) ? 1f : 0f,
        // one head at a time round the row, a step every sixteenth (an eighth on a short row)
        Phrase.Cycle => i == (int)MathF.Floor(Beat * (n > 8 ? 4f : 2f)) % n ? 1f : 0f,
        Phrase.PingPong => i == PingPong((int)MathF.Floor(Beat * (n > 8 ? 4f : 2f)), n) ? 1f : 0f,
        _ => 1f,
    };

    private void Ballyhoo(RigGroup grp, int beatN, Color color)
    {
        var g = G(grp);
        for (int i = 0; i < g.Length; i++)
        {
            uint h = (uint)(beatN * 7919 + i * 104729 + _e.Seed);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            Aim(g[i], (h % 160) - 80f, 15f + (h >> 8) % 50);
            Paint(g[i], color);
            Open(g[i], 1f, zoom: 0.15f, strobe: 0.5f);
        }
    }

    private void Blinders(float level)
    {
        foreach (var f in G(RigGroup.Blinders))
        {
            _plan.Patch.Set01(f, Attr.Dimmer, 1f);
            foreach (int o in f.Type.CellOffsets) _plan.Patch.Set(f, o, (byte)(level * 255));
        }
    }

    private void Strobes(float dim, float rate)
    {
        foreach (var f in G(RigGroup.Strobes))
        {
            _plan.Patch.Set01(f, Attr.Dimmer, dim);
            _plan.Patch.Set01(f, Attr.Rate, rate);
            _plan.Patch.Set01(f, Attr.Duration, 0.2f);
        }
    }

    private void Pixels(Func<int, Color> cell)
    {
        foreach (var f in G(RigGroup.PixelBars))
        {
            // the cells wherever its layout puts them (an imported bar may lead with a master dimmer)
            var cells = f.Type.CellOffsets;
            _plan.Patch.Set01(f, Attr.Dimmer, 1f);
            for (int c = 0; c + 2 < cells.Length; c += 3)
            {
                var col = cell(c / 3 + f.Id) * _pixelGain;
                _plan.Patch.Set(f, cells[c], (byte)(Math.Clamp(col.R, 0, 1) * 255));
                _plan.Patch.Set(f, cells[c + 1], (byte)(Math.Clamp(col.G, 0, 1) * 255));
                _plan.Patch.Set(f, cells[c + 2], (byte)(Math.Clamp(col.B, 0, 1) * 255));
            }
        }
    }

    /// <summary>The media-server clip that shows the program camera (IMAG).</summary>
    public const int CameraClip = 5;

    private void Screens(int clip, float speed, Color a, Color b, float dim)
    {
        var p = _plan.Patch;
        foreach (var f in G(RigGroup.Screens))
        {
            p.Set01(f, Attr.Dimmer, dim);
            // the side screens show the cameras (IMAG) through verse, groove and chorus; the centre wall in a calm verse
            bool side = f.ScreenSize.X < 15f;
            bool imag = Section is ShowSection.Calm || side && Section is ShowSection.Groove or ShowSection.Chorus;
            p.Set(f, f.Type.Offset(Attr.Clip), (byte)((imag ? CameraClip : clip) * 32 + 4));
            p.Set01(f, Attr.ClipSpeed, Math.Clamp(speed / 4f, 0f, 1f));
            p.Set01(f, Attr.Red, a.R); p.Set01(f, Attr.Green, a.G); p.Set01(f, Attr.Blue, a.B);
            p.Set01(f, Attr.Red2, b.R); p.Set01(f, Attr.Green2, b.G); p.Set01(f, Attr.Blue2, b.B);
        }
    }
}
