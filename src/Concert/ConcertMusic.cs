using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Items;

namespace UnitSport.Concert;

/// <summary>
/// Where a song is, for the desk: what its analysis says at this moment of the show. The finer bands
/// (<see cref="CdAnalysisRuntime.SampleBands"/>): snare and hat hits, the bass and the brightness, 0..1;
/// <see cref="Build"/> 0..1 through a build, <see cref="SinceDrop"/> seconds since the last drop.
/// </summary>
public readonly record struct MusicMoment(ShowSection Section, float Beat, float Kick, float Level, int SectionBar, bool LastSection, float Burst,
    float Snare = 0f, float Hat = 0f, float Bass = 0.5f, float Bright = 0.5f, float Build = 0f, float SinceDrop = float.PositiveInfinity,
    bool HasBands = false);

/// <summary>
/// A concert's music (#885): a setlist of the server's CDs (<see cref="CdLibrary"/>), chosen and
/// ordered by the event's seed, played back to back from the PA on the shared clock — every peer on
/// the same song at the same second, nothing sent — and read for the desk: the song's sections
/// (calm, groove, peak, chorus from <see cref="CdAnalysis"/>) cue the looks, its beat grid clocks the
/// effects, its kick hits the dimmers, its loudness rides the master. Between two songs, a gap of
/// silence the desk plays as a blackout. The PA is two <see cref="RadioSpeaker"/>s (four in the round)
/// with a stadium's reach, fetched and kept in sync as a radio's are.
/// </summary>
public partial class ConcertMusic : Node3D
{
    /// <summary>Silence between two songs, s.</summary>
    public const double Gap = 6.0;
    private const int MaxSongs = 12;
    /// <summary>Full volume within this many metres of a PA hang, heard to the far end of the bowl.</summary>
    private const float Unit = 40f, Reach = 650f;

    private readonly ConcertEvent _e;
    private readonly Vector3[] _hangs;
    private readonly List<RadioSpeaker> _speakers = new();
    private CdInfo[] _setlist = Array.Empty<CdInfo>();
    private int _libraryVersion = -1;

    /// <summary>The song playing now and when it started (shared clock), or none.</summary>
    public CdInfo? Song { get; private set; }
    public double SongStartedAt { get; private set; }
    public IReadOnlyList<CdInfo> Setlist => _setlist;

    public ConcertMusic(ConcertEvent e, Vector3[] hangs)
    {
        _e = e;
        _hangs = hangs;
        Name = "Music";
    }

    public ConcertMusic() : this(ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 1), Array.Empty<Vector3>()) { }

    public override void _Ready()
    {
        foreach (var at in _hangs)
        {
            var s = new RadioSpeaker { Name = $"PA{_speakers.Count}", Position = at, Reach = Reach, Unit = Unit, Volume = 1f };
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

    /// <summary>The setlist from the library: its CDs that have a beat grid, shuffled by the seed; the rat beat and personal CDs never.</summary>
    public static CdInfo[] SetlistOf(ConcertEvent e, IEnumerable<CdInfo> library)
    {
        var rng = new Random(e.Seed);
        return library.Where(c => c.Id > 0 && !CdLibrary.IsRatBeat(c.Id) && c.Duration > 30f && c.Bpm > 0)
            .OrderBy(c => c.Id).OrderBy(_ => rng.Next()).Take(MaxSongs).ToArray();
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
        if (!on || _setlist.Length == 0)
        {
            Song = null;
            foreach (var s in _speakers) s.On = false;
            return null;
        }
        // the setlist loops on the shared clock: the same song and second on every peer
        double cycle = 0;
        foreach (var c in _setlist) cycle += c.Duration + Gap;
        double pos = serverNow % cycle, start = serverNow - pos;
        CdInfo? song = null;
        foreach (var c in _setlist)
        {
            if (pos < c.Duration) { song = c; break; }
            if (pos < c.Duration + Gap) break;   // the gap after it
            pos -= c.Duration + Gap;
            start += c.Duration + Gap;
        }
        if (song == null)
        {
            Song = null;
            foreach (var s in _speakers) s.On = false;
            return new MusicMoment(ShowSection.Blackout, 0f, 0f, 0f, 0, false, 0f);
        }
        if (Song?.Id != song.Id || Math.Abs(SongStartedAt - start) > 0.5)
            GD.Print($"[concert] {_e.Artist} plays \"{song.Title}\" ({song.Bpm:F0} bpm, {song.Analysis?.Drops.Length ?? 0} drops, {_setlist.Length} songs in the set)");
        Song = song;
        SongStartedAt = start;
        foreach (var s in _speakers)
        {
            s.CdId = song.Id;
            s.StartedAt = start;
            s.Length = song.Duration;
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
        return bands
            ? new MusicMoment(section, beat, g.Kick, g.Level, bar, last, g.Burst, snare, hat, bass, bright, build, since, true)
            : new MusicMoment(section, beat, g.Kick, g.Level, bar, last, g.Burst);
    }
}
