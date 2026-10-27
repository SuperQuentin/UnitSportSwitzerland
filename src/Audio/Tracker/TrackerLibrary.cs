using Godot;

namespace UnitSport.Audio.Tracker;

/// <summary>What a venue plays when its radio is silent: the mood of its kind (#1244).</summary>
public enum VenueMood { None, Arcade, Bowling, Club, Bar, Cafe, Pub, Beiz, Pizzeria, FastFood, Cinema }

/// <summary>
/// The venues' tracker modules (#1244), in <c>assets/music/venues/</c> (not <c>assets/audio/</c>, which no
/// export packs: #718), packed by every export preset's <c>include_filter</c>,, public domain or CC0 from The
/// Mod Archive (sources and licences in <c>assets/ASSETS.md</c>), a playlist per <see cref="VenueMood"/>.
/// Every client plays the same thing at the same time: the playlist runs round on the shared clock
/// (<see cref="Net.ClockSync.ServerNow"/>) from a start the venue's own salt shifts, so two cafés side by
/// side do not play the same bar. A module is read once and kept; its length is one pass, measured by
/// playing it through without mixing.
/// </summary>
public static class TrackerLibrary
{
    public const string Folder = "res://assets/music/venues/";

    /// <summary>The tracklist (<c>docs/notes/audio/venue-music.md</c>): files by mood.</summary>
    public static readonly IReadOnlyDictionary<VenueMood, string[]> Playlists = new Dictionary<VenueMood, string[]>
    {
        [VenueMood.Arcade] = new[] { "drozerix_-_mecanum_overdrive.xm", "drozerix_-_digital_rendezvous.xm", "cabin_fever.xm" },
        [VenueMood.Bowling] = new[] { "drozerix_-_poppy_flower_girls.xm", "drozerix_-_bubble_machine.xm", "drozerix_-_dream_candy.xm" },
        [VenueMood.Club] = new[] { "kc-basslinetech.xm", "musix-wild-perspective.mod", "absolu.mod" },
        [VenueMood.Bar] = new[] { "blue_intermission.xm", "midnight_drive.xm", "liftyver20.xm" },
        [VenueMood.Cafe] = new[] { "which_brand_of_mustard_shall_i_buy.xm", "takeawalk.xm", "napping_on_a_cloud.xm" },
        [VenueMood.Pub] = new[] { "rumpus.xm", "mega_destruction.xm", "jam_-_blue.xm" },
        [VenueMood.Beiz] = new[] { "loituma.mod", "cancan.mod" },
        [VenueMood.Pizzeria] = new[] { "jam_-_saltarello.xm", "drozerix_-_bakin_blueberry_pie.xm" },
        [VenueMood.FastFood] = new[] { "headspin.xm", "girl_from_mars.xm" },
        [VenueMood.Cinema] = new[] { "drozerix_-_leisurely_voice.xm", "ambient_starfield.xm" },
    };

    /// <summary>Every song of every mood, its first 30 s in mono, in the sound player (<c>--sounds</c>).</summary>
    [SoundShowcase("Venue music")]
    private static IEnumerable<(string Category, string Name, Func<float[]> Make)> Songs() =>
        Playlists.SelectMany(kv => kv.Value, (kv, file) => ("Venue music", $"{kv.Key} - {file}", (Func<float[]>)(() =>
        {
            if (Get(file).Module is not { } m) return Array.Empty<float>();
            var p = new TrackerPlayer(m, Dsp.Rate);
            var stereo = new float[Dsp.Rate * 30 * 2];
            p.Render(stereo, Dsp.Rate * 30);
            var mono = new float[Dsp.Rate * 30];
            for (int k = 0; k < mono.Length; k++) mono[k] = (stereo[k * 2] + stereo[k * 2 + 1]) * 0.5f;
            return mono;
        })));

    /// <summary>A song is cut after this long, s: a module that never comes round still moves on.</summary>
    public const double MaxSong = 360;

    private static readonly Dictionary<string, (TrackerModule? Module, double Length)> Cache = new();

    /// <summary>A module and its length, read on first use; null when the file is missing or not a module.</summary>
    public static (TrackerModule? Module, double Length) Get(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var hit)) return hit;
            TrackerModule? m = null;
            try
            {
                var bytes = Godot.FileAccess.GetFileAsBytes(Folder + file);
                if (bytes.Length > 0) m = TrackerModule.Load(bytes);
            }
            catch (Exception e) { GD.PushWarning($"[tracker] {file}: {e.Message}"); }
            if (m == null) GD.PushWarning($"[tracker] {file}: not found or not a module");
            var entry = (m, m == null ? 0 : Math.Clamp(TrackerPlayer.Duration(m, MaxSong), 5, MaxSong));
            Cache[file] = entry;
            return entry;
        }
    }

    /// <summary>
    /// What <paramref name="mood"/> plays at <paramref name="time"/> on the shared clock for a venue whose
    /// salt is <paramref name="salt"/>: the file, how far into it, and how long until the next one.
    /// </summary>
    public static (string File, double Into, double Left)? At(VenueMood mood, double time, uint salt)
    {
        if (!Playlists.TryGetValue(mood, out var list)) return null;
        var songs = list.Select(f => (File: f, Get(f).Length)).Where(s => s.Length > 0).ToList();
        double cycle = songs.Sum(s => s.Length);
        if (cycle <= 0) return null;
        double t = ((time + salt % 100_000) % cycle + cycle) % cycle;
        foreach (var (file, length) in songs)
        {
            if (t < length) return (file, t, length - t);
            t -= length;
        }
        return (songs[0].File, 0, songs[0].Length);
    }
}
