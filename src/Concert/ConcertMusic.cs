using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Items;

namespace UnitSport.Concert;

/// <summary>
/// Where a song is, for the desk: what its analysis says at this moment of the show. The finer bands
/// (<see cref="CdAnalysisRuntime.SampleBands"/>): snare and hat hits, the bass and the brightness, 0..1;
/// <see cref="Build"/> 0..1 through a build, <see cref="SinceDrop"/> seconds since the last drop.
/// </summary>
/// <summary>
/// The setlist moved by hand (#994, <c>/concert song</c>): song <see cref="Index"/> of the setlist started
/// at <see cref="At"/> on the shared clock and the set runs on from it; <see cref="Off"/> the PA is silent
/// (the desk then makes its songs up). Null on a <see cref="ConcertMusic"/>: the set runs on the clock alone.
/// </summary>
public readonly record struct SongCue(int Index, double At, bool Off);

public readonly record struct MusicMoment(ShowSection Section, float Beat, float Kick, float Level, int SectionBar, bool LastSection, float Burst,
    float Snare = 0f, float Hat = 0f, float Bass = 0.5f, float Bright = 0.5f, float Build = 0f, float SinceDrop = float.PositiveInfinity,
    bool HasBands = false, bool Finale = false, MelodyMoment Melody = default);

/// <summary>
/// The singer at this moment (#1153, <see cref="CdAnalysisRuntime.SampleVocal"/>/<see cref="CdAnalysisRuntime.MelodyAt"/>):
/// <see cref="Vocal"/> level 0..1, <see cref="Rise"/> +1 up a climbing run .. -1 down a falling one,
/// <see cref="Hold"/> 0..1 through a long note, <see cref="SinceIn"/> s since the voice came back,
/// <see cref="Pitch"/> the sung MIDI note (0 unvoiced). Default (no vocal analysis): nothing sung.
/// </summary>
public readonly record struct MelodyMoment(bool Has, float Vocal, float Rise, float Hold, float SinceIn, float Pitch)
{
    public static MelodyMoment Of(CdInfo song, double t)
    {
        if (!CdAnalysisRuntime.SampleVocal(song, t, out float vocal, out _, out _, out float pitch)) return default;
        CdAnalysisRuntime.MelodyAt(song, t, out float rise, out float hold, out float since);
        return new MelodyMoment(true, vocal, rise, hold, since, pitch);
    }
}

/// <summary>
/// A concert's music (#885): a setlist of the server's CDs (<see cref="CdLibrary"/>), chosen and
/// ordered by the event's seed, played back to back from the PA on the shared clock — every peer on
/// the same song at the same second, nothing sent — and read for the desk: the song's sections
/// (calm, groove, peak, chorus from <see cref="CdAnalysis"/>) cue the looks, its beat grid clocks the
/// effects, its kick hits the dimmers, its loudness rides the master. Between two songs, a gap of
/// silence the desk plays as a blackout. The PA is two <see cref="ConcertSpeaker"/>s (four in the round)
/// with a stadium's reach, fetched and kept in sync as a radio's are, each heard late by its distance
/// (#984): the desk reads the song's own time, never the delayed one.
/// </summary>
public partial class ConcertMusic : Node3D
{
    /// <summary>Silence between two songs, s.</summary>
    public const double Gap = 6.0;

    /// <summary>The PA's voice/music mix (#1153), the operator's (X the voice, Y the music): every hang plays it.</summary>
    public Vector2 Mix { get; set; } = Vector2.One;
    private const int MaxSongs = 12;
    /// <summary>Full volume within this many metres of a PA hang, heard to the far end of the bowl.</summary>
    private const float Unit = 40f, Reach = 650f;

    private readonly ConcertEvent _e;
    private readonly PaSource[] _hangs;
    private readonly Vector2 _outer;
    private readonly List<ConcertSpeaker> _speakers = new();
    private CdInfo[] _setlist = Array.Empty<CdInfo>();
    private int _libraryVersion = -1;

    /// <summary>The song playing now and when it started (shared clock), or none.</summary>
    public CdInfo? Song { get; private set; }
    public double SongStartedAt { get; private set; }
    public IReadOnlyList<CdInfo> Setlist => _setlist;
    public IReadOnlyList<ConcertSpeaker> Speakers => _speakers;
    public IReadOnlyList<PaSource> Hangs => _hangs;

