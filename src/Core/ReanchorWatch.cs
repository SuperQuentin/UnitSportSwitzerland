using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// What a re-anchor must not show (#1710, G2 D0, plan <c>docs/plans/global-tiling-g2-d.md</c> section 5): a jump of the
/// camera, the ground or the things near it. Shared by <c>--reanchorcheck</c>, <c>--longdrivecheck</c> and
/// <c>--longflightcheck</c>.
/// <list type="bullet">
/// <item><b>Tracks</b> (the camera, a vehicle, a prop): every frame's Earth position (ECEF, through the frame of that
/// moment). Across a frame interval that holds a re-anchor, the middle sample is compared with the line through its
/// neighbours (timestamps weighted): a node the shift moved twice, or not at all, leaves that line by the arc moved.
/// The same error over the frames with no re-anchor is the noise it is judged against (bumps, braking).</item>
/// <item><b>The ground under the camera</b>: its Earth position asked just before the re-anchor and asked again in the
/// new frame: the height query must answer the same within <see cref="GroundTolM"/>.</item>
/// <item><b>The picture</b> (windowed only, <see cref="Shots"/>): a 160 × 90 grey copy of every frame; the change
/// across a re-anchor against the changes of the two frames either side. A spike over all of them is a visible pop.</item>
/// </list>
/// Each check reads <see cref="Faults"/> and prints <see cref="Summary"/>.
/// </summary>
public partial class ReanchorWatch : Node
{
    /// <summary>
    /// A track's error at a re-anchor past this, metres, beyond its own noise, is a pop. The error is the middle sample's
    /// distance from its neighbours' line: a jump of d between two samples reads as d/2.
    /// </summary>
    public float JumpTolM { get; init; } = 0.10f;
    /// <summary>The ground under the camera asked again after a re-anchor, metres.</summary>
    public float GroundTolM { get; init; } = 0.02f;
    /// <summary>A picture's change across a re-anchor over this many times its neighbours' (and over 2 grey levels).</summary>
    public float ShotSpike { get; init; } = 3f;
    /// <summary>Compare frames across re-anchors (windowed only: headless has no picture).</summary>
    public bool Shots { get; init; }

    private readonly WorldOrigin _origin;
    private readonly IWorldGround? _ground;
    private readonly string _tag;
    private readonly List<Track> _tracks = new();
    private readonly List<string> _faults = new();
    private bool _shifted, _shiftedPicture, _pictureShift;
    private double _clock;
    private int _shifts, _groundAsked;
    private double _worstGround;
    private Func<Vector3?>? _groundAt;
    private GlobalPos? _groundBefore;

    // the picture: the last three frames' grey copies and whether a re-anchor fell between the last two
    private readonly Queue<(byte[] Grey, bool AfterShift)> _frames = new();
    private readonly List<float> _diffs = new();
    private readonly List<bool> _diffAtShift = new();
    private double _worstShotRatio;
    private int _shotsJudged, _shotSpikes;

    public int Shifts => _shifts;
    public IReadOnlyList<string> Faults => _faults;

    private sealed class Track(string name, Func<Node3D?> node)
    {
        public readonly string Name = name;
        public readonly Func<Node3D?> Node = node;
        // the last three samples: ECEF, time, and whether a re-anchor fell in the interval before the sample
        public readonly (EcefPos P, double T, bool Shift)[] Last = new (EcefPos, double, bool)[3];
        public int Count;
        public double WorstAtShift, WorstElse;
        // every error with no re-anchor by it: its 95th percentile is the noise (a turn, a bump, a leg's end)
        public readonly List<float> Else = new();
        public double Noise
        {
            get
            {
                if (Else.Count == 0) return 0;
                Else.Sort();
                return Else[(int)(0.95 * (Else.Count - 1))];
            }
        }
        public int AtShift;
        public string? WorstWhere;
    }

    public ReanchorWatch(WorldOrigin origin, IWorldGround? ground, string tag)
    {
        _origin = origin;
        _ground = ground;
        _tag = tag;
        Name = "ReanchorWatch";
        // after the shifter (int.MinValue) and every mover: a frame's samples are where the frame drew them
        ProcessPriority = int.MaxValue;
    }

    /// <summary>A node whose Earth position must run on through a re-anchor (a camera, a vehicle, a prop at rest).</summary>
    public void Watch(string name, Func<Node3D?> node) => _tracks.Add(new Track(name, node));

    /// <summary>Where the ground is asked again across each re-anchor (the camera's foot): null skips it.</summary>
    public void WatchGround(Func<Vector3?> at) => _groundAt = at;

    public override void _EnterTree() => _origin.Shifted += OnShifted;
    public override void _ExitTree() => _origin.Shifted -= OnShifted;

    private void OnShifted(OriginShift shift)
    {
        _shifts++;
        _shifted = _shiftedPicture = true;
        // the ground under the camera, asked again by its Earth position in the new frame
        if (_ground != null && _groundBefore is { } g)
        {
            var p = _origin.ToWorld(g);
            if (_ground.TryGetHeight(p, out float h))
            {
                _groundAsked++;
                double d = Math.Abs(h - p.Y);
                if (d > _worstGround) _worstGround = d;
                if (d > GroundTolM && _faults.Count < 20)
                    _faults.Add(Inv($"the ground under the camera moved {d * 100:F1} cm at re-anchor {_shifts}"));
            }
        }
        _groundBefore = null;
    }

