using Godot;
using UnitSport.Avatar;

namespace UnitSport.Items;

/// <summary>
/// The held meshes of the barracks items (#716, docs/notes/items/barracks-items.md): a fanned hand of
/// cards, two stacks of poker chips, a beer bottle and the army's mess tin. Real sizes, built into the
/// caller's <see cref="MeshScratch"/> like every other held mesh (<see cref="ItemDefs.HandMesh"/>):
/// authored with +Y up and the face of a card toward +Z, the holder; the origin is where the hand
/// closes on it.
/// </summary>
public static class BarracksMeshes
{
    private static readonly Color Paper = new(0.96f, 0.94f, 0.88f);
    private static readonly Color CardRed = new(0.82f, 0.10f, 0.12f);
    private static readonly Color CardBlack = new(0.08f, 0.08f, 0.10f);
    private static readonly Color CardBack = new(0.16f, 0.22f, 0.52f);

    /// <summary>
    /// Five 59 x 88 mm cards fanned from the bottom edge, where the hand holds them: the first leans
    /// furthest left and lies at the back, the last is on top, face to the holder. Every card shows its
    /// index (a rank mark and a pip) in the top left corner, the top one an ace as well.
    /// </summary>
    /// <remarks>
    /// <see cref="MeshScratch.Build()"/> turns a mesh half a turn about Y, so the holder (at +Z, looking
    /// down -Z) sees what is authored at -Z, and his right is authored -X: the cards are laid out in
    /// the holder's own terms (<c>vx</c> to his right, <c>vz</c> toward him) and put through that turn here.
    /// </remarks>
    public static void AppendCards(MeshScratch s)
    {
        const float w = 0.058f, h = 0.088f, thick = 0.0014f, step = 0.0026f, fan = 15f;
        const int n = 5;
        var pivot = new Vector3(0, -0.030f, 0);
        for (int i = 0; i < n; i++)
        {
            // the holder sees the top of card i tipped left by this much; about authored +Z that is the other way round
            var rot = new Basis(Vector3.Back, -Mathf.DegToRad((n - 1) / 2f * fan - i * fan));
            float layer = i * step;
            Vector3 At(float vx, float vy, float vz = 0f) => pivot + rot * new Vector3(-vx, vy, 0) + new Vector3(0, 0, -(layer + vz));

            s.Box(At(0, h * 0.5f), new Vector3(w, h, thick), Paper, rot);
            // the back, a hair behind: blue with a white border
            s.Box(At(0, h * 0.5f, -thick * 0.5f - 0.0002f), new Vector3(w * 0.94f, h * 0.95f, 0.0004f), CardBack, rot);

            // the face, a hair in front
            float face = thick * 0.5f + 0.0002f;
            bool red = i is 0 or 2 or 4;
            var ink = red ? CardRed : CardBlack;
            s.Box(At(-0.0215f, h - 0.0135f, face), new Vector3(0.006f, 0.014f, 0.0004f), ink, rot);
            Pip(s, red, At(-0.0215f, h - 0.0300f, face), 0.0085f, ink, rot);
            if (i == n - 1)
            {
                // the ace
                Pip(s, red, At(0, h * 0.5f, face), 0.026f, ink, rot);
                s.Box(At(0.0215f, 0.0135f, face), new Vector3(0.006f, 0.014f, 0.0004f), ink, rot);
            }
        }
    }

    /// <summary>A pip: a diamond for the red suit; the black one grows a stem, so it reads as a spade at a glance.</summary>
    private static void Pip(MeshScratch s, bool red, Vector3 centre, float size, Color ink, Basis card)
    {
        // a square turned 45 degrees about the card's normal is a diamond whose points are size apart
        var diamond = card * new Basis(Vector3.Back, Mathf.Pi / 4f);
        float side = size / Mathf.Sqrt2;
        s.Box(centre, new Vector3(side, side, 0.0004f), ink, diamond);
        if (red) return;
        s.Box(centre - card.Y * size * 0.62f, new Vector3(size * 0.16f, size * 0.42f, 0.0004f), ink, card);
    }

    private static readonly Color ChipRed = new(0.78f, 0.12f, 0.12f);
    private static readonly Color ChipBlue = new(0.14f, 0.30f, 0.72f);
    private static readonly Color ChipGreen = new(0.10f, 0.50f, 0.22f);
    private static readonly Color ChipWhite = new(0.93f, 0.93f, 0.90f);
    private static readonly Color ChipBlack = new(0.10f, 0.10f, 0.12f);

    /// <summary>
    /// Two stacks of 39 mm chips side by side, fourteen and nine high, each chip with its four edge spots
    /// and the top one an inlay. The stacks stand with their middle at the grip.
    /// </summary>
    public static void AppendChips(MeshScratch s)
    {
        Stack(s, -0.024f, 0f, new[]
        {
            ChipRed, ChipRed, ChipRed, ChipRed, ChipWhite, ChipWhite, ChipBlue, ChipBlue, ChipBlue, ChipBlue,
            ChipGreen, ChipGreen, ChipGreen, ChipBlack,
        });
        Stack(s, 0.024f, 0.012f, new[] { ChipGreen, ChipGreen, ChipGreen, ChipBlack, ChipBlack, ChipWhite, ChipRed, ChipRed, ChipRed });
    }

