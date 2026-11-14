using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// Artificial satellites in the sky (#1444): the ISS, the bright ones and Starlink, from two-line
/// elements propagated with <see cref="Sgp4"/>. The elements ship as a snapshot
/// (<c>assets/space/tle_snapshot.json</c>: stations and the visual group) and are fetched fresh from
/// CelesTrak at most once a day in normal play (cached in <c>user://tle/</c>; Starlink comes only
/// that way), never in a check or a run on a pinned date. Like a real pass, a satellite shows only
/// while it is sunlit over a dark sky (the sun 4° down here, the satellite out of the Earth's shadow),
/// as a point sized by its brightness.
///
/// <para>
/// SGP4 for ten thousand satellites is no per-frame work: a worker propagates them all to the sky's
/// moment and one second later (the velocity), and each frame only carries them on along that line
/// until the next run, about every 30 s of sky time (a 7.7 km/s orbit bends 20 m in 2 s). The points
/// are one <see cref="MultiMesh"/> (<c>sky_point.gdshader</c>); only its instances move.
/// </para>
/// </summary>
public partial class Satellites : MultiMeshInstance3D
{
    public const string SnapshotPath = "res://assets/space/tle_snapshot.json";
    public const string CacheDir = "user://tle";
    public static readonly string[] Groups = ["stations", "visual", "starlink"];

    public static Satellites? Instance { get; private set; }

    private sealed class Sat
    {
        public Sgp4 Model = null!;
        public float StdMagnitude;
        public string Name = "";
    }

    private List<Sat> _sats = new();

    // the worker's last run: Earth-fixed positions (m) at _snapUtc and their velocities (m/s)
    private sealed record Snapshot(DateTime Utc, double[] Position, double[] Velocity, List<Sat> Sats);
    private Snapshot? _snap;
    private Task<Snapshot>? _job;

    private readonly ShaderMaterial _material;
    private float _distance = -1f;

    /// <summary>Drawn this frame: the satellite and its direction in world axes.</summary>
    private readonly List<(Sat Sat, Vector3 Direction)> _shown = new();

    /// <summary>How many satellites are loaded, and how many show now (the checks).</summary>
    public int Count => _sats.Count;
    public int Shown => _shown.Count;

