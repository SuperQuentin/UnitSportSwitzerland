using Godot;
using UnitSport.Terrain.Format;

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
/// height; lamp: reach, always on, 0, 0), (the room's rectangle x0 z0 x1 z1), (its floor y, its
/// ceiling y, 0, 0). A light only reaches inside its own room's box, so a window never lights
/// the room behind its wall. A room rising through several storeys (a nave) is listed on each.
/// </para>
/// </summary>
public static class RoomLights
{
    public const int TexelsPerLight = 4;

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
                // the lamp hangs in the middle of the room, where its fixture is drawn (#434); in
                // an abandoned house none works
                if (Lit(l, r))
                {
                    float reach = Mathf.Max(3.5f, 0.75f * Mathf.Max(r.Width, r.Depth));
                    lights.Add(Light(LampAt(l, r, y0), 1, reach, windows ? 0 : 1, 0, 0, r, y0, top));
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

    /// <summary>What hangs from a room's ceiling (#434, drawn by <c>InteriorMeshBuilder.Fixture</c>).</summary>
    public enum Lamp { Hidden, Shade, Chandelier, Bulb, Wire, Panel, Industrial }

    /// <summary>
    /// The room's lamp: nothing drawn in a church (its light stays), a panel in an office, a shop or
    /// a school, an enamel shade over a workshop or a store, else by the house's mood: a chandelier
    /// in a fancy house's grand rooms, a bare bulb in half a messy house's rooms, the cord alone in
    /// an abandoned one, a cloth shade everywhere else.
    /// </summary>
    public static Lamp LampOf(InteriorLayout l, RoomPlan r)
    {
        if (r.Type is RoomType.Nave or RoomType.Belfry or RoomType.Porch) return Lamp.Hidden;
        if (l.Mood == InteriorMood.Abandoned) return Lamp.Wire;
        if (r.Type is RoomType.Office or RoomType.Classroom or RoomType.Shop or RoomType.BankHall or RoomType.Vault
            || r.Type == RoomType.Lobby && l.Kind is not (BuildingKind.House or BuildingKind.Apartment or BuildingKind.Other))
            return Lamp.Panel;
        if (r.Type is RoomType.Workshop or RoomType.Garage or RoomType.Barn or RoomType.Storage or RoomType.Shelter)
            return Lamp.Industrial;
        if (l.Mood == InteriorMood.Fancy && r.Type is RoomType.Living or RoomType.Dining or RoomType.Hall or RoomType.Study
            or RoomType.Bedroom or RoomType.GuestRoom or RoomType.Landing or RoomType.Lobby)
            return Lamp.Chandelier;
        if (r.Type is RoomType.Cellar or RoomType.Laundry or RoomType.Pantry or RoomType.Carnotzet
            || l.Mood == InteriorMood.Messy && Core.Fnv.Unit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{l.Key}|{r.X0:F1}|{r.Z0:F1}")) < 0.5)
            return Lamp.Bulb;
        return Lamp.Shade;
    }

    /// <summary>Whether a room's lamp gives light: not in an abandoned house.</summary>
    public static bool Lit(InteriorLayout l, RoomPlan r) => l.Mood != InteriorMood.Abandoned;

    /// <summary>
    /// Where the room's lamp is (the bulb, or the chandelier's middle), interior-local: in the
    /// middle of the room, hung lower under a high ceiling, never below 2.15 m off the floor.
    /// </summary>
    public static Vector3 LampAt(InteriorLayout l, RoomPlan r, float y0)
    {
        float clear = l.ClearOf(r);
        float drop = LampOf(l, r) switch
        {
            Lamp.Panel => 0.05f,
            Lamp.Chandelier => r.Span > 1 ? 1.5f : 0.55f,
            Lamp.Industrial => 0.6f,
            Lamp.Hidden => 0.3f,
            _ => 0.5f,
        };
        drop = Mathf.Max(0.05f, Mathf.Min(drop, clear - 2.15f));
        return new Vector3((r.X0 + r.X1) / 2, y0 + clear - drop, (r.Z0 + r.Z1) / 2);
    }

    private static float[] Light(Vector3 c, float kind, float a, float b, float d, float e, RoomPlan r, float y0, float top) =>
    [
        c.X, c.Y, c.Z, kind,
        a, b, d, e,
        r.X0 - 0.01f, r.Z0 - 0.01f, r.X1 + 0.01f, r.Z1 + 0.01f,
        y0 - 0.05f, top + 0.05f, 0, 0,
    ];

    /// <summary>A window's centre in the wall's plane, and the unit normal into the room.</summary>
    private static (Vector3 Center, Vector3 Inward) WindowFrame(RoomPlan r, OpeningPlan o, float y0)
    {
        float y = y0 + (o.Bottom + o.Top) / 2;
        return o.Side switch
        {
            Side.Front => (new Vector3(o.Center, y, r.Z0), Vector3.Back),
            Side.Back => (new Vector3(o.Center, y, r.Z1), Vector3.Forward),
            Side.Left => (new Vector3(r.X0, y, o.Center), Vector3.Right),
            _ => (new Vector3(r.X1, y, o.Center), Vector3.Left),
        };
    }
}