    // the tracks on the physics tick: what moves (a car, a body, the probes' cameras) moves there, and a rendered
    // frame holds 0, 1 or 2 ticks (40 cm of noise at 50 m/s sampled per frame)
    public override void _PhysicsProcess(double delta)
    {
        // game time: what the movers step by (a time-warped flight, --fixed-fps)
        double now = _clock += delta;
        bool shifted = _shifted;
        _shifted = false;
        foreach (var t in _tracks) Sample(t, now, shifted);
        // the ground under the camera now, for the next re-anchor
        if (_ground != null && _groundAt?.Invoke() is { } foot && _ground.TryGetHeight(foot, out float h))
            _groundBefore = _origin.ToGlobal(foot with { Y = h });
    }

    public override void _Process(double delta)
    {
        if (!Shots) return;
        // the viewport's texture is the frame drawn before this _Process: a re-anchor shows in the next one's
        Picture(_pictureShift);
        _pictureShift = _shiftedPicture;
        _shiftedPicture = false;
    }

    private void Sample(Track t, double now, bool shifted)
    {
        if (t.Node() is not { } node || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree()) { t.Count = 0; return; }
        var p = _origin.ToEcef(node.GlobalPosition);
        t.Last[0] = t.Last[1];
        t.Last[1] = t.Last[2];
        t.Last[2] = (p, now, shifted);
        if (++t.Count < 3) return;
        // the middle sample against the line through its neighbours: a re-anchor in either interval round it
        var (a, ta, _) = t.Last[0];
        var (b, tb, s1) = t.Last[1];
        var (c, tc, s2) = t.Last[2];
        if (tc - ta < 1e-6) return;
        double k = (tb - ta) / (tc - ta);
        double ex = a.X + (c.X - a.X) * k - b.X, ey = a.Y + (c.Y - a.Y) * k - b.Y, ez = a.Z + (c.Z - a.Z) * k - b.Z;
        double err = Math.Sqrt(ex * ex + ey * ey + ez * ez);
        if (s1 || s2)
        {
            t.AtShift++;
            if (err > t.WorstAtShift) { t.WorstAtShift = err; t.WorstWhere = Inv($"re-anchor {_shifts}"); }
        }
        else
        {
            if (err > t.WorstElse) t.WorstElse = err;
            t.Else.Add((float)err);
        }
    }

    private void Picture(bool shifted)
    {
        if (GetViewport()?.GetTexture()?.GetImage() is not { } img || img.GetWidth() < 16) return;
        img.Resize(160, 90, Image.Interpolation.Bilinear);
        img.Convert(Image.Format.L8);
        var grey = img.GetData();
        if (_frames.Count > 0)
        {
            var last = _frames.Last();
            long sum = 0;
            int n = Math.Min(grey.Length, last.Grey.Length);
            for (int i = 0; i < n; i++) sum += Math.Abs(grey[i] - last.Grey[i]);
            _diffs.Add(n > 0 ? (float)sum / n : 0f);
            _diffAtShift.Add(shifted);
        }
        _frames.Enqueue((grey, shifted));
        if (_frames.Count > 2) _frames.Dequeue();
        // the diff two frames back is judged once the two after it are in: a spike over its four neighbours
        int j = _diffs.Count - 3;
        if (j < 2 || !_diffAtShift[j]) return;
        float d = _diffs[j];
        float around = Math.Max(Math.Max(_diffs[j - 2], _diffs[j - 1]), Math.Max(_diffs[j + 1], _diffs[j + 2]));
        _shotsJudged++;
        double ratio = d / Math.Max(around, 0.25f);
        if (ratio > _worstShotRatio) _worstShotRatio = ratio;
        if (ratio > ShotSpike && d > 2f)
        {
            _shotSpikes++;
            if (_faults.Count < 20) _faults.Add(Inv($"the picture jumped at a re-anchor: {d:F1} grey levels against {around:F1} round it"));
        }
    }

    /// <summary>Judges the tracks (once, at the end): a jump at a re-anchor past the tolerance and twice the noise (95th percentile).</summary>
    public void Judge()
    {
        if (_shifts == 0) _faults.Add("no re-anchor happened");
        foreach (var t in _tracks)
        {
            double tol = JumpTolM + 2 * t.Noise;
            if (t.AtShift > 0 && t.WorstAtShift > tol)
                _faults.Add(Inv($"{t.Name} jumped {t.WorstAtShift:F2} m at {t.WorstWhere} (noise elsewhere {t.Noise:F2} m, worst {t.WorstElse:F2} m)"));
        }
    }

    public string Summary()
    {
        var parts = new List<string> { Inv($"{_shifts} re-anchors") };
        foreach (var t in _tracks) parts.Add(Inv($"{t.Name} worst {t.WorstAtShift * 100:F1} cm at {t.AtShift} samples by a re-anchor (else {t.Noise * 100:F1} cm at the 95th percentile, {t.WorstElse * 100:F1} cm worst)"));
        if (_ground != null) parts.Add(Inv($"ground asked again {_groundAsked} times, worst {_worstGround * 100:F2} cm"));
        if (Shots) parts.Add(Inv($"{_shotsJudged} frames judged, worst change {_worstShotRatio:F1}x its neighbours, {_shotSpikes} spikes"));
        return $"[{_tag}] re-anchors: " + string.Join("; ", parts);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
