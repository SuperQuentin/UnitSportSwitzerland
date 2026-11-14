using System;
using System.Collections.Generic;
using Godot;

namespace UnitSport.World.Bodies;

/// <summary>
/// Lamps that light another body's ground (#1942): the ground's shader (<c>body_globe.gdshader</c>) is unshaded, lit by
/// its own sun term, so no <see cref="Light3D"/> reaches it. A flood light or a colony's lamp registers its light here
/// as well; <see cref="BodyGround"/> hands the nearest <see cref="Max"/> to its squares' shader (position and range,
/// spot direction and cone, colour times energy) whenever the set changes or moves (a re-anchor), never per frame
/// otherwise. The models round the lamp are lit by the <see cref="Light3D"/> itself.
/// </summary>
public static class GroundLamps
{
    /// <summary>How many lamps the shader takes (its arrays' size).</summary>
    public const int Max = 16;

    private static readonly List<Light3D> Lamps = new();

    /// <summary>Bumped at every add or remove.</summary>
    public static int Version { get; private set; }

    public static IReadOnlyList<Light3D> All => Lamps;

    public static void Add(Light3D light)
    {
        Lamps.Add(light);
        Version++;
    }

    public static void Remove(Light3D light)
    {
        if (Lamps.Remove(light)) Version++;
    }

    /// <summary>
    /// The shader's arrays for the lamps nearest <paramref name="eye"/> (reused, filled in place): <c>lamp_pos</c> (xyz,
    /// range), <c>lamp_dir</c> (a spot's direction, the cosine of its half angle; w 0 for an omni), <c>lamp_col</c>
    /// (colour times energy). Returns the count.
    /// </summary>
    public static int Fill(Vector3 eye, Vector4[] pos, Vector4[] dir, Vector4[] col)
    {
        int n = 0;
        // the nearest first: a simple selection, the list is short
        Span<int> pick = stackalloc int[Max];
        Span<float> best = stackalloc float[Max];
        for (int i = 0; i < Lamps.Count; i++)
        {
            var l = Lamps[i];
            if (!GodotObject.IsInstanceValid(l) || !l.IsInsideTree() || !l.Visible) continue;
            float d = l.GlobalPosition.DistanceSquaredTo(eye);
            int k = n < Max ? n++ : Max;
            if (k == Max)
            {
                if (d >= best[Max - 1]) continue;
                k = Max - 1;
            }
            while (k > 0 && best[k - 1] > d)
            {
                best[k] = best[k - 1];
                pick[k] = pick[k - 1];
                k--;
            }
            best[k] = d;
            pick[k] = i;
        }
        for (int j = 0; j < n; j++)
        {
            var l = Lamps[pick[j]];
            var p = l.GlobalPosition;
            float energy = l.LightEnergy;
            col[j] = new Vector4(l.LightColor.R * energy, l.LightColor.G * energy, l.LightColor.B * energy, 0);
            if (l is SpotLight3D s)
            {
                var forward = -s.GlobalBasis.Z.Normalized();
                pos[j] = new Vector4(p.X, p.Y, p.Z, s.SpotRange);
                dir[j] = new Vector4(forward.X, forward.Y, forward.Z, Mathf.Cos(Mathf.DegToRad(s.SpotAngle)));
            }
            else
            {
                pos[j] = new Vector4(p.X, p.Y, p.Z, l is OmniLight3D o ? o.OmniRange : 20);
                dir[j] = Vector4.Zero;
            }
        }
        return n;
    }
}
