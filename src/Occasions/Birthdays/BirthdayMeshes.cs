using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// The birthday's models (#1423): the cake (one mesh per number of slices left, built when a slice is taken, never per frame), the
/// wrapped gift and the rocket. The hats are figure hats (<see cref="HumanMeshBuilder"/>, <c>HumanMeshBuilder.Birthday.cs</c>).
/// </summary>
public static class BirthdayMeshes
{
    private static readonly Color Sponge = new(0.80f, 0.58f, 0.36f), Cream = new(0.97f, 0.95f, 0.90f), Berry = new(0.86f, 0.18f, 0.32f),
        Plate = new(0.92f, 0.93f, 0.95f), Wax = new(0.35f, 0.62f, 0.95f), Flame = new(1f, 0.80f, 0.28f);

    private static readonly ArrayMesh?[] Cakes = new ArrayMesh?[BirthdayRules.CakeSlices + 1];

    /// <summary>The cake on its plate with <paramref name="left"/> of its <see cref="BirthdayRules.CakeSlices"/> slices; cached per count.</summary>
    public static ArrayMesh Cake(int left)
    {
        left = Math.Clamp(left, 0, BirthdayRules.CakeSlices);
        if (Cakes[left] is { } done) return done;
        var s = new MeshScratch();
        s.Tube(Vector3.Zero, new Vector3(0, 0.012f, 0), 0.21f, 0.2f, Plate, 12);
        const int n = BirthdayRules.CakeSlices;
        for (int i = 0; i < left; i++)
        {
            float a = (i + 0.5f) / n * Mathf.Tau;
            var basis = new Basis(Vector3.Up, a);
            var dir = basis * Vector3.Back;
            // a wedge as two blocks, narrow inside and wide at the rim; cream on top, a berry and a candle per slice
            s.Box(dir * 0.05f + new Vector3(0, 0.067f, 0), new Vector3(0.05f, 0.11f, 0.09f), Sponge, basis);
            s.Box(dir * 0.12f + new Vector3(0, 0.067f, 0), new Vector3(0.10f, 0.11f, 0.08f), Sponge, basis);
            s.Box(dir * 0.05f + new Vector3(0, 0.128f, 0), new Vector3(0.052f, 0.014f, 0.092f), Cream, basis);
            s.Box(dir * 0.12f + new Vector3(0, 0.128f, 0), new Vector3(0.102f, 0.014f, 0.082f), Cream, basis);
            s.Box(dir * 0.12f + new Vector3(0, 0.07f, 0), new Vector3(0.104f, 0.02f, 0.084f), Cream, basis);   // the cream between the layers
            s.Box(dir * 0.15f + new Vector3(0, 0.145f, 0), new Vector3(0.022f, 0.022f, 0.022f), Berry, basis);
            var candle = dir * 0.09f + new Vector3(0, 0.135f, 0);
            s.Tube(candle, candle + new Vector3(0, 0.07f, 0), 0.006f, 0.006f, i % 2 == 0 ? Wax : Berry, 4);
            s.Tube(candle + new Vector3(0, 0.07f, 0), candle + new Vector3(0, 0.095f, 0), 0.008f, 0.001f, Flame, 4);
        }
        return Cakes[left] = s.Build();
    }

    /// <summary>The cake in the hand, with its origin at the grip (under the plate).</summary>
    public static void AppendHeldCake(MeshScratch s)
    {
        s.Tube(new Vector3(0, -0.01f, 0), new Vector3(0, 0.0f, 0), 0.13f, 0.13f, Plate, 10);
        s.Tube(new Vector3(0, 0.0f, 0), new Vector3(0, 0.07f, 0), 0.10f, 0.10f, Sponge, 10);
        s.Tube(new Vector3(0, 0.07f, 0), new Vector3(0, 0.08f, 0), 0.102f, 0.102f, Cream, 10);
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f * Mathf.Tau;
            var at = new Vector3(Mathf.Sin(a) * 0.06f, 0.08f, Mathf.Cos(a) * 0.06f);
            s.Tube(at, at + new Vector3(0, 0.05f, 0), 0.005f, 0.005f, i % 2 == 0 ? Wax : Berry, 4);
            s.Tube(at + new Vector3(0, 0.05f, 0), at + new Vector3(0, 0.068f, 0), 0.007f, 0.001f, Flame, 4);
        }
    }

    /// <summary>The wrapped gift: a pink box, a gold ribbon both ways and a bow.</summary>
    public static void AppendGift(MeshScratch s)
    {
        var paper = new Color(0.90f, 0.38f, 0.62f);
        var ribbon = new Color(0.95f, 0.78f, 0.25f);
        s.Box(new Vector3(0, 0.0f, 0), new Vector3(0.20f, 0.16f, 0.20f), paper);
        s.Box(new Vector3(0, 0.0f, 0), new Vector3(0.205f, 0.165f, 0.04f), ribbon);
        s.Box(new Vector3(0, 0.0f, 0), new Vector3(0.04f, 0.165f, 0.205f), ribbon);
        s.Box(new Vector3(-0.035f, 0.10f, 0), new Vector3(0.07f, 0.04f, 0.03f), ribbon, new Basis(Vector3.Back, 0.5f));
        s.Box(new Vector3(0.035f, 0.10f, 0), new Vector3(0.07f, 0.04f, 0.03f), ribbon, new Basis(Vector3.Back, -0.5f));
    }

    /// <summary>The birthday rocket: a striped tube, a cone and its stick.</summary>
    public static void AppendRocket(MeshScratch s)
    {
        s.Tube(new Vector3(0, -0.22f, 0), new Vector3(0, 0.0f, 0), 0.006f, 0.006f, new Color(0.62f, 0.48f, 0.30f), 4);   // the stick
        s.Tube(new Vector3(0, 0.0f, 0), new Vector3(0, 0.06f, 0), 0.025f, 0.025f, new Color(0.25f, 0.55f, 0.92f), 8);
        s.Tube(new Vector3(0, 0.06f, 0), new Vector3(0, 0.12f, 0), 0.025f, 0.025f, new Color(0.93f, 0.36f, 0.62f), 8);
        s.Tube(new Vector3(0, 0.12f, 0), new Vector3(0, 0.17f, 0), 0.027f, 0.002f, new Color(0.98f, 0.82f, 0.22f), 8);
        s.Tube(new Vector3(0, -0.01f, 0), new Vector3(0, -0.03f, 0), 0.004f, 0.002f, new Color(0.2f, 0.18f, 0.16f), 4);  // the fuse
    }

    private static ArrayMesh Built(Action<MeshScratch> append)
    {
        var s = new MeshScratch();
        append(s);
        return s.Build();
    }

    [Showcase("Birthday", "Cake, slices left")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> CakeShowcase()
    {
        foreach (int left in new[] { 8, 7, 5, 3, 1 })
            yield return ($"{left} left", () => ModelViewer.Shaded(Cake(left)));
    }

    [Showcase("Birthday", "Cake, held")]
    private static ArrayMesh HeldCakeShowcase() => Built(AppendHeldCake);

    [Showcase("Birthday", "Gift")]
    private static ArrayMesh GiftShowcase() => Built(AppendGift);

    [Showcase("Birthday", "Rocket")]
    private static ArrayMesh RocketShowcase() => Built(AppendRocket);
}
