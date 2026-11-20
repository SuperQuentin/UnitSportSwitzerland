using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// <c>--moontrailer &lt;file&gt;</c> (a film tool, dev): one scripted camera move filmed at each site of <c>--moonsites</c>, the
/// file's lines in the same order (<c>#</c> comments and blank lines skipped):
/// <c>name|sun°|eye e,n,u&gt;e,n,u|look e,n,u&gt;e,n,u|seconds|fov|flags</c>.
/// Points are metres east and north of the site's main part and up over the ground under that point; the sun stands at
/// <c>sun°</c> over the site's horizon (setting; negative is night). Flags, comma separated: <c>drift</c> (a handheld sway),
/// <c>lin</c> or <c>in</c> (the move's ease: linear, or easing in only; eased at both ends without), and
/// <c>terminal=&lt;title start&gt;:&lt;page&gt;</c> (the eye and the look are then metres out from that terminal's screen, to
/// its right and up, and the screen shows that page of its log), <c>at=&lt;module kind&gt;</c> (points from the colony's first
/// module of that kind, not the site's main part). Each move is its own clip,
/// <c>test_output/clips/trailer_&lt;name&gt;</c>. Run as <c>--moonshowcase</c> is (a window at 1920x1080, <c>--fixed-fps 30</c>,
/// <c>--clip-hq</c>, a user:// without the test profile).
/// </summary>
public partial class MoonSitesCheck
{
    public static string? TrailerFile => CmdArgs.Value("--moontrailer");
    public static bool Trailer => TrailerFile != null;

    public sealed record Move(string Name, double SunDeg, Vector3 Eye0, Vector3 Eye1, Vector3 Look0, Vector3 Look1, double Seconds,
        float Fov, bool Drift, string Ease, string? Terminal, int Page, string? At);

    private static List<Move>? _moves;

    private static List<Move> Moves()
    {
        if (_moves != null) return _moves;
        _moves = new List<Move>();
        if (TrailerFile is not { } file || !System.IO.File.Exists(file)) return _moves;
        static Vector3 V(string t)
        {
            var p = t.Split(',');
            return new Vector3(F(p[0]), F(p[1]), F(p[2]));
        }
        static float F(string t) => float.Parse(t.Trim(), CultureInfo.InvariantCulture);
        foreach (var raw in System.IO.File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('|');
            var eye = f[2].Split('>');
            var look = f[3].Split('>');
            string flags = f.Length > 6 ? f[6] : "";
            string? terminal = null;
            int page = -1;
            string ease = "smooth";
            string? at = null;
            bool drift = false;
            foreach (var flag in flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (flag == "drift") drift = true;
                else if (flag is "lin" or "in") ease = flag;
                else if (flag.StartsWith("at=")) at = flag["at=".Length..];
                else if (flag.StartsWith("terminal="))
                {
                    var t = flag["terminal=".Length..];
                    int colon = t.LastIndexOf(':');
                    terminal = colon > 0 ? t[..colon] : t;
                    page = colon > 0 ? int.Parse(t[(colon + 1)..], CultureInfo.InvariantCulture) : -1;
                }
            }
            _moves.Add(new Move(f[0], double.Parse(f[1], CultureInfo.InvariantCulture), V(eye[0]), V(eye.Length > 1 ? eye[1] : eye[0]),
                V(look[0]), V(look.Length > 1 ? look[1] : look[0]), double.Parse(f[4], CultureInfo.InvariantCulture),
                F(f[5]), drift, ease, terminal, page, at));
        }
        GD.Print($"[moonsites] trailer: {_moves.Count} moves from {file}");
        return _moves;
    }

    private Move? TrailerShot(int index) => Trailer && index >= 0 && index < Moves().Count ? Moves()[index] : null;

    /// <summary>Days to add to the bodies' clock for the sun at <paramref name="deg"/> over a site's horizon, setting.</summary>
    private double SunDays(MoonSite s, double deg)
    {
        var baseNow = _baseSky?.Invoke() ?? SkyMoment.At(DateTime.UtcNow);
        double best = 0, bestErr = double.MaxValue, prev = double.NaN;
        for (double d = 0; d < 29.6; d += 0.02)
        {
            var sun = SunLocal(s, baseNow with { Utc = baseNow.Utc.AddDays(d) });
            double el = Math.Asin(Math.Clamp(sun.Up, -1, 1)) * 180 / Math.PI;
            if (!double.IsNaN(prev) && el < prev && Math.Abs(el - deg) < bestErr)
            {
                bestErr = Math.Abs(el - deg);
                best = d;
            }
            prev = el;
        }
        return best;
    }

