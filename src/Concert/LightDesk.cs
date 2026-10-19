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

    // the moment being written, for the delegates below
    private int _beatN;
    private int _n;

    // made once (perf-no-per-frame-allocations): each reads the moment from the fields above
    private readonly Func<int, Color> _walkInWash, _walkInPixels, _calmWash, _calmPixels, _grooveWash, _groovePixels,
        _chorusWash, _chorusPixels, _peakWash, _peakPixels;
    private readonly Func<int, float> _grooveChase, _full, _floorGroove;

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
    }

    /// <summary>The section playing at a moment of the show (seconds since it started; negative before).</summary>
    public ShowSection SectionAt(double showSeconds, out float beat)
    {
        beat = (float)(showSeconds * _e.Bpm / 60.0);
        if (showSeconds < 0) return ShowSection.WalkIn;
        int bar = (int)MathF.Floor(beat / 4f);
        int songBars = SongForm.Length * BarsPerSection + 1;
        int inSong = bar % songBars;
        if (inSong == songBars - 1) return ShowSection.Blackout;
        // each song shuffles its form a little, from the seed
        int song = bar / songBars;
        int k = inSong / BarsPerSection;
        if (((_e.Seed >> (song % 16)) & 1) == 1 && k is 1 or 3) k = 4 - k;   // groove and chorus swap places
        return SongForm[k];
    }

    /// <summary>Writes every channel for this moment of the show.</summary>
    public void Write(double showSeconds)
    {
        Section = SectionAt(showSeconds, out float beat);
        Beat = beat;
        float ph = beat - MathF.Floor(beat);           // within the beat
        float bar = beat / 4f, barPh = bar - MathF.Floor(bar);
        float kick = MathF.Exp(-ph * 5f);              // a hit on every beat, decaying
        int beatN = _beatN = (int)MathF.Floor(beat);
        _n = G(RigGroup.MidTruss).Length;
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
                Washes(0.35f, _walkInWash, tilt: 25f, sway: 6f, speed: 0.05f, beat);
                Off(RigGroup.FrontTruss); Off(RigGroup.MidTruss); Off(RigGroup.Floor);
                Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_walkInPixels);
                Screens(clip: 0, speed: 0.1f, _pal[0], _pal[1], 0.6f);
                break;

            case ShowSection.Blackout:
                foreach (var g in _groups) foreach (var f in g) if (f.Group != RigGroup.Haze) Dark(f);
                break;

            case ShowSection.Calm:
                Washes(0.55f, _calmWash, tilt: 20f, sway: 10f, speed: 0.125f, beat);
                // the spots pick out the artist: aimed at the stage's centre, warm
                Spots(0.8f, new Color(1f, 0.85f, 0.65f), aimCentre: true, beat, zoom: 0.25f);
                Off(RigGroup.MidTruss); Off(RigGroup.Floor); Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_calmPixels);
                Screens(clip: 1, speed: 0.2f, _pal[0], _pal[2], 0.8f);
                break;

            case ShowSection.Groove:
                Washes(0.6f + 0.4f * kick, _grooveWash, tilt: 30f, sway: 15f, speed: 0.25f, beat);
                Spots(0.7f, _pal[2], aimCentre: false, beat, zoom: 0.4f);
                // the beams: a fan that breathes with the bar, a chase over it on every beat
                Beams(RigGroup.MidTruss, _grooveChase, _pal[1],
                    fan: 35f, tilt: 25f + 15f * MathF.Sin(bar * MathF.Tau / 4f), open: true);
                Beams(RigGroup.Floor, _floorGroove, _pal[0], fan: 20f, tilt: 20f + 10f * MathF.Sin(beat * MathF.PI / 2f), open: true);
                Off(RigGroup.Blinders); Off(RigGroup.Strobes);
                Pixels(_groovePixels);
                Screens(clip: 2, speed: _e.Bpm / 240f, _pal[0], _pal[1], 1f);
                break;

            case ShowSection.Chorus:
                Washes(1f, _chorusWash, tilt: 35f, sway: 20f, speed: 0.25f, beat);
                Spots(1f, Colors.White, aimCentre: false, beat, zoom: 0.6f);
                Beams(RigGroup.MidTruss, _full, _pal[(beatN / 4) % 2 == 0 ? 0 : 2], fan: 55f, tilt: 40f, open: true);
                Beams(RigGroup.Floor, _full, Colors.White, fan: 40f, tilt: 10f + 25f * barPh, open: true);
                // the blinders hit the crowd on every downbeat
                Blinders(barPh < 0.25f ? MathF.Exp(-barPh * 12f) : 0f);
                Off(RigGroup.Strobes);
                Pixels(_chorusPixels);
                Screens(clip: 3, speed: _e.Bpm / 120f, _pal[0], Colors.White, 1f);
                break;

            case ShowSection.Peak:
                Washes(kick, _peakWash, tilt: 40f, sway: 30f, speed: 0.5f, beat);
                Spots(1f, _pal[2], aimCentre: false, beat, zoom: 0.8f);
                // a ballyhoo: every beat each beam jumps somewhere new, strobing
                Ballyhoo(RigGroup.MidTruss, beatN, _pal[1]);
                Ballyhoo(RigGroup.Floor, beatN + 7, Colors.White);
                Blinders((beatN & 3) == 0 ? kick : 0f);
                Strobes(1f, rate: 0.6f);
                Pixels(_peakPixels);
                Screens(clip: 4, speed: _e.Bpm / 60f, Colors.White, _pal[1], 1f);
                break;
        }
    }

    // ---- palettes and effects, written as DMX ------------------------------------------------------

    private Fixture[] G(RigGroup g) => _groups[(int)g];

    /// <summary>A chase across a group: each fixture lit as the wave passes, <paramref name="width"/> of the group wide.</summary>
    private static float Chase(int i, int n, float beat, float width)
    {
        if (n <= 1) return 1f;
        float pos = (beat % n) / n, me = (float)i / n;
        float d = MathF.Abs(pos - me);
        d = MathF.Min(d, 1f - d);
        return MathF.Max(0f, 1f - d / width);
    }

    private void Dark(Fixture f)
    {
        var p = _plan.Patch;
        p.Set01(f, Attr.Dimmer, 0f);
        p.Set01(f, Attr.DimmerFine, 0f);
        if (f.Type.Kind is FixtureKind.Blinder or FixtureKind.PixelBar)
            for (int c = 0; c < f.Type.Footprint; c++) p.Set(f, c, 0);
    }

    private void Off(RigGroup g)
    {
        foreach (var f in G(g)) Dark(f);
    }

    /// <summary>Position as degrees: pan about the yoke, tilt away from its axis towards the fixture's home side.</summary>
    private void Aim(Fixture f, float panDeg, float tiltDeg)
    {
        var p = _plan.Patch;
        p.Set16(f, Attr.Pan, Attr.PanFine, 0.5f + panDeg / f.Type.PanRange);
        p.Set16(f, Attr.Tilt, Attr.TiltFine, 0.5f + tiltDeg / f.Type.TiltRange);
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
            float a = beat * speed * MathF.Tau + i * 0.6f;
            Aim(g[i], MathF.Sin(a) * sway, tilt + MathF.Cos(a) * sway * 0.5f);
            Paint(g[i], color(i));
            Open(g[i], dim, zoom: 0.7f);
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
            else Aim(g[i], off * 60f + MathF.Sin(beat * MathF.PI / 4f + i) * 10f, 30f + MathF.Cos(beat * MathF.PI / 8f + i) * 15f);
            Paint(g[i], color);
            Open(g[i], dim, zoom);
        }
    }

    private void Beams(RigGroup grp, Func<int, float> dim, Color color, float fan, float tilt, bool open)
    {
        var g = G(grp);
        for (int i = 0; i < g.Length; i++)
        {
            float off = g.Length <= 1 ? 0 : (i / (float)(g.Length - 1) - 0.5f);
            Aim(g[i], off * fan * 2f, tilt);
            Paint(g[i], color);
            Open(g[i], open ? dim(i) : 0f, zoom: 0.2f);
        }
    }

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
            for (int c = 0; c < f.Type.Footprint; c++) _plan.Patch.Set(f, c, (byte)(level * 255));
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
            for (int c = 0; c < 8; c++)
            {
                var col = cell(c + f.Id);
                _plan.Patch.Set(f, c * 3, (byte)(Math.Clamp(col.R, 0, 1) * 255));
                _plan.Patch.Set(f, c * 3 + 1, (byte)(Math.Clamp(col.G, 0, 1) * 255));
                _plan.Patch.Set(f, c * 3 + 2, (byte)(Math.Clamp(col.B, 0, 1) * 255));
            }
    }

    private void Screens(int clip, float speed, Color a, Color b, float dim)
    {
        var p = _plan.Patch;
        foreach (var f in G(RigGroup.Screens))
        {
            p.Set01(f, Attr.Dimmer, dim);
            p.Set(f, f.Type.Offset(Attr.Clip), (byte)(clip * 32 + 4));
            p.Set01(f, Attr.ClipSpeed, Math.Clamp(speed / 4f, 0f, 1f));
            p.Set01(f, Attr.Red, a.R); p.Set01(f, Attr.Green, a.G); p.Set01(f, Attr.Blue, a.B);
            p.Set01(f, Attr.Red2, b.R); p.Set01(f, Attr.Green2, b.G); p.Set01(f, Attr.Blue2, b.B);
        }
    }
}
