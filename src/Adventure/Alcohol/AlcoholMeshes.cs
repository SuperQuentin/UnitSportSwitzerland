using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// The held meshes of the two breathalyzers (#1271, <c>docs/notes/adventure/alcohol.md</c>), real sizes, +Y up, the
/// screen toward +Z (the holder), the origin where the hand closes: the electronic device (a 12 cm body with a
/// screen, a button and a mouthpiece on top) and the single-use test (an 11 cm crystal tube in a paper sleeve,
/// <see cref="Tube"/> clear, green or amber). Built once per look, never per frame.
/// </summary>
public static class AlcoholMeshes
{
    private static readonly Color Shell = new(0.20f, 0.26f, 0.32f);
    private static readonly Color Screen = new(0.55f, 0.85f, 0.55f);
    private static readonly Color Bezel = new(0.06f, 0.07f, 0.08f);
    private static readonly Color Button = new(0.85f, 0.25f, 0.20f);
    private static readonly Color Mouth = new(0.88f, 0.88f, 0.86f);
    private static readonly Color Glass = new(0.80f, 0.90f, 0.95f);
    private static readonly Color Paper = new(0.94f, 0.92f, 0.84f);
    private static readonly Color Green = new(0.20f, 0.72f, 0.28f);
    private static readonly Color Amber = new(0.95f, 0.62f, 0.10f);

    /// <summary>The electronic device, about 12 x 5.5 x 2.4 cm with its mouthpiece.</summary>
    public static void AppendDevice(MeshScratch s)
    {
        s.Box(new Vector3(0, 0, 0), new Vector3(0.055f, 0.105f, 0.024f), Shell);
        s.Box(new Vector3(0, 0.022f, -0.0125f), new Vector3(0.042f, 0.040f, 0.002f), Bezel);              // the screen's bezel (toward the holder after the build's turn)
        s.Box(new Vector3(0, 0.022f, -0.0138f), new Vector3(0.036f, 0.032f, 0.001f), Screen);              // the screen
        s.Box(new Vector3(0, -0.030f, -0.0130f), new Vector3(0.016f, 0.016f, 0.003f), Button);             // the start button
        s.Tube(new Vector3(0, 0.0525f, 0), new Vector3(0, 0.074f, 0), 0.0105f, 0.0085f, Mouth, 8);         // the mouthpiece
        s.Tube(new Vector3(0, 0.0735f, 0), new Vector3(0, 0.0765f, 0), 0.0105f, Bezel, 8);                 // its opening
    }

    /// <summary>The single-use test: a sleeve with a crystal tube in it, the tube <paramref name="colour"/>.</summary>
    public static void AppendTube(MeshScratch s, TubeColour colour)
    {
        s.Tube(new Vector3(0, -0.055f, 0), new Vector3(0, 0.020f, 0), 0.0105f, Paper, 8);                  // the paper sleeve
        s.Tube(new Vector3(0, -0.058f, 0), new Vector3(0, -0.055f, 0), 0.0105f, Bezel, 8);                 // its end cap
        s.Tube(new Vector3(0, 0.020f, 0), new Vector3(0, 0.055f, 0), 0.0070f, Glass, 8);                   // the glass tube above it
        if (colour != TubeColour.Clear)
            s.Tube(new Vector3(0, 0.024f, 0), new Vector3(0, 0.050f, 0), 0.0045f, colour == TubeColour.Green ? Green : Amber, 8);   // the crystals
        else
            s.Tube(new Vector3(0, 0.024f, 0), new Vector3(0, 0.050f, 0), 0.0045f, new Color(0.93f, 0.90f, 0.40f), 8);                // fresh: yellow
        s.Tube(new Vector3(0, 0.055f, 0), new Vector3(0, 0.058f, 0), 0.0075f, Mouth, 8);                  // the cap
    }

    [Showcase("Adventure", "Breathalyzer")]
    private static ArrayMesh DeviceShowcase()
    {
        var s = new MeshScratch();
        AppendDevice(s);
        return s.Build();
    }

    [Showcase("Adventure", "Breath test tubes")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> TubeShowcase()
    {
        foreach (var c in new[] { TubeColour.Clear, TubeColour.Green, TubeColour.Amber })
            yield return (c.ToString().ToLowerInvariant(), () =>
            {
                var s = new MeshScratch();
                AppendTube(s, c);
                return ModelViewer.Shaded(s.Build());
            });
    }
}