    private double _trT = -1;
    private bool _trCut;
    private (ColonyTerminals Set, int Index)? _trTerminal;

    /// <summary>One frame of a site's scripted move; true once its clip is written.</summary>
    private bool TrailerStep(MoonSite s, BodyGround ground, double delta)
    {
        if (_showCam == null || TrailerShot(_index) is not { } m) return true;
        if (_trT < 0)
        {
            _trTerminal = null;
            if (m.Terminal != null)
                foreach (var set in ColonyTerminals.Sets)
                    for (int i = 0; i < set.Count; i++)
                        if (set.Title(i).StartsWith(m.Terminal, StringComparison.OrdinalIgnoreCase))
                        {
                            _trTerminal = (set, i);
                            set.ShowPage(i, m.Page);
                        }
            if (m.Terminal != null && _trTerminal == null) GD.PrintErr($"[moonsites] trailer: no terminal \"{m.Terminal}\" here");
            _showCam.Fov = m.Fov;
            ClipRecorder.Film(GetTree(), $"trailer_{m.Name}");
            _trT = 0;
            _trCut = false;
        }
        if (_trCut)
        {
            if (ClipRecorder.Busy) return false;
            _trT = -1;
            return true;
        }
        _trT += delta;
        double u = Math.Clamp(_trT / m.Seconds, 0, 1);
        float e = (float)(m.Ease switch { "lin" => u, "in" => u * u, _ => u * u * (3 - 2 * u) });
        var eye = Place(s, ground, m.Eye0.Lerp(m.Eye1, e));
        var look = Place(s, ground, m.Look0.Lerp(m.Look1, e));
        if (m.Drift)
        {
            // a handheld sway: a few centimetres, slow and uneven
            double t = _trT;
            eye += new Vector3((float)(Math.Sin(t * 1.3) * 0.05 + Math.Sin(t * 2.9) * 0.02), (float)(Math.Sin(t * 1.7 + 1) * 0.04),
                (float)(Math.Sin(t * 1.1 + 2) * 0.05));
            look += new Vector3((float)(Math.Sin(t * 0.9 + 3) * 0.03), (float)(Math.Sin(t * 1.5) * 0.025), 0);
        }
        _showCam.GlobalPosition = eye;
        _showCam.LookAt(look, Vector3.Up);
        if (_trT >= m.Seconds)
        {
            ClipRecorder.Cut();
            _trCut = true;
        }
        return false;
    }

    /// <summary>A move's point in the world: off a terminal's screen, or east, north and up over the ground at the site.</summary>
    private Vector3 Place(MoonSite s, BodyGround ground, Vector3 p)
    {
        if (_trTerminal is { } t)
        {
            var at = t.Set.ScreenAt(t.Index);
            var facing = t.Set.Facing(t.Index) with { Y = 0 };
            facing = facing.Normalized();
            var right = facing.Cross(Vector3.Up).Normalized();
            return at + facing * p.X + right * p.Y + Vector3.Up * p.Z;
        }
        var main = s.Kind == MoonSiteKind.Impact ? new SitePart(MoonPart.SivbDebris, 0, 0, 0) : s.Parts[0];
        double e0 = main.East, n0 = main.North;
        // at=<module kind>: from the colony's first module of that kind (its frame: x east, z south)
        if (TrailerShot(_index) is { At: { } kind } && Enum.TryParse<ModuleKind>(kind, true, out var mk)
            && MoonColonies.Find(PlacedId(s)) is { } colony)
            foreach (var mod in ColonyPlan.For(colony).Modules)
                if (mod.Kind == mk)
                {
                    e0 = mod.X;
                    n0 = -mod.Z;
                    break;
                }
        var axes = _origin.Frame.Axes!;
        var (x, y, z) = axes.FromEcef(ground.Heights.Ground(MoonSites.Offset(s, e0 + p.X, n0 + p.Y)));
        return new Vector3((float)x, (float)y + p.Z, (float)z);
    }
}