    public Satellites()
    {
        Name = "Satellites";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;   // the sky: depth hides it, never the occlusion buffer (SkyBodies)
        GIMode = GIModeEnum.Disabled;
        CustomAabb = new Aabb(-Vector3.One * 1e7f, Vector3.One * 2e7f);
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sky_point.gdshader") };
        _material.SetShaderParameter("instanced", true);
        MaterialOverride = _material;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // directions only, nothing camera-relative: placed in the frame, so a headless run has them too
    public override void _Process(double delta) => Place();

    public override void _Ready()
    {
        // the instances stand at their directions from the origin: the node itself never moves
        GlobalTransform = Transform3D.Identity;
        Load();
        if (MayFetch) FetchStale();
    }

    /// <summary>Normal play only: a check, a screenshot run or a pinned date keeps the shipped snapshot.</summary>
    private static bool MayFetch => SunPath.Model == SunModel.Real && Calendar.Pinned == null && !Systems.Narrowed
        && !CmdArgs.Has("--shot") && !CmdArgs.Has("--shot-queue");

    // ---- the elements ------------------------------------------------------------------------------

    /// <summary>The snapshot, then whatever fresher groups the cache holds (by satellite number, the newest epoch wins).</summary>
    private void Load()
    {
        var byNumber = new Dictionary<int, Tle>();
        void Add(Tle? t)
        {
            if (t == null) return;
            if (!byNumber.TryGetValue(t.Number, out var old) || old.Epoch < t.Epoch) byNumber[t.Number] = t;
        }
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(SnapshotPath));
            foreach (var row in doc.RootElement.GetProperty("tle").EnumerateArray())
                Add(Tle.Parse(row[0].GetString()!, row[1].GetString()!, row[2].GetString()!));
        }
        catch (Exception e) { GD.PushWarning($"[satellites] {SnapshotPath}: {e.Message}"); }
        foreach (var group in Groups)
        {
            string path = $"{CacheDir}/{group}.tle";
            if (!Godot.FileAccess.FileExists(path)) continue;
            foreach (var t in ParseLines(Godot.FileAccess.GetFileAsString(path))) Add(t);
        }
        _sats = byNumber.Values.Select(t => new Sat { Model = new Sgp4(t), Name = t.Name, StdMagnitude = StdMagnitudeOf(t) })
            .Where(s => !s.Model.DeepSpace).ToList();
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One * 2f }, InstanceCount = _sats.Count, VisibleInstanceCount = 0,
        };
        _snap = null;
        GD.Print($"[satellites] {_sats.Count} near-Earth satellites");
    }

    /// <summary>A file of three-line TLEs (name, line 1, line 2).</summary>
    public static IEnumerable<Tle> ParseLines(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        for (int i = 0; i + 2 < lines.Length; i++)
        {
            if (lines[i + 1].StartsWith('1') && lines[i + 2].StartsWith('2') && Tle.Parse(lines[i], lines[i + 1], lines[i + 2]) is { } t)
            {
                yield return t;
                i += 2;
            }
        }
    }

    /// <summary>
    /// How bright it is at 1,000 km, half lit (the "standard magnitude"): the ISS -1.8, a Starlink 5.5,
    /// the visual group's bright ones about 3.
    /// </summary>
    private static float StdMagnitudeOf(Tle t) => t.Number == 25544 ? -1.8f
        : t.Name.Contains("CSS") || t.Name.Contains("TIANHE") ? -0.5f
        : t.Name.StartsWith("STARLINK") ? 5.5f
        : 3f;

    /// <summary>Fetches each group whose cached file is a day old or missing, one at a time.</summary>
    private void FetchStale()
    {
        DirAccess.MakeDirRecursiveAbsolute(CacheDir);
        var stale = Groups.Where(g =>
        {
            string path = $"{CacheDir}/{g}.tle";
            if (!Godot.FileAccess.FileExists(path)) return true;
            long modified = (long)Godot.FileAccess.GetModifiedTime(path);
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - modified > 86400;
        }).ToList();
        FetchNext(stale);
    }

    private void FetchNext(List<string> groups)
    {
        if (groups.Count == 0) return;
        string group = groups[0];
        groups.RemoveAt(0);
        var http = new HttpRequest { Name = "TleFetch", Timeout = 30 };
        AddChild(http);
        http.RequestCompleted += (result, code, _, body) =>
        {
            http.QueueFree();
            if (result == (long)HttpRequest.Result.Success && code == 200 && body.Length > 100)
            {
                using (var f = Godot.FileAccess.Open($"{CacheDir}/{group}.tle", Godot.FileAccess.ModeFlags.Write)) f?.StoreBuffer(body);
                GD.Print($"[satellites] fetched {group} from CelesTrak ({body.Length / 1024} KB)");
            }
            else GD.Print($"[satellites] {group}: CelesTrak gave {result} {code}; keeping what we have");
            // the last group in: everything loaded again at once
            if (groups.Count == 0) Load();
            else FetchNext(groups);
        };
        var error = http.Request($"https://celestrak.org/NORAD/elements/gp.php?GROUP={group}&FORMAT=tle",
            ["User-Agent: UnitSportSwitzerland (game; once a day)"]);
        if (error != Error.Ok) http.QueueFree();
    }

    // ---- the sky -----------------------------------------------------------------------------------

    /// <summary>All of them at <paramref name="utc"/> and a second later. On a worker.</summary>
    private static Snapshot Propagate(List<Sat> sats, DateTime utc)
    {
        var p = new double[sats.Count * 3];
        var v = new double[sats.Count * 3];
        var later = utc.AddSeconds(1);
        for (int i = 0; i < sats.Count; i++)
        {
            if (sats[i].Model.Ecef(utc) is not { } a || sats[i].Model.Ecef(later) is not { } b)
            {
                p[i * 3] = double.NaN;
                continue;
            }
            p[i * 3] = a.X; p[i * 3 + 1] = a.Y; p[i * 3 + 2] = a.Z;
            v[i * 3] = b.X - a.X; v[i * 3 + 1] = b.Y - a.Y; v[i * 3 + 2] = b.Z - a.Z;
        }
        return new Snapshot(utc, p, v, sats);
    }

    /// <summary>A satellite's direction this frame, in world axes, if it is shown.</summary>
    public (string Name, Vector3 Direction)? Nearest(Vector3 direction)
    {
        (string, Vector3)? best = null;
        float bestAngle = float.MaxValue;
        foreach (var (sat, dir) in _shown)
        {
            float a = dir.AngleTo(direction);
            if (a < bestAngle) { bestAngle = a; best = (sat.Name, dir); }
        }
        return best;
    }

    /// <summary>Where the satellite of that name is now (world axes), while it shows.</summary>
    public Vector3? DirectionOf(string name)
    {
        foreach (var (sat, dir) in _shown) if (sat.Name == name) return dir;
        return null;
    }

    private void Place()
    {
        _shown.Clear();
        if (DayNight.Instance is not { Sky: { } sky } day || GetViewport()?.GetCamera3D() is not { } cam || _sats.Count == 0)
        {
            Visible = false;
            return;
        }
        var utc = SwissTime.ToUtc(sky.Date, sky.Hour);
        if (_job is { IsCompleted: true } done)
        {
            if (done.Result.Sats == _sats) _snap = done.Result;
            _job = null;
        }
        double age = _snap == null ? double.MaxValue : Math.Abs((utc - _snap.Utc).TotalSeconds);
        if (_job == null && age > 30)
        {
            var sats = _sats;
            _job = Task.Run(() => Propagate(sats, utc));
        }
        // nothing to carry on from that is near enough, or a sky too bright: none shows
        // above the air the sky is black whatever the sun does below (#1845): only the Earth's shadow hides them
        if (_snap == null || age > 300 || (day.SunElevationDeg > -4f && Atmosphere.Space < 0.5f))
        {
            Visible = false;
            if (Multimesh != null) Multimesh.VisibleInstanceCount = 0;
            return;
        }
        Visible = true;
        float distance = cam.Far * 0.92f;
        if (distance != _distance)
        {
            _distance = distance;
            _material.SetShaderParameter("distance", distance);
        }

        var (toWorld, obs) = SunPath.EarthFrame(sky.At);
        double ox = obs.X, oy = obs.Y, oz = obs.Z, olen = Math.Sqrt(ox * ox + oy * oy + oz * oz);
        var sunEcef = Ephemeris.Ecef(SkyBody.Sun, utc);
        double sl = sunEcef.Length, sx = sunEcef.X / sl, sy = sunEcef.Y / sl, sz = sunEcef.Z / sl;
        const double earthRadius = 6_371_000;
        double dt = (utc - _snap.Utc).TotalSeconds;
        var snap = _snap;
        var mm = Multimesh!;
        int n = 0;
        for (int i = 0; i < snap.Sats.Count; i++)
        {
            double px = snap.Position[i * 3];
            if (double.IsNaN(px)) continue;
            px += snap.Velocity[i * 3] * dt;
            double py = snap.Position[i * 3 + 1] + snap.Velocity[i * 3 + 1] * dt;
            double pz = snap.Position[i * 3 + 2] + snap.Velocity[i * 3 + 2] * dt;
            double dx = px - ox, dy = py - oy, dz = pz - oz;
            // above the horizon (the local up is near enough the ellipsoid's here); from high up, anything the Earth's
            // ball is not in front of (#1845: from orbit the limb is far under the horizontal)
            double up = (dx * ox + dy * oy + dz * oz) / olen;
            if (up <= 0 && (olen < earthRadius + 50_000 || BehindEarth(ox, oy, oz, dx, dy, dz, earthRadius))) continue;
            // in the Earth's shadow: behind it from the sun and within its radius of the line
            double along = px * sx + py * sy + pz * sz;
            if (along < 0)
            {
                double qx = px - along * sx, qy = py - along * sy, qz = pz - along * sz;
                if (qx * qx + qy * qy + qz * qz < earthRadius * earthRadius) continue;
            }
            double range = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            var dir = (toWorld * new Vector3((float)(dx / range), (float)(dy / range), (float)(dz / range))).Normalized();
            var sat = snap.Sats[i];
            float mag = sat.StdMagnitude + 5f * (float)Math.Log10(range / 1_000_000.0);
            if (mag > 6.5f) continue;
            var (size, alpha) = StarField.Look(mag);
            mm.SetInstanceTransform(n, new Transform3D(Basis.Identity, dir));
            mm.SetInstanceColor(n, new Color(1f, 0.97f, 0.9f, alpha));
            mm.SetInstanceCustomData(n, new Color(size, 0, 0, 0));
            _shown.Add((sat, dir));
            n++;
        }
        mm.VisibleInstanceCount = n;
    }

    /// <summary>
    /// The segment from the observer <c>o</c> along <c>d</c> (to the satellite) passes through a ball of radius
    /// <paramref name="radius"/> round the Earth's centre: hidden from an observer high up (#1845).
    /// </summary>
    internal static bool BehindEarth(double ox, double oy, double oz, double dx, double dy, double dz, double radius)
    {
        double dd = dx * dx + dy * dy + dz * dz;
        // the point of the segment nearest the centre
        double t = Math.Clamp(-(ox * dx + oy * dy + oz * dz) / dd, 0, 1);
        double cx = ox + dx * t, cy = oy + dy * t, cz = oz + dz * t;
        return cx * cx + cy * cy + cz * cz < radius * radius;
    }
}
