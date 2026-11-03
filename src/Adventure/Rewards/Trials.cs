using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>What a trial is run as. Append only (sent as a number).</summary>
public enum TrialKind : byte
{
    /// <summary>On foot, from a chapel, a hut, a tower or a castle to the next place 0.4-3 km off.</summary>
    Run = 0,
    /// <summary>Paraglider or wingsuit, from a peak or a viewpoint down to a place at least 300 m lower, 2-8 km off.</summary>
    Glide = 1,
}

/// <summary>A trial's gate: a ground checkpoint (run) or an air gate (glide), LV95 and metres.</summary>
public readonly record struct TrialGate(double E, double N, float Alt, float Radius);

/// <summary>
/// A timed trial at a landmark (#1227, rewards plan 6): a fixed course made from its id, the same on every peer.
/// The start stone stands by the start POI; the course runs to <see cref="ToPoiId"/>.
/// </summary>
public sealed record TrialDef(string Id, string Name, TrialKind Kind, string PoiId, string ToPoiId,
    double StartE, double StartN, float StartAlt, double ToE, double ToN, float ToAlt)
{
    public double Length => Math.Sqrt((ToE - StartE) * (ToE - StartE) + (ToN - StartN) * (ToN - StartN));
}

/// <summary>A trial's par times (seconds): gold, silver, bronze.</summary>
public readonly record struct TrialPar(double Gold, double Silver, double Bronze);

/// <summary>The trials' rules: which POIs have one, their courses, par times and medals.</summary>
public static class Trials
{
    public const float RunGateEvery = 200, GlideGateEvery = 400, RunRadius = 25, GlideRadius = 40;
    public const double RunMin = 400, RunMax = 3000, GlideMin = 2000, GlideMax = 8000, GlideDrop = 300;
    /// <summary>The start stone stands this far from its POI (a waymarked post beside it).</summary>
    public const double StoneOffset = 6;

    /// <summary>The fastest a body may go between two gates (m/s, <c>RaceManager.MaxPace</c>'s classes): a run is on foot, a glide on wings.</summary>
    public static double MaxPace(TrialKind k) => k == TrialKind.Glide ? 70 : 14;

    public static string RunId(Poi p) => "tr:" + p.Id + ":run";
    public static string GlideId(Poi p) => "tr:" + p.Id + ":glide";

    private static bool RunStart(Poi p) => p.Kind is PoiKind.Chapel or PoiKind.Hut or PoiKind.LookoutTower or PoiKind.Castle;
    private static bool GlideStart(Poi p) => p.Kind is PoiKind.Peak or PoiKind.SummitCross || p.Is(PoiFlags.Viewpoint) && p.Kind != PoiKind.Pass;
    private static bool Finish(Poi p) => p.Kind is not (PoiKind.Lake or PoiKind.BusStop);

    /// <summary>
    /// The trials starting at a POI: a run to the nearest place 0.4-3 km away, a glide to the nearest place 2-8 km
    /// away and at least 300 m lower. <paramref name="cat"/> is searched with <see cref="PoiCatalogue.Near"/>.
    /// </summary>
    public static List<TrialDef> At(Poi poi, PoiCatalogue cat)
    {
        var found = new List<TrialDef>(2);
        var near = new List<Poi>();
        if (RunStart(poi))
        {
            cat.Near(poi.E, poi.N, (float)RunMax, near);
            if (Nearest(poi, near, RunMin, RunMax, p => Finish(p) && p.Kind != poi.Kind) is { } to)
                found.Add(Make(RunId(poi), $"Run: {poi.Name} to {to.Name}", TrialKind.Run, poi, to));
        }
        if (GlideStart(poi))
        {
            cat.Near(poi.E, poi.N, (float)GlideMax, near);
            if (Nearest(poi, near, GlideMin, GlideMax, p => Finish(p) && p.Elevation <= poi.Elevation - GlideDrop) is { } to)
                found.Add(Make(GlideId(poi), $"Glide: {poi.Name} to {to.Name}", TrialKind.Glide, poi, to));
        }
        return found;
    }

