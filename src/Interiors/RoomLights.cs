using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// What lights a building's rooms (#388), as a small float texture the interior shaders read
/// (<c>shaders/body/interior.gdshaderinc</c>): every window to the outside, which lets daylight in
/// and throws the sun's patch on the floor, and a ceiling lamp in every room, on at night and all
/// day in a room with no window. Interior-local coordinates, like the mesh.
/// <para>
/// Row 0 is a table by floor: texel <c>f</c> = (first light, count) of floor <c>f</c>'s list in
/// row 1, so a fragment only walks its own floor's lights. Row 1 holds the lights, four texels
/// each: (centre xyz, kind 0 window / 1 lamp), (window: inward normal x z, half width, half
/// height; lamp: reach, always on, tint (<see cref="Tint"/>), 0), (the room's rectangle x0 z0 x1 z1), (its floor y, its
/// ceiling y, 0, 0). A light only reaches inside its own room's box, so a window never lights
/// the room behind its wall. A room rising through several storeys (a nave) is listed on each.
/// </para>
/// </summary>
public static class RoomLights
{
    public const int TexelsPerLight = 4;

    /// <summary>Metres between high-bay lamps in a room big enough to need more than one (#497).</summary>
    private const float HighBaySpacing = 11f;
    /// <summary>At most this many lamps each way, so a 120 m shed is not 120 lights in the table.</summary>
    private const int MaxHighBays = 6;

    public sealed record Table(ImageTexture Texture, float FloorBase, float StoreyHeight, int Floors);

    public static Table Build(InteriorLayout l)
    {
        int floors = l.Floors.Count;
        var byFloor = new List<float[]>[floors];
        for (int f = 0; f < floors; f++) byFloor[f] = new();

        for (int f = 0; f < floors; f++)
        {
            float y0 = l.FloorY(f);
            foreach (var r in l.Floors[f].Rooms)
            {
                float top = y0 + l.ClearOf(r);
                var lights = new List<float[]>();
                bool windows = false;
                foreach (var o in r.Openings)
                {
                    if (o.Kind != OpeningKind.Window || o.Other != -1) continue;
                    windows = true;
                    var (c, n) = WindowFrame(r, o, y0);
                    lights.Add(Light(c, 0, n.X, n.Z, o.Width / 2, (o.Top - o.Bottom) / 2, r, y0, top));
                }
                // a toilet stall (#1285) has no lamp of its own: the washroom's reaches over it, as a ceiling light does
                // over the cubicles, so a row of them is not a light each
                if (r.Type == RoomType.ToiletStall)
                {
                    for (int s = 0; s < Math.Max(1, r.Span) && f + s < floors; s++) byFloor[f + s].AddRange(lights);
                    continue;
                }
                var box = StallsOf(l.Floors[f].Rooms, r);
                // the lamps hang a little under the ceiling. One in the middle of an ordinary room;
                // a works hall gets the grid of high bays it really has, because one lamp with a 30 m
                // reach lights the middle of a shed and leaves its corners black (#497)
                int cols = Math.Clamp((int)(r.Width / HighBaySpacing), 1, MaxHighBays);
                int rows = Math.Clamp((int)(r.Depth / HighBaySpacing), 1, MaxHighBays);
                float reach = cols * rows > 1
                    ? HighBaySpacing * 0.85f
                    : Mathf.Max(3.5f, 0.75f * Mathf.Max(box.Width, box.Depth));
                for (int cx = 0; cx < cols; cx++)
                    for (int cz = 0; cz < rows; cz++)
                    {
                        var lamp = new Vector3(
                            r.X0 + r.Width * (cx + 0.5f) / cols, top - 0.3f,
                            r.Z0 + r.Depth * (cz + 0.5f) / rows);
                        lights.Add(Light(lamp, 1, reach, windows ? 0 : 1, Tint(r.Type), 0, box, y0, top));
                    }
                for (int s = 0; s < Math.Max(1, r.Span) && f + s < floors; s++)
                    byFloor[f + s].AddRange(lights);
            }
        }

        int count = byFloor.Sum(x => x.Count);
        int width = Math.Max(1, Math.Max(floors, count * TexelsPerLight));
        var image = Image.CreateEmpty(width, 2, false, Image.Format.Rgbaf);
        int at = 0;
        for (int f = 0; f < floors; f++)
        {
            image.SetPixel(f, 0, new Color(at, byFloor[f].Count, 0, 0));
            foreach (var light in byFloor[f])
            {
                for (int t = 0; t < TexelsPerLight; t++)
                    image.SetPixel(at * TexelsPerLight + t, 1, new Color(light[t * 4], light[t * 4 + 1], light[t * 4 + 2], light[t * 4 + 3]));
                at++;
            }
        }
        return new Table(ImageTexture.CreateFromImage(image), l.FloorY(0), l.StoreyHeight, floors);
    }

