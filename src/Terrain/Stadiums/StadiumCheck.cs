using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// <c>--stadiumcheck</c> (#885): every catalogued stadium built in its own frame, without a world,
/// and held to what is known of the real one: its outline against the aerial, its seats against
/// the published capacity, a triangle budget, stands a player can walk up, and a gate tunnel with
/// headroom. Also that the tile holding the centre spot is the one that builds it. Prints a RESULT
/// line, exits 0 or 1.
/// </summary>
public static class StadiumCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--stadiumcheck") >= 0;

    /// <summary>What is known of a real stadium, to hold its row to.</summary>
    private sealed record Known(StadiumSpec Spec, float OutsideU, float OutsideV, int Capacity);

    private static readonly Known[] Rows =
    {
        // the roof's outline on the swissimage aerial; 30,084 seats (Wikipedia, StadiumDB)
        new(StadiumCatalog.Geneva, 153f, 206f, 30084),
    };

    /// <summary>A stadium's mesh may not exceed this; Geneva is about 20 k.</summary>
    private const int TriangleBudget = 40000;
    /// <summary>The steepest a walkable tier's slope may be, a little under <c>FootPlayer</c>'s 52° floor angle.</summary>
    private const float MaxRampDeg = 45f;

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, FormattableString what)
        {
            if (!ok) failures++;
            GD.Print($"[stadiumcheck] {(ok ? "ok  " : "FAIL")} {FormattableString.Invariant(what)}");
        }

        Expect(StadiumCatalog.All.All(s => Rows.Any(r => r.Spec == s)), $"every catalogued stadium has a row of known figures here");
        foreach (var row in Rows)
        {
            var s = row.Spec;
            var build = StadiumBuilder.BuildLocal(s);
            var v = build.Mesh!.Vertices;
            float minU = v.Min(p => p.X), maxU = v.Max(p => p.X), minV = v.Min(p => p.Z), maxV = v.Max(p => p.Z);
            float w = maxU - minU, l = maxV - minV;
            Expect(Math.Abs(w - row.OutsideU) < 4f && Math.Abs(l - row.OutsideV) < 4f,
                $"{s.Name}: {w:F1} x {l:F1} m outside, the aerial gives {row.OutsideU} x {row.OutsideV}");
            Expect(Math.Abs(build.Capacity - row.Capacity) < row.Capacity * 0.12f,
                $"{s.Name}: {build.Capacity} seats, the real one has {row.Capacity}");
            int tris = build.Mesh.Indices.Length / 3;
            Expect(tris < TriangleBudget, $"{s.Name}: {tris} triangles, {build.Faces.Length / 3} collision faces (budget {TriangleBudget})");

            // the stands' collision: no slope a player would slide off
            float steepest = 0;
            for (int t = 0; t + 2 < build.Faces.Length; t += 3)
            {
                var n = (build.Faces[t + 1] - build.Faces[t]).Cross(build.Faces[t + 2] - build.Faces[t]);
                if (n.LengthSquared() < 1e-8f) continue;
                float deg = Mathf.RadToDeg(Mathf.Acos(Math.Abs(n.Normalized().Y)));
                if (deg > 2f && deg < 80f) steepest = Math.Max(steepest, deg);
            }
            Expect(steepest > 15f && steepest < MaxRampDeg, $"{s.Name}: steepest walkable slope {steepest:F1}° (under {MaxRampDeg}°)");

            // a gate: under the north stand's middle, nothing below the tunnel's ceiling, the ceiling over it
            var gateFloor = Heights(build.Faces, new Vector2(0, -s.FrontHalfV - 2f));
            Expect(!gateFloor.Any(h => h > 0.1f && h < s.GateHeight - 0.05f) && gateFloor.Any(h => Math.Abs(h - s.GateHeight) < 0.05f),
                $"{s.Name}: the north gate's tunnel is clear to {s.GateHeight} m and roofed (surfaces at {Fmt(gateFloor)})");
            // beside it, the stand: the lowest surface over the front rows is the tier, a step up from the pitch
            var stand = Heights(build.Faces, new Vector2(s.GateWidth + 6f, -s.FrontHalfV - 2f));
            Expect(stand.Count > 0 && stand.Where(h => h > -1f).DefaultIfEmpty(99).Min() is > 0.8f and < 3f,
                $"{s.Name}: the north stand beside the gate rises from the pitch wall (surfaces at {Fmt(stand)})");

            // a seat: on a seat block's row, facing the pitch; none on a tier's first row or in a gate
            float a = Mathf.DegToRad(s.AzimuthDeg);
            (double E, double N) Lv95(float u, float v) => (s.E + u * MathF.Cos(a) - v * MathF.Sin(a), s.N - (u * MathF.Sin(a) + v * MathF.Cos(a)));
            float row8 = s.FrontHalfU + 8.5f * s.Tiers[0].RowDepth;
            var (we, wn) = Lv95(-row8, 30f);
            var seat = StadiumSeats.At(we, wn);
            Expect(seat is { Tier: 0, Row: 8 } st && Math.Abs(Mathf.AngleDifference(st.Yaw, Mathf.DegToRad(-90f - s.AzimuthDeg))) < 0.05f
                && Math.Abs(st.Drop - (s.Tiers[0].Riser + 0.05f)) < 0.01f,
                $"{s.Name}: a seat on the west stand's row 8, facing east ({seat})");
            var (fe, fn) = Lv95(-(s.FrontHalfU + 0.3f), 30f);
            var (ge, gn) = Lv95(-row8, 0f);
            Expect(StadiumSeats.At(fe, fn) == null && StadiumSeats.At(ge, gn) == null, $"{s.Name}: no seat on the first row, nor in the west gate");

            // a data building straddling the facade loses what is inside and keeps the rest; a far tile is untouched
            var owner = StadiumCatalog.Owner(s);
            var (omin, _) = StadiumClip.Bounds(StadiumBuilder.Outline(s, owner));
            float spotZ = (float)(owner.MaxN - s.N), x0 = omin.X + 5, x1 = omin.X + 15, o0 = omin.X - 60, o1 = omin.X - 50;
            var slab = new Building
            {
                Kind = BuildingKind.Commercial, MinY = 0, MaxY = 10,
                Triangles = new[]
                {
                    x0, 10, spotZ - 5, x1, 10, spotZ - 5, x1, 10, spotZ + 5, x0, 10, spotZ - 5, x1, 10, spotZ + 5, x0, 10, spotZ + 5,
                    o0, 10, spotZ - 5, o1, 10, spotZ - 5, o1, 10, spotZ + 5, o0, 10, spotZ - 5, o1, 10, spotZ + 5, o0, 10, spotZ + 5,
                },
            };
            var tile = new BuildingTile { Id = owner, Buildings = new() { slab } };
            var clipped = StadiumClip.Clip(tile)!;
            Expect(clipped.Buildings.Count == 1 && clipped.Buildings[0].TriangleCount == 2,
                $"{s.Name}: a building reaching inside the facade keeps only its outside ({clipped.Buildings[0].TriangleCount} of 4 triangles)");
            var far = new BuildingTile { Id = new TileId(owner.E + 5, owner.N), Buildings = tile.Buildings };
            Expect(ReferenceEquals(StadiumClip.Clip(far), far), $"{s.Name}: a tile 5 km away is read as it was");

            var placed = StadiumBuilder.Build(owner, null, mesh: false, collision: true);
            Expect(placed is { Faces.Length: > 0 } && StadiumCatalog.OwnedBy(new TileId(owner.E + 1, owner.N)).All(o => o != s),
                $"{s.Name}: built by tile {owner} and by no other");
        }

        GD.Print(failures == 0 ? "[stadiumcheck] RESULT: ok" : $"[stadiumcheck] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    private static string Fmt(List<float> hs) => string.Join(", ", hs.OrderBy(h => h).Select(h => h.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The heights at which a vertical line through (u, v) meets the collision faces.</summary>
    private static List<float> Heights(Vector3[] faces, Vector2 at)
    {
        var hits = new List<float>();
        for (int t = 0; t + 2 < faces.Length; t += 3)
        {
            Vector3 a = faces[t], b = faces[t + 1], c = faces[t + 2];
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(d) < 1e-6f) continue;
            float l1 = ((b.Z - c.Z) * (at.X - c.X) + (c.X - b.X) * (at.Y - c.Z)) / d;
            float l2 = ((c.Z - a.Z) * (at.X - c.X) + (a.X - c.X) * (at.Y - c.Z)) / d;
            float l3 = 1 - l1 - l2;
            if (l1 < 0 || l2 < 0 || l3 < 0) continue;
            hits.Add(l1 * a.Y + l2 * b.Y + l3 * c.Y);
        }
        return hits;
    }
}