    /// <summary>The trial an id names, or null (a forged id cannot exist).</summary>
    public static TrialDef? Resolve(string id, PoiCatalogue cat)
    {
        if (!id.StartsWith("tr:", StringComparison.Ordinal)) return null;
        int last = id.LastIndexOf(':');
        if (last <= 3 || cat.Resolve(id[3..last]) is not { } poi) return null;
        foreach (var t in At(poi, cat))
            if (t.Id == id) return t;
        return null;
    }

    private static Poi? Nearest(Poi from, List<Poi> near, double min, double max, Func<Poi, bool> ok)
    {
        Poi? best = null;
        double bd = double.MaxValue;
        foreach (var p in near)
        {
            if (p == from || !ok(p)) continue;
            double d = Math.Sqrt((p.E - from.E) * (p.E - from.E) + (p.N - from.N) * (p.N - from.N));
            // ties broken by id, so every peer picks the same one
            if (d < min || d > max || d > bd || d == bd && best != null && string.CompareOrdinal(p.Id, best.Id) > 0) continue;
            (best, bd) = (p, d);
        }
        return best;
    }

    private static TrialDef Make(string id, string name, TrialKind kind, Poi from, Poi to)
    {
        // the stone a few metres off its place, toward the course
        double d = Math.Sqrt((to.E - from.E) * (to.E - from.E) + (to.N - from.N) * (to.N - from.N));
        double ue = (to.E - from.E) / d, un = (to.N - from.N) / d;
        return new TrialDef(id, name, kind, from.Id, to.Id, from.E + ue * StoneOffset, from.N + un * StoneOffset, from.Elevation,
            to.E, to.N, to.Elevation);
    }

    /// <summary>
    /// A trial's gates, the last one the finish. A run: one every 200 m on the ground, 25 m wide. A glide: one every
    /// 400 m on the straight glide from 30 m over the start to the finish, never under the terrain + 25 m, 40 m wide.
    /// </summary>
    public static TrialGate[] Course(TrialDef d, Func<double, double, float> ground)
    {
        double len = d.Length;
        float every = d.Kind == TrialKind.Glide ? GlideGateEvery : RunGateEvery;
        int n = Math.Max(1, (int)Math.Ceiling(len / every));
        var gates = new TrialGate[n];
        for (int i = 0; i < n; i++)
        {
            double t = (i + 1.0) / n;
            double e = d.StartE + (d.ToE - d.StartE) * t, nn = d.StartN + (d.ToN - d.StartN) * t;
            float g = ground(e, nn);
            if (d.Kind == TrialKind.Run) gates[i] = new TrialGate(e, nn, g, RunRadius);
            else
            {
                float line = (float)(d.StartAlt + 30 + (d.ToAlt - d.StartAlt - 30) * t);
                gates[i] = new TrialGate(e, nn, Math.Max(line, g + 25), GlideRadius);
            }
        }
        return gates;
    }

    /// <summary>
    /// Par times from the course and the class's pace: a run at 4.5 m/s on the flat plus 0.35 m/s up, a glide at
    /// 13 m/s; silver 25 % and bronze 60 % slower than gold.
    /// </summary>
    public static TrialPar Par(TrialDef d, TrialGate[] course)
    {
        double gold;
        if (d.Kind == TrialKind.Glide) gold = d.Length / 13;
        else
        {
            double climb = 0;
            float prev = d.StartAlt;
            foreach (var g in course)
            {
                climb += Math.Max(0, g.Alt - prev);
                prev = g.Alt;
            }
            gold = d.Length / 4.5 + climb / 0.35;
        }
        gold = Math.Round(gold + 5);
        return new TrialPar(gold, Math.Round(gold * 1.25), Math.Round(gold * 1.6));
    }

    /// <summary>The medal of a time: 3 gold, 2 silver, 1 bronze, 0 none.</summary>
    public static int Medal(double seconds, TrialPar par) => seconds <= par.Gold ? 3 : seconds <= par.Silver ? 2 : seconds <= par.Bronze ? 1 : 0;