    private static void Stack(MeshScratch s, float x, float z, Color[] chips)
    {
        const float r = 0.0195f, t = 0.0033f;
        float y = -0.023f;   // both stand on one floor, the tall one centred on the grip
        for (int i = 0; i < chips.Length; i++)
        {
            var c = chips[i];
            var spot = c.Luminance > 0.6f ? ChipRed : ChipWhite;
            s.Tube(new Vector3(x, y, z), new Vector3(x, y + t, z), r, c, 12);
            // the edge spots, turned a little more on each chip so the stack is not a barcode
            for (int k = 0; k < 4; k++)
            {
                float a = Mathf.Pi * 0.5f * k + i * 0.35f;
                var at = new Vector3(x + Mathf.Cos(a) * (r + 0.0002f), y + t * 0.5f, z + Mathf.Sin(a) * (r + 0.0002f));
                s.Box(at, new Vector3(0.004f, t * 0.8f, 0.0075f), spot, new Basis(Vector3.Up, -a));
            }
            y += t;
        }
        // the top chip's inlay
        var top = chips[^1];
        s.Tube(new Vector3(x, y, z), new Vector3(x, y + 0.0004f, z), 0.0125f, top.Luminance > 0.6f ? ChipRed : ChipWhite, 12);
        s.Tube(new Vector3(x, y, z), new Vector3(x, y + 0.0008f, z), 0.0085f, top, 12);
    }

    /// <summary>
    /// A 33 cl bottle of beer, 235 mm: brown glass, a paper label with a red band, a gold foil on the
    /// neck, a crown cap. The grip is the middle of the body.
    /// </summary>
    public static void AppendBeer(MeshScratch s)
    {
        var glass = new Color(0.34f, 0.17f, 0.05f);
        var label = new Color(0.93f, 0.88f, 0.70f);
        var red = new Color(0.78f, 0.12f, 0.10f);
        var gold = new Color(0.86f, 0.70f, 0.24f);
        s.Tube(new Vector3(0, -0.065f, 0), new Vector3(0, 0.055f, 0), 0.0295f, glass, 10);                 // the body
        s.Tube(new Vector3(0, 0.055f, 0), new Vector3(0, 0.115f, 0), 0.0295f, 0.0125f, glass, 10);          // the shoulder
        s.Tube(new Vector3(0, 0.115f, 0), new Vector3(0, 0.168f, 0), 0.0125f, 0.0115f, glass, 10);          // the neck
        s.Tube(new Vector3(0, -0.032f, 0), new Vector3(0, 0.042f, 0), 0.0307f, label, 10);                  // the label
        s.Tube(new Vector3(0, 0.001f, 0), new Vector3(0, 0.021f, 0), 0.0311f, red, 10);                     // its band
        s.Tube(new Vector3(0, 0.122f, 0), new Vector3(0, 0.150f, 0), 0.0129f, 0.0124f, gold, 10);           // the foil
        s.Tube(new Vector3(0, 0.1675f, 0), new Vector3(0, 0.1735f, 0), 0.0148f, gold, 8);                   // the crown cap
    }

    /// <summary>
    /// The Swiss army's Gamelle: three nesting parts in olive aluminium (the pot, the dish and the
    /// lid) closed with a wire bail, carried by it. About 20 x 13 x 10 cm; the grip is the top of the bail.
    /// </summary>
    public static void AppendGamelle(MeshScratch s)
    {
        var pot = new Color(0.40f, 0.45f, 0.31f);
        var dish = new Color(0.47f, 0.52f, 0.37f);
        var lid = new Color(0.53f, 0.58f, 0.43f);
        var rim = new Color(0.66f, 0.70f, 0.57f);
        var wire = new Color(0.22f, 0.23f, 0.25f);

        Oblong(s, -0.175f, -0.125f, 0.065f, pot);          // the pot
        Oblong(s, -0.125f, -0.092f, 0.0655f, dish);        // the dish, nested on it
        Oblong(s, -0.1260f, -0.1220f, 0.0685f, rim);       // a rim line between the two
        Oblong(s, -0.092f, -0.080f, 0.0665f, lid);         // the lid
        Oblong(s, -0.0925f, -0.0885f, 0.0685f, rim);       // and its rim line
        s.Tube(new Vector3(0, -0.080f, 0), new Vector3(0, -0.071f, 0), 0.012f, 0.010f, rim, 8);   // the knob

        // the bail: an arch of wire from a lug on each end over the top, the hand on its crown
        ReadOnlySpan<Vector2> arch = [new(-0.102f, -0.108f), new(-0.092f, -0.050f), new(-0.060f, -0.012f), new(0f, 0f),
            new(0.060f, -0.012f), new(0.092f, -0.050f), new(0.102f, -0.108f)];
        for (int i = 0; i + 1 < arch.Length; i++)
            s.Tube(new Vector3(arch[i].X, arch[i].Y, 0), new Vector3(arch[i + 1].X, arch[i + 1].Y, 0), 0.0028f, wire, 5);
        foreach (float sx in stackalloc[] { -1f, 1f })
            s.Box(new Vector3(sx * 0.099f, -0.108f, 0), new Vector3(0.008f, 0.014f, 0.012f), wire);
    }

    /// <summary>A tin's body from <paramref name="y0"/> to <paramref name="y1"/>: two half-round ends and the block between, 20 x 13 cm at radius .065.</summary>
    private static void Oblong(MeshScratch s, float y0, float y1, float radius, Color colour)
    {
        const float half = 0.035f;
        foreach (float sx in stackalloc[] { -1f, 1f })
            s.Tube(new Vector3(sx * half, y0, 0), new Vector3(sx * half, y1, 0), radius, colour, 10);
        s.Box(new Vector3(0, (y0 + y1) * 0.5f, 0), new Vector3(half * 2f, y1 - y0, radius * 2f), colour);
    }
}
