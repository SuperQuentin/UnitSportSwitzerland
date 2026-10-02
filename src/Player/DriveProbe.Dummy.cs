using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--dummy arc:lat:speed[:r];...</c> (#159 lane check): scripted cars on the race road, from <c>arc</c> m along
/// it at <c>lat</c> m left of the centreline, at a steady <c>speed</c> m/s. Plain ones are solid boxes the
/// pilots sense like traffic; <c>r</c> ones are rivals (handed to the pilots as other racers, no body). At the
/// end, per racer and dummy: the lowest speed within 80 m short of it to 5 m past, against the profile there,
/// and the cap and the rule that set it. A car with room to pass beside it must not be braked for.
/// </summary>
public partial class DriveProbe
{
    private sealed class Dummy
    {
        public float Arc, Lat, Speed;
        public bool Rival;
        public AnimatableBody3D? Body;
        public Vector3 Pos, Vel;
        public readonly Dictionary<Entry, (float Ratio, float Speed, float Cap, int Rule)> Worst = new();
        /// <summary>Most the racer was off its racing line within 10 m of it, and its gap to it then (m, centre to centre across).</summary>
        public readonly Dictionary<Entry, (float Off, float Gap)> OffLine = new();
        /// <summary>Racers a speed cap held (cap under its speed + 2 m/s) within 80 m short of it.</summary>
        public readonly HashSet<Entry> Braked = new();
    }

    private readonly List<Dummy> _dummies = ParseDummies();

    private static List<Dummy> ParseDummies()
    {
        var list = new List<Dummy>();
        if (CmdArgs.Value("--dummy") is not { } arg) return list;
        foreach (var part in arg.Split(';', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(':');
            float P(int i) => float.Parse(f[i], CultureInfo.InvariantCulture);
            list.Add(new Dummy { Arc = P(0), Lat = P(1), Speed = P(2), Rival = f.Length > 3 && f[3] == "r" });
        }
        return list;
    }

    private void StepDummies(float dt)
    {
        foreach (var d in _dummies)
        {
            d.Arc += d.Speed * dt;
            int i = _route.Line.IndexAt(d.Arc);
            var n = RaceLine.Normal(_route.Centre, i);
            var prev = d.Pos;
            int j = Mathf.Min(i + 1, _route.Centre.Count - 1);
            var line = _route.Line;
            float span = line.Arc[j] - line.Arc[i];
            var centre = span > 0.01f ? _route.Centre[i].Lerp(_route.Centre[j], Mathf.Clamp((d.Arc - line.Arc[i]) / span, 0f, 1f)) : _route.Centre[i];
            d.Pos = centre + new Vector3(n.X, 0, n.Y) * d.Lat;
            d.Vel = dt > 0f && prev != Vector3.Zero ? (d.Pos - prev) / dt : Vector3.Zero;
            var fwd = RaceRoute.Flat(_route.Line.PointAt(d.Arc + 2f) - _route.Line.PointAt(d.Arc - 2f)).Normalized();
            if (!d.Rival && d.Body == null)
            {
                d.Body = new AnimatableBody3D { SyncToPhysics = false, Name = "Dummy" };
                d.Body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.8f, 1.4f, 4.4f) }, Position = Vector3.Up * 0.7f });
                d.Body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.8f, 1.4f, 4.4f) }, Position = Vector3.Up * 0.7f });
                AddChild(d.Body);
            }
            if (d.Body != null) d.Body.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Atan2(-fwd.X, -fwd.Z)), d.Pos);
            foreach (var en in _entries)
            {
                if (en.Out || en.Pilot is not { } p) continue;
                float gap = d.Arc - en.Arc;
                if (gap > 80f || gap < -5f) continue;
                float v = en.Player.Motion.Speed, want = p.Profile[p.D.Near];
                float ratio = v / Mathf.Max(want, 1f);
                if (p.D.Cap < v + 2f) d.Braked.Add(en);
                if (!d.Worst.TryGetValue(en, out var w) || ratio < w.Ratio)
                    d.Worst[en] = (ratio, v, p.D.Cap, p.CapRule);
                if (Mathf.Abs(gap) < 10f)
                {
                    int jj = _route.Line.IndexAt(en.Arc);
                    var nj = RaceLine.Normal(_route.Centre, jj);
                    var rel = en.Player.GlobalPosition - _route.Centre[jj];
                    float myLat = rel.X * nj.X + rel.Z * nj.Y, off = Mathf.Abs(myLat - _route.Line.Offset[jj]);
                    if (!d.OffLine.TryGetValue(en, out var o) || off > o.Off) d.OffLine[en] = (off, Mathf.Abs(myLat - d.Lat));
                }
            }
        }
    }

    /// <summary>The rival dummies as a driver sees them.</summary>
    private IEnumerable<AutoPilot.Other> DummyRivals() =>
        _dummies.Where(d => d.Rival && d.Pos != Vector3.Zero).Select(d => new AutoPilot.Other(d.Pos, d.Vel, false));

    /// <summary>
    /// Prints the DUMMY lines; false when a racer braked for a car that left it room (on the far side of a lane,
    /// |lat| ≥ 1.5 m) or moved further off its line beside one than a car's width beside it needs (+0.3 m), or did
    /// not brake for one standing in its line.
    /// </summary>
    private bool PrintDummies()
    {
        GD.Print($"[drive] traffic: largest speed across a car's own heading {World.Traffic.MaxSideSlip:F2} m/s");
        bool ok = true;
        int k = 0;
        foreach (var d in _dummies)
        {
            k++;
            bool roomy = Mathf.Abs(d.Lat) >= 1.5f;
            float need = Mathf.Max(0f, (d.Rival ? 2.0f : 2.4f) - Mathf.Abs(d.Lat)) + 0.3f;
            foreach (var (en, w) in d.Worst)
            {
                var o = d.OffLine.GetValueOrDefault(en);
                bool capped = d.Braked.Contains(en);
                bool good = roomy ? !capped && o.Off <= need : capped;
                ok &= good;
                GD.Print($"[drive] DUMMY {k} ({(d.Rival ? "rival" : "traffic")}, lat {d.Lat:F1}, {d.Speed * 3.6f:F0} km/h) {en.Label}: "
                    + $"lowest {w.Speed * 3.6f:F0} km/h = {w.Ratio * 100f:F0}% of the profile, cap {(w.Cap < 1e9f ? w.Cap * 3.6f : 0f):F0} (rule {w.Rule}), "
                    + $"off its line up to {o.Off:F2} m beside it (gap {o.Gap:F1} m){(good ? "" : roomy ? " — BRAKED OR SWERVED WITH ROOM" : " — DID NOT BRAKE")}");
            }
        }
        return ok;
    }
}