    /// <summary>A room's rectangle grown over the toilet stalls opening off it (#1285), which its lamp lights.</summary>
    private static RoomPlan StallsOf(List<RoomPlan> rooms, RoomPlan r)
    {
        RoomPlan? box = null;
        foreach (var o in r.Openings)
        {
            if (o.Kind != OpeningKind.Door || o.Other < 0 || o.Other >= rooms.Count || rooms[o.Other].Type != RoomType.ToiletStall) continue;
            var st = rooms[o.Other];
            box ??= new RoomPlan { X0 = r.X0, Z0 = r.Z0, X1 = r.X1, Z1 = r.Z1 };
            (box.X0, box.Z0, box.X1, box.Z1) = (Math.Min(box.X0, st.X0), Math.Min(box.Z0, st.Z0), Math.Max(box.X1, st.X1), Math.Max(box.Z1, st.Z1));
        }
        return box ?? r;
    }

    /// <summary>
    /// A lamp's tint code: 0 the interior's lamp colour; 1 cosmic bowling (#1235), the lane hall's
    /// lamps going blacklight purple as night falls, which leaves its self-lit paint, numbers and
    /// screens glowing; 2 an auditorium's house lights, scaled by the interior material's
    /// <c>house_lights</c> (<see cref="CinemaBooths"/>).
    /// </summary>
    public static float Tint(RoomType t) => t switch
    {
        RoomType.BowlingLanes => 1,
        // #1238: an auditorium's house lights, which the booth dims (the material's `house_lights`)
        RoomType.Auditorium => 2,
        // #1240: an arcade's lamps, dim and violet whatever the hour
        RoomType.ArcadeHall or RoomType.ClubRoom => 3,
        _ => 0,
    };

    private static float[] Light(Vector3 c, float kind, float a, float b, float d, float e, RoomPlan r, float y0, float top)
    {
        // a shaped room (#1290) is lit over all of it, not only its core
        var (x0, z0, x1, z1) = (r.X0, r.Z0, r.X1, r.Z1);
        if (r.Shape != null)
            foreach (var p in r.Corners()) { x0 = Math.Min(x0, p.X); z0 = Math.Min(z0, p.Y); x1 = Math.Max(x1, p.X); z1 = Math.Max(z1, p.Y); }
        return
        [
            c.X, c.Y, c.Z, kind,
            a, b, d, e,
            x0 - 0.01f, z0 - 0.01f, x1 + 0.01f, z1 + 0.01f,
            y0 - 0.05f, top + 0.05f, 0, 0,
        ];
    }

    /// <summary>A window's centre in the wall's plane, and the unit normal into the room.</summary>
    private static (Vector3 Center, Vector3 Inward) WindowFrame(RoomPlan r, OpeningPlan o, float y0)
    {
        float y = y0 + (o.Bottom + o.Top) / 2;
        var (at, outward, _) = r.WallFrame(o);
        return (new Vector3(at.X, y, at.Y), new Vector3(-outward.X, 0, -outward.Y));
    }
}