    /// <summary>For the crowd (#984): the show is on, how far into the song or the gap after it (s, -1 when not), the gap's length left, the set's end.</summary>
    public bool ShowOn { get; private set; }
    public double SongTime { get; private set; } = -1;
    public double GapTime { get; private set; } = -1;
    public double GapLeft { get; private set; }
    public bool Finale { get; private set; }
    /// <summary>The song cue in force (<see cref="ConcertService"/>), or null: the set runs on the clock.</summary>
    public SongCue? Cue { get; set; }

    /// <summary>What the last <see cref="Step"/> told the desk.</summary>
    public MusicMoment? Moment { get; private set; }

    public ConcertMusic(ConcertEvent e, StagePlan plan)
    {
        _e = e;
        _hangs = Sources(plan);
        _outer = e.Stadium.Kind == Terrain.Stadiums.VenueKind.OpenAir ? Vector2.Zero : OuterHalf(e.Stadium);
        Name = "Music";
    }

    public ConcertMusic() : this(ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 1), new StagePlan(ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 1))) { }

    /// <summary>The bowl's footprint to its facade, half extents in the stadium frame (its corners taken square).</summary>
    public static Vector2 OuterHalf(Terrain.Stadiums.StadiumSpec s) =>
        new(s.FrontHalfU + Math.Max(s.DepthEast, s.DepthWest), s.FrontHalfV + Math.Max(s.DepthNorth, s.DepthSouth));

    /// <summary>Each hang with the way its line array faces, and the delay tower on its side (a hang that is not by a main one).</summary>
    public static PaSource[] Sources(StagePlan plan)
    {
        var hangs = plan.PaHangs;
        var main = new Vector3[hangs.Count];
        var towers = new List<PaCab>();
        foreach (var cab in plan.Pa)
        {
            if (cab.Sub) continue;
            int near = -1;
            for (int i = 0; i < hangs.Count; i++)
                if (Flat(cab.Centre - hangs[i]) < 3f) near = i;   // across: an open air lifts a low cabinet to its ground, not the hang
            if (near >= 0) main[near] = cab.Facing;
            else towers.Add(cab);
        }
        var sources = new PaSource[hangs.Count];
        for (int i = 0; i < hangs.Count; i++) sources[i] = new PaSource(hangs[i], main[i]);
        foreach (var t in towers)
        {
            int best = -1;
            for (int i = 0; i < hangs.Count; i++)
                if (best < 0 || Flat(t.Centre - hangs[i]) < Flat(t.Centre - hangs[best])) best = i;
            if (best >= 0) sources[best] = sources[best] with { Tower = t.Centre, TowerFacing = t.Facing };
        }
        return sources;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    public override void _Ready()
    {
        foreach (var src in _hangs)
        {
            var s = new ConcertSpeaker { Name = $"PA{_speakers.Count}", Position = src.Hang, Reach = Reach, Unit = Unit, Volume = 1f, Source = src, OuterHalf = _outer };
            AddChild(s);
            _speakers.Add(s);
        }
        if (CdLibrary.Instance is { } lib) lib.Changed += OnLibrary;
    }

    public override void _ExitTree()
    {
        if (CdLibrary.Instance is { } lib) lib.Changed -= OnLibrary;
    }

    private void OnLibrary() => _libraryVersion = -1;

    /// <summary>The setlist from the library: its CDs that have a beat grid, shuffled by the seed; the rat beat and personal CDs never; a jazz night plays the jazz playlist only (#1394), every other stage none of it.</summary>
    public static CdInfo[] SetlistOf(ConcertEvent e, IEnumerable<CdInfo> library)
    {
        var rng = new Random(e.Seed);
        return library.Where(c => c.Id > 0 && !CdLibrary.IsRatBeat(c.Id) && c.Duration > 30f && c.Bpm > 0 && CdLibrary.IsJazz(c) == (e.Genre == Genre.Jazz))
            .OrderBy(c => c.Id).OrderBy(_ => rng.Next()).Take(MaxSongs).ToArray();
    }

    /// <summary>
    /// Where the set is at this moment of the shared clock, s into its loop: on the clock alone, or from
    /// the song a cue started. Pure: the server and every peer find the same song (#994).
    /// </summary>
    public static double Position(IReadOnlyList<CdInfo> setlist, double serverNow, SongCue? cue)
    {
        double cycle = 0, offset = 0;
        for (int i = 0; i < setlist.Count; i++)
        {
            if (cue is { } c && i < c.Index) offset += setlist[i].Duration + Gap;
            cycle += setlist[i].Duration + Gap;
        }
        if (cycle <= 0) return 0;
        double t = cue is { } k ? serverNow - k.At + offset : serverNow;
        return ((t % cycle) + cycle) % cycle;
    }

    /// <summary>The song playing at a position of the set (<see cref="Position"/>), or the one whose gap it is in; and seconds into it.</summary>
    public static (int Index, double Into, bool InGap) At(IReadOnlyList<CdInfo> setlist, double pos)
    {
        for (int i = 0; i < setlist.Count; i++)
        {
            double len = setlist[i].Duration;
            if (pos < len) return (i, pos, false);
            if (pos < len + Gap) return (i, pos, true);
            pos -= len + Gap;
        }
        return (Math.Max(0, setlist.Count - 1), 0, true);
    }

    /// <summary>
    /// Plays the setlist at this moment of the shared clock and says where the song is; null when there
    /// is no music (no CDs, or the show is not on), the desk then makes up its songs.
    /// </summary>
    public MusicMoment? Step(double serverNow, bool on)
    {
        if (_libraryVersion < 0 && CdLibrary.Instance is { } lib)
        {
            _setlist = SetlistOf(_e, lib.All.Values);
            _libraryVersion = 0;
        }
        ShowOn = on;
        SongTime = GapTime = -1;
        if (!on || _setlist.Length == 0 || Cue is { Off: true })
        {
            Song = null;
            foreach (var s in _speakers) s.On = false;
            return Moment = null;
        }
        // the setlist loops on the shared clock: the same song and second on every peer
        double pos = Position(_setlist, serverNow, Cue), start = serverNow - pos;
        CdInfo? song = null;
        bool finale = false;
        for (int i = 0; i < _setlist.Length; i++)
        {
            var c = _setlist[i];
            if (pos < c.Duration) { song = c; break; }
            if (pos < c.Duration + Gap)
            {
                // the gap after it
                finale = i == _setlist.Length - 1;
                GapTime = pos - c.Duration;
                GapLeft = c.Duration + Gap - pos;
                break;
            }
            pos -= c.Duration + Gap;
            start += c.Duration + Gap;
        }
        if (song == null)
        {
            Song = null;
            foreach (var s in _speakers) s.On = false;
            Finale = finale;
            // the gap after the set's last song: the finale's fireworks
            return Moment = new MusicMoment(ShowSection.Blackout, 0f, 0f, 0f, 0, false, 0f, Finale: finale);
        }
        if (Song?.Id != song.Id || Math.Abs(SongStartedAt - start) > 0.5)
            GD.Print($"[concert] {_e.Artist} plays \"{song.Title}\" ({song.Bpm:F0} bpm, {song.Analysis?.Drops.Length ?? 0} drops, {_setlist.Length} songs in the set)");
        Song = song;
        SongStartedAt = start;
        Finale = false;
        SongTime = serverNow - start;
        foreach (var s in _speakers)
        {
            s.CdId = song.Id;
            s.SongStart = start;
            s.Length = song.Duration;
            s.Voice = Mix.X;
            s.Music = Mix.Y;
            s.On = true;
        }

        // the song, read: its section, beat, kick and loudness at this second
        var g = RadioGroove.Of(song.Id, start, serverNow);
        double t = serverNow - start;
        var kind = CdAnalysisRuntime.SectionAt(song, t, out int index);
        var starts = song.Analysis?.SectionStarts ?? Array.Empty<float>();
        bool last = starts.Length == 0 || index >= starts.Length - 1;
        float sectionStart = index >= 0 && index < starts.Length ? starts[index] : 0f;
        int bar = (int)Math.Max(0, Math.Floor((t - sectionStart) * song.Bpm / 60.0 / 4.0));
        var section = kind switch
        {
            SectionKind.Calm => ShowSection.Calm,
            SectionKind.Peak => ShowSection.Peak,
            SectionKind.Chorus => ShowSection.Chorus,
            _ => ShowSection.Groove,
        };
        float beat = g.Beat + g.Phase;
        bool bands = CdAnalysisRuntime.SampleBands(song, t, out float snare, out float hat, out float bass, out float bright);
        CdAnalysisRuntime.DropAt(song, t, out float since, out float build);
        var melody = MelodyMoment.Of(song, t);
        return Moment = bands
            ? new MusicMoment(section, beat, g.Kick, g.Level, bar, last, g.Burst, snare, hat, bass, bright, build, since, true, Melody: melody)
            : new MusicMoment(section, beat, g.Kick, g.Level, bar, last, g.Burst, Melody: melody);
    }
}