    public static string MedalName(int medal) => medal switch { 3 => "gold", 2 => "silver", 1 => "bronze", _ => "no medal" };

    /// <summary>"1:07.4", invariant.</summary>
    public static string Clock(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{seconds % 60:00.0}");
}

/// <summary>A trial's best for one player.</summary>
public sealed class TrialBest
{
    public double Seconds { get; set; }
    public int Medal { get; set; }
    public int Runs { get; set; }
}

/// <summary>The <c>trials</c> section (#1227): the player's personal best per trial id (seconds, medal, runs finished).</summary>
public sealed class TrialsProgress
{
    public static readonly SectionKey<TrialsProgress> Section = new("trials", Version: 1);

    public Dictionary<string, TrialBest> Best { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One row of a trial's board. <c>Name</c> is a cache of the name at the run; the board shows the live one (<see cref="TrialBoard.NamesOf"/>).</summary>
public sealed record TrialEntry(string Key, string Name, double Seconds, long When);

/// <summary>
/// The server's trial boards (#1227, decided Oct 2026: per server): the top 10 per trial, one row per player (its
/// best), saved as <c>user://adventure/trials.json</c>. Plain C#.
/// </summary>
public sealed class TrialBoard
{
    public const int Top = 10;

    private readonly Dictionary<string, List<TrialEntry>> _boards = new(StringComparer.Ordinal);

    public IReadOnlyList<TrialEntry> Of(string trialId) => _boards.TryGetValue(trialId, out var l) ? l : Array.Empty<TrialEntry>();

    /// <summary>
    /// The names to show for a board's rows (#1359): <paramref name="nameOf"/> gives a player's name now from their key
    /// (an id: a PlayerKey fingerprint or <c>acct:</c>); any other key (<c>local</c>, <c>peer:</c>) keeps the stored name,
    /// which is the backup name.
    /// </summary>
    public string[] NamesOf(string trialId, Func<string, string?, string>? nameOf) =>
        Of(trialId).Select(r => nameOf != null && Net.PlayerIds.IsId(r.Key) ? nameOf(r.Key, r.Name) : r.Name).ToArray();

    /// <summary>A finished run: kept if it is the player's best and in the top 10. Returns its rank (0 first) or -1.</summary>
    public int Submit(string trialId, string key, string name, double seconds, long when)
    {
        if (!_boards.TryGetValue(trialId, out var list)) _boards[trialId] = list = new List<TrialEntry>();
        int mine = list.FindIndex(r => r.Key == key);
        if (mine >= 0)
        {
            if (list[mine].Seconds <= seconds) return -1;
            list.RemoveAt(mine);
        }
        var row = new TrialEntry(key, name, seconds, when);
        int at = 0;
        while (at < list.Count && list[at].Seconds <= seconds) at++;
        if (at >= Top) return -1;
        list.Insert(at, row);
        if (list.Count > Top) list.RemoveRange(Top, list.Count - Top);
        return at;
    }

    public Dictionary<string, List<TrialEntry>> ToData() => _boards;

    public static TrialBoard FromData(Dictionary<string, List<TrialEntry>>? data)
    {
        var b = new TrialBoard();
        if (data == null) return b;
        foreach (var (id, rows) in data)
        {
            rows.Sort((x, y) => x.Seconds.CompareTo(y.Seconds));
            b._boards[id] = rows.Count > Top ? rows.GetRange(0, Top) : rows;
        }
        return b;
    }
}

/// <summary>What a gate report did.</summary>
public enum TrialGateResult : byte { Ignored, Next, Finished, TooFast, NotRunning }

/// <summary>
/// The trials being run on the server (#1227): per player, the trial, its gates and the server time it started.
/// The server checks where the body is; this book checks the order and the pace between gates (an impossible pace
/// is out, as <c>RaceManager.Plausible</c>). Plain C#.
/// </summary>
public sealed class TrialRuns
{
    private sealed class Run
    {
        public required TrialDef Def;
        public required TrialGate[] Gates;
        public double Started, LastAt;
        public int Next;
        public double LastE, LastN;
    }

    private readonly Dictionary<string, Run> _runs = new(StringComparer.Ordinal);

    public void Start(string key, TrialDef def, TrialGate[] gates, double now) =>
        _runs[key] = new Run { Def = def, Gates = gates, Started = now, LastAt = now, LastE = def.StartE, LastN = def.StartN };

    public bool Running(string key, out TrialDef? def, out TrialGate[]? gates, out int next)
    {
        if (_runs.TryGetValue(key, out var r)) { (def, gates, next) = (r.Def, r.Gates, r.Next); return true; }
        (def, gates, next) = (null, null, 0);
        return false;
    }

    public double StartedAt(string key) => _runs.TryGetValue(key, out var r) ? r.Started : 0;

    /// <summary>
    /// Gate <paramref name="gate"/> passed at <paramref name="now"/>; the server has checked the body is at it.
    /// <paramref name="seconds"/> is the run's time when it finished.
    /// </summary>
    public TrialGateResult Gate(string key, string trialId, int gate, double now, out double seconds)
    {
        seconds = 0;
        if (!_runs.TryGetValue(key, out var r) || r.Def.Id != trialId) return TrialGateResult.NotRunning;
        if (gate != r.Next) return TrialGateResult.Ignored;
        var g = r.Gates[gate];
        double dist = Math.Sqrt((g.E - r.LastE) * (g.E - r.LastE) + (g.N - r.LastN) * (g.N - r.LastN)) - g.Radius;
        double dt = Math.Max(0.05, now - r.LastAt);
        if (dist > 0 && dist / dt > Trials.MaxPace(r.Def.Kind))
        {
            _runs.Remove(key);
            return TrialGateResult.TooFast;
        }
        r.LastAt = now;
        (r.LastE, r.LastN) = (g.E, g.N);
        r.Next++;
        if (r.Next < r.Gates.Length) return TrialGateResult.Next;
        seconds = now - r.Started;
        _runs.Remove(key);
        return TrialGateResult.Finished;
    }

    public bool Leave(string key) => _runs.Remove(key);
}

/// <summary>
/// A trial's ghost (#1227): the run sampled at 4 Hz (LV95 relative to its first point, altitude, yaw), ~4 KB a
/// minute, capped at <see cref="MaxBytes"/>. The client records and uploads it after a personal best; the server
/// keeps the top 3; a retry races it.
/// </summary>
public static class GhostTrack
{
    public const int MaxBytes = 64 * 1024;
    public const double Rate = 4;
    private const uint Magic = 0x31485347; // "GSH1"

    public readonly record struct Sample(double E, double N, float Alt, float Yaw);

    public static byte[] Encode(IReadOnlyList<Sample> samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        int n = Math.Min(samples.Count, (MaxBytes - 24) / 16);
        w.Write(n);
        double e0 = n > 0 ? samples[0].E : 0, n0 = n > 0 ? samples[0].N : 0;
        w.Write(e0);
        w.Write(n0);
        for (int i = 0; i < n; i++)
        {
            w.Write((float)(samples[i].E - e0));
            w.Write((float)(samples[i].N - n0));
            w.Write(samples[i].Alt);
            w.Write(samples[i].Yaw);
        }
        return ms.ToArray();
    }

    /// <summary>The samples, or null for bytes that are not a ghost (a forged upload).</summary>
    public static List<Sample>? Decode(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes.Length > MaxBytes) return null;
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (r.ReadUInt32() != Magic) return null;
        int n = r.ReadInt32();
        if (n < 0 || 24 + n * 16L != bytes.Length) return null;
        double e0 = r.ReadDouble(), n0 = r.ReadDouble();
        var list = new List<Sample>(n);
        for (int i = 0; i < n; i++)
        {
            float de = r.ReadSingle(), dn = r.ReadSingle(), alt = r.ReadSingle(), yaw = r.ReadSingle();
            if (!float.IsFinite(de) || !float.IsFinite(dn) || !float.IsFinite(alt) || !float.IsFinite(yaw)) return null;
            list.Add(new Sample(e0 + de, n0 + dn, alt, yaw));
        }
        return list;
    }
}
