using Godot;

namespace UnitSport.Avatar;

public sealed record SkiPalette(Color Ski, Color Binding, Color Pole, Color Grip)
{
    public static readonly SkiPalette Default = new(
        Ski: new Color(0.88f, 0.24f, 0.16f),
        Binding: new Color(0.13f, 0.14f, 0.16f),
        Pole: new Color(0.58f, 0.60f, 0.64f),
        Grip: new Color(0.10f, 0.10f, 0.12f));

    public static SkiPalette ForRider(int index) => Default with
    {
        Ski = Color.FromHsv((index * 0.37f + 0.18f) % 1f, 0.72f, 0.86f),
    };
}

/// <summary>
/// Skis and poles, and the tucked figure that stands on them.
///
/// <para>
/// Real dimensions again: a 1.70 m all-mountain ski, 88 mm at the waist, mounted so the boot
/// sits slightly behind centre, with the tip rockered up. That last detail is the one that
/// matters at this polygon count — a flat plank under each foot reads as a plank, and the
/// upturned tip is the only cue that says <i>ski</i> from more than ten metres away.
/// </para>
///
/// <para>
/// Built facing +Z, origin on the snow between the skis, matching <see cref="BikeMeshBuilder"/>
/// so both drop into the world under the player with no offset.
/// </para>
/// </summary>
public static class SkierMeshBuilder
{
    private const float SkiLength = 1.70f;
    private const float SkiWidth = 0.088f;
    private const float SkiThickness = 0.022f;

    /// <summary>Lateral stance. Matches the tucked rig's ankles.</summary>
    private const float Stance = 0.110f;

    /// <summary>Boot centre along the ski, and how far the ski runs fore and aft of it.</summary>
    private const float BootZ = 0.100f;
    private const float TailRun = SkiLength * 0.46f;    // boot sits behind centre
    private const float TipRun = SkiLength * 0.54f;

    /// <summary>Just the equipment — for the avatar preview, where the rider is optional.</summary>
    public static ArrayMesh Build(SkiPalette? gear = null)
    {
        var s = new MeshScratch();
        AppendGear(s, gear ?? SkiPalette.Default);
        return s.Build();
    }

    /// <summary>The full figure: skis, poles and a tucked rider, as one surface.</summary>
    public static ArrayMesh BuildSkier(HumanPalette rider, SkiPalette gear)
    {
        var s = new MeshScratch();
        AppendGear(s, gear);
        HumanMeshBuilder.Append(s, rider, HumanPose.Tucked, includeLegs: true, helmet: true);
        return s.Build();
    }

    private static void AppendGear(MeshScratch s, SkiPalette p)
    {
        foreach (float side in new[] { -Stance, Stance })
        {
            var tail = new Vector3(side, SkiThickness * 0.5f, BootZ - TailRun);
            var tip = new Vector3(side, SkiThickness * 0.5f, BootZ + TipRun);

            // the running length as one flat box, then the rockered tip as a second
            var flatTip = tip - new Vector3(0, 0, 0.22f);
            s.Box((tail + flatTip) * 0.5f,
                new Vector3(SkiWidth, SkiThickness, flatTip.Z - tail.Z), p.Ski);
            s.Tube(flatTip, tip + new Vector3(0, 0.075f, 0), SkiWidth * 0.5f, SkiWidth * 0.32f,
                p.Ski, 4);

            // binding and boot
            s.Box(new Vector3(side, 0.055f, BootZ),
                new Vector3(SkiWidth + 0.012f, 0.045f, 0.24f), p.Binding);

            // Pole: from the hand back and down to just off the snow, trailing behind the hip.
            // The basket is thrown well outboard — a pole held parallel to the body would pass
            // through the skier's own shin, which is both wrong and, at this polygon count,
            // exactly what it looks like.
            var grip = new Vector3(side * 1.9f, 0.800f, 0.645f);
            var basket = new Vector3(side * 4.0f, 0.030f, -0.250f);
            s.Tube(grip, basket, 0.010f, 0.008f, p.Pole, 4);
            s.Tube(grip + new Vector3(0, 0.075f, 0.020f), grip, 0.014f, p.Grip, 5);
            s.Ring(basket + (grip - basket).Normalized() * 0.09f,
                (grip - basket).Normalized(), 0.010f, 0.042f, 0.006f, p.Grip, 6);
        }
    }
}
