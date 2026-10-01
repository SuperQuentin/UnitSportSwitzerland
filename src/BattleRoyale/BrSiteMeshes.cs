using Godot;
using UnitSport.Avatar;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The outdoor loot sites as low-poly meshes (#198). The origin is on the ground where the loot
/// is searched. Authored with +Z towards the open side (a bunker's door, a high seat's ladder, a
/// hut's doorway); <see cref="MeshScratch.Build()"/> turns it half round, so on the node the open
/// side is −Z and <see cref="BrSites.YawFacing"/> gives the yaw. Vertex-coloured.
/// </summary>
public static class BrSiteMeshes
{
    private static readonly Dictionary<(CrateStyle, bool), ArrayMesh> Cache = new();

    private static readonly Color Wood = new(0.55f, 0.38f, 0.21f), DarkWood = new(0.38f, 0.25f, 0.13f);
    private static readonly Color Concrete = new(0.58f, 0.57f, 0.54f), Steel = new(0.24f, 0.27f, 0.25f);

    /// <summary>The mesh for a site style (open or locked), or null for the plain crates.</summary>
    public static ArrayMesh? For(CrateStyle style, bool locked)
    {
        if (style is CrateStyle.DeathBox or CrateStyle.Supply or CrateStyle.Military or CrateStyle.Airdrop) return null;
        if (Cache.TryGetValue((style, locked), out var m)) return m;
        var s = new MeshScratch();
        switch (style)
        {
            case CrateStyle.Bunker: Bunker(s, locked); break;
            case CrateStyle.HighSeat: HighSeat(s); break;
            case CrateStyle.HayStash: HayStash(s); break;
            case CrateStyle.SacBox: SacBox(s); break;
            case CrateStyle.Wreck: Wreck(s); break;
            case CrateStyle.FishingHut: FishingHut(s); break;
            case CrateStyle.Pile: Pile(s); break;
        }
        return Cache[(style, locked)] = s.Build();
    }

    /// <summary>A Réduit fort door: a concrete face set into the slope, camouflage, a steel door.</summary>
    private static void Bunker(MeshScratch s, bool locked)
    {
        s.Box(new Vector3(0, 1.4f, -1.4f), new Vector3(6.4f, 3.6f, 3.4f), Concrete);
        // camouflage blotches on the face
        s.Box(new Vector3(-1.9f, 2.3f, 0.31f), new Vector3(1.6f, 0.9f, 0.02f), new Color(0.40f, 0.44f, 0.32f));
        s.Box(new Vector3(2.0f, 0.9f, 0.31f), new Vector3(1.4f, 1.2f, 0.02f), new Color(0.47f, 0.42f, 0.33f));
        s.Box(new Vector3(0, 3.05f, 0.45f), new Vector3(6.6f, 0.25f, 0.6f), new Color(0.50f, 0.49f, 0.46f));   // the lintel
        if (locked)
        {
            s.Box(new Vector3(0, 1.1f, 0.33f), new Vector3(1.4f, 2.2f, 0.08f), Steel);
            s.Box(new Vector3(0.45f, 1.1f, 0.4f), new Vector3(0.16f, 0.16f, 0.06f), new Color(0.75f, 0.75f, 0.72f));   // the dial
        }
        else
        {
            s.Box(new Vector3(0, 1.1f, 0.25f), new Vector3(1.4f, 2.2f, 0.06f), new Color(0.05f, 0.05f, 0.06f));      // the dark inside
            s.Box(new Vector3(-0.72f, 1.1f, 0.95f), new Vector3(0.08f, 2.2f, 1.4f), Steel);                          // the door, swung open
        }
        // a weapons case inside the door
        s.Box(new Vector3(0, 0.25f, 0.9f), new Vector3(1.0f, 0.4f, 0.45f), new Color(0.30f, 0.36f, 0.22f));
    }

    /// <summary>A hunter's high seat (Hochsitz): four legs, a cabin on top, a ladder, a box at the foot.</summary>
    private static void HighSeat(MeshScratch s)
    {
        foreach (var (x, z) in new[] { (-0.8f, -0.8f), (0.8f, -0.8f), (-0.8f, 0.8f), (0.8f, 0.8f) })
            s.Tube(new Vector3(x * 1.25f, 0, z * 1.25f), new Vector3(x, 3.4f, z), 0.08f, DarkWood, 5);
        s.Box(new Vector3(0, 3.45f, 0), new Vector3(2.0f, 0.12f, 2.0f), Wood);
        s.Box(new Vector3(0, 4.15f, -0.85f), new Vector3(1.9f, 1.3f, 0.08f), Wood);
        s.Box(new Vector3(-0.92f, 4.15f, 0), new Vector3(0.08f, 1.3f, 1.8f), Wood);
        s.Box(new Vector3(0.92f, 4.15f, 0), new Vector3(0.08f, 1.3f, 1.8f), Wood);
        s.Box(new Vector3(0, 3.75f, 0.88f), new Vector3(1.9f, 0.5f, 0.08f), Wood);   // the low front, to shoot over
        s.Box(new Vector3(0, 4.95f, 0), new Vector3(2.3f, 0.1f, 2.3f), new Color(0.30f, 0.32f, 0.28f), Basis.FromEuler(new Vector3(0.15f, 0, 0)));
        // the ladder, leaning up to the platform
        foreach (float x in new[] { -0.3f, 0.3f })
            s.Tube(new Vector3(x, 0, 2.3f), new Vector3(x, 3.45f, 1.0f), 0.05f, DarkWood, 4);
        for (int i = 1; i < 9; i++)
        {
            float t = i / 9f;
            s.Tube(new Vector3(-0.3f, 3.45f * t, 2.3f - 1.3f * t), new Vector3(0.3f, 3.45f * t, 2.3f - 1.3f * t), 0.03f, Wood, 4);
        }
        s.Box(new Vector3(0.9f, 0.2f, 1.6f), new Vector3(0.6f, 0.4f, 0.4f), Wood);   // the box at the foot
    }

    /// <summary>Round hay bales stacked by a barn, with a crate tucked behind them.</summary>
    private static void HayStash(MeshScratch s)
    {
        var hay = new Color(0.86f, 0.74f, 0.38f);
        s.Tube(new Vector3(-1.4f, 0.65f, -0.6f), new Vector3(-1.4f, 0.65f, 0.6f), 0.65f, hay, 10);
        s.Tube(new Vector3(0.0f, 0.65f, -0.6f), new Vector3(0.0f, 0.65f, 0.6f), 0.65f, hay, 10);
        s.Tube(new Vector3(-0.7f, 1.8f, -0.6f), new Vector3(-0.7f, 1.8f, 0.6f), 0.65f, new Color(0.80f, 0.68f, 0.34f), 10);
        s.Box(new Vector3(1.2f, 0.25f, 0.2f), new Vector3(0.7f, 0.5f, 0.5f), Wood);
    }

    /// <summary>The SAC's red emergency box on its post, a white cross on the front.</summary>
    private static void SacBox(MeshScratch s)
    {
        s.Tube(Vector3.Zero, new Vector3(0, 1.1f, 0), 0.05f, new Color(0.45f, 0.45f, 0.45f), 5);
        s.Box(new Vector3(0, 1.3f, 0), new Vector3(0.7f, 0.5f, 0.35f), new Color(0.82f, 0.10f, 0.10f));
        s.Box(new Vector3(0, 1.3f, 0.18f), new Vector3(0.24f, 0.07f, 0.01f), Colors.White);
        s.Box(new Vector3(0, 1.3f, 0.18f), new Vector3(0.07f, 0.24f, 0.01f), Colors.White);
        s.Box(new Vector3(0, 1.58f, 0), new Vector3(0.78f, 0.06f, 0.42f), new Color(0.65f, 0.08f, 0.08f));
    }

    /// <summary>A crashed helicopter, burnt: a broken fuselage, its tail boom, bent blades, two crates thrown clear.</summary>
    private static void Wreck(MeshScratch s)
    {
        var burnt = new Color(0.20f, 0.22f, 0.19f);
        var tilt = Basis.FromEuler(new Vector3(0.12f, 0.3f, 0.35f));
        s.Box(new Vector3(0, 1.0f, -3.0f), new Vector3(2.2f, 2.0f, 4.5f), burnt, tilt);
        s.Box(new Vector3(0.2f, 1.2f, -0.6f), new Vector3(1.8f, 1.3f, 1.2f), new Color(0.10f, 0.12f, 0.14f), tilt);   // the cockpit glass
        s.Tube(new Vector3(-0.4f, 1.3f, -5.2f), new Vector3(-1.4f, 1.0f, -10.0f), 0.35f, 0.2f, burnt, 6);
        s.Box(new Vector3(-1.5f, 1.6f, -10.2f), new Vector3(0.15f, 1.4f, 0.8f), burnt);
        s.Box(new Vector3(2.6f, 0.12f, -2.0f), new Vector3(0.25f, 0.08f, 6.5f), new Color(0.30f, 0.30f, 0.30f), Basis.FromEuler(new Vector3(0, 0.7f, 0.1f)));
        s.Box(new Vector3(-2.9f, 0.15f, -4.0f), new Vector3(0.25f, 0.08f, 5.5f), new Color(0.30f, 0.30f, 0.30f), Basis.FromEuler(new Vector3(0, -0.4f, 0.3f)));
        foreach (float x in new[] { -1.1f, 1.1f })
            s.Tube(new Vector3(x, 0.08f, -4.6f), new Vector3(x * 1.2f, 0.1f, -1.2f), 0.06f, new Color(0.35f, 0.35f, 0.35f), 4);
        // scorched ground under it
        s.Box(new Vector3(0, 0.01f, -3.5f), new Vector3(6f, 0.02f, 9f), new Color(0.12f, 0.11f, 0.10f));
        // two crates thrown clear, by the open side
        s.Box(new Vector3(-0.6f, 0.22f, 0.6f), new Vector3(1.1f, 0.44f, 0.5f), new Color(0.30f, 0.36f, 0.22f));
        s.Box(new Vector3(0.8f, 0.22f, 1.1f), new Vector3(1.1f, 0.44f, 0.5f), new Color(0.30f, 0.36f, 0.22f), Basis.FromEuler(new Vector3(0, 0.6f, 0)));
    }

    /// <summary>A small fishing shed: plank walls, a pitched roof, a rod by the door, a crate at it.</summary>
    private static void FishingHut(MeshScratch s)
    {
        s.Box(new Vector3(0, 1.0f, -1.6f), new Vector3(2.4f, 2.0f, 0.1f), Wood);
        s.Box(new Vector3(-1.2f, 1.0f, -0.4f), new Vector3(0.1f, 2.0f, 2.4f), Wood);
        s.Box(new Vector3(1.2f, 1.0f, -0.4f), new Vector3(0.1f, 2.0f, 2.4f), Wood);
        s.Box(new Vector3(-0.75f, 1.0f, 0.8f), new Vector3(0.9f, 2.0f, 0.1f), Wood);
        s.Box(new Vector3(0.75f, 1.0f, 0.8f), new Vector3(0.9f, 2.0f, 0.1f), Wood);
        s.Box(new Vector3(-0.68f, 2.35f, -0.4f), new Vector3(1.55f, 0.08f, 2.8f), DarkWood, Basis.FromEuler(new Vector3(0, 0, 0.5f)));
        s.Box(new Vector3(0.68f, 2.35f, -0.4f), new Vector3(1.55f, 0.08f, 2.8f), DarkWood, Basis.FromEuler(new Vector3(0, 0, -0.5f)));
        s.Tube(new Vector3(1.5f, 0, 1.0f), new Vector3(1.7f, 2.6f, 1.3f), 0.015f, new Color(0.15f, 0.15f, 0.15f), 3);
        s.Box(new Vector3(0, 0.2f, 1.5f), new Vector3(0.6f, 0.4f, 0.4f), Wood);
    }

    /// <summary>What is left of a supply crate shot open: scattered planks and its contents.</summary>
    private static void Pile(MeshScratch s)
    {
        var rng = new Random(7);
        for (int i = 0; i < 7; i++)
            s.Box(new Vector3((float)rng.NextDouble() * 1.4f - 0.7f, 0.03f + i * 0.02f, (float)rng.NextDouble() * 1.4f - 0.7f),
                new Vector3(0.7f, 0.03f, 0.12f), i % 2 == 0 ? Wood : DarkWood, Basis.FromEuler(new Vector3(0, (float)rng.NextDouble() * 3f, 0)));
        s.Box(new Vector3(0.1f, 0.12f, 0), new Vector3(0.3f, 0.2f, 0.25f), new Color(0.35f, 0.40f, 0.30f));
    }
}
