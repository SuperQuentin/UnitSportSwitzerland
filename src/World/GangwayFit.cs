using Godot;
using UnitSport.Avatar;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The steamer's adjustable gangway (#383): where a pier's head (#377, <see cref="Landings"/>) lies
/// off an open gangway, the plank runs from its hinge on the deck's edge to the head's face, its foot
/// flush with the head's deck, tilting up or down to it (within <see cref="MaxSlope"/>); elsewhere it
/// is the plank as built, 1.3 m out and 0.3 m down onto a quay. Worked out from the drawn ship and the
/// landings alone, which every peer has the same, so every peer lays the same plank under its own
/// copy of the ship. See <c>docs/notes/world/landings.md</c>.
/// </summary>
public static class GangwayFit
{
    /// <summary>The steepest the plank lies, rise over run (1 in 2.5).</summary>
    public const float MaxSlope = 0.42f;
    /// <summary>The shortest and longest reach to a face, m (the plank as built reaches 1.3).</summary>
    public const float MinRun = 0.5f, MaxRun = 2.2f;

    /// <summary>The plank as built: 1.3 m out, 0.3 m down.</summary>
    public static (float Run, float Drop) Default => (SteamerMeshBuilder.PlankOut, SteamerMeshBuilder.PlankDrop);

    /// <summary>
    /// The plank of gangway <paramref name="door"/> (0 port, 1 starboard) of a steamer drawn at
    /// <paramref name="frame"/> (its node frame, world): its run out from the hinge and its drop
    /// (negative: it rises to a higher pier). The default when no pier's head lies off it.
    /// </summary>
    public static (float Run, float Drop) Of(Transform3D frame, int door)
    {
        float side = door == 0 ? 1f : -1f;
        var hinge = frame * BoatMeshBuilder.Flip(new Vector3(side * SteamerMeshBuilder.PlankEdge, SteamerMeshBuilder.DeckY,
            SteamerMeshBuilder.Z((SteamerMeshBuilder.GangFrom + SteamerMeshBuilder.GangTo) * 0.5f)));
        var outward = (frame.Basis * BoatMeshBuilder.Flip(new Vector3(side, 0, 0))) with { Y = 0 };
        if (outward.LengthSquared() < 1e-6f || !WaterField.TryLv95(hinge, out double he, out double hn)
            || !WaterField.TryWorld(he, hn, out var ground)) return Default;
        outward = outward.Normalized();
        double oe = outward.X, on = -outward.Z;   // LV95: north is -Z
        var landings = Landings.Current.Landings;
        for (int i = 0; i < landings.Count; i++)
        {
            var ribbons = landings[i].Ribbons;
            for (int j = 0; j < ribbons.Count; j++)
            {
                var r = ribbons[j];
                if (r.Kind != PierKind.Pier || r.Rails || r.Points.Count != 2) continue;
                if (Fit(r, he, hn, oe, on) is not { } run) continue;
                float deckY = ground.Y + (float)r.Points[0][2];
                float drop = hinge.Y - deckY;
                if (run < MinRun || run > MaxRun || Mathf.Abs(drop) > MaxSlope * run) continue;
                return (run, drop);
            }
        }
        return Default;
    }

    /// <summary>
    /// The level distance from the hinge out to a head's face, when the head lies straight out from
    /// it along the ship's beam and the hinge faces its long side; null otherwise.
    /// </summary>
    private static float? Fit(PierRibbon head, double he, double hn, double oe, double on)
    {
        double ae = head.Points[0][0], an = head.Points[0][1];
        double ue = head.Points[1][0] - ae, un = head.Points[1][1] - an;
        double len = Math.Sqrt(ue * ue + un * un);
        if (len < 0.5) return null;
        ue /= len; un /= len;
        double ve = -un, vn = ue;   // across the head
        double re = he - ae, rn = hn - an;
        double along = re * ue + rn * un, across = re * ve + rn * vn;
        double half = head.Width * 0.5;
        if (along < 0.3 || along > len - 0.3 || Math.Abs(across) <= half) return null;
        // the beam out from the hinge points at the head, square to its face
        double toward = -(oe * ve + on * vn) * Math.Sign(across);
        if (toward < 0.94) return null;
        return (float)((Math.Abs(across) - half) / toward);
    }
}
