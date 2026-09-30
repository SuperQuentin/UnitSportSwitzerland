using Godot;
using UnitSport.Avatar;

namespace UnitSport.Birds;

/// <summary>
/// A low-poly bird from <see cref="MeshScratch"/> tubes and boxes: a body with a belly patch, a
/// head with a cap in the accent colour, a bill, a tail and legs, plus two wings as SEPARATE
/// meshes so they can flap by rotating about the shoulder. One body plan per
/// <see cref="BodyPlan"/>, scaled to the species' real length and wingspan, so a wren and a
/// bearded vulture share code but not proportions. Built once per species and cached.
/// </summary>
public static class BirdMesh
{
    /// <summary>Proportions as fractions of body length: the handful that tell a heron from a duck.</summary>
    private readonly record struct Plan(float Body, float Radius, float Neck, float Head, float Bill,
        float Tail, float Legs, float Chord, float Taper, bool AccentBill);

    private static Plan For(BodyPlan b) => b switch
    {
        BodyPlan.Passerine => new(0.45f, 0.16f, 0.00f, 0.13f, 0.10f, 0.35f, 0.18f, 0.30f, 0.70f, false),
        BodyPlan.Corvid => new(0.45f, 0.15f, 0.00f, 0.12f, 0.14f, 0.35f, 0.20f, 0.28f, 0.80f, false),
        BodyPlan.Pigeon => new(0.50f, 0.19f, 0.00f, 0.11f, 0.06f, 0.32f, 0.10f, 0.30f, 0.65f, false),
        BodyPlan.Woodpecker => new(0.45f, 0.15f, 0.00f, 0.13f, 0.15f, 0.30f, 0.12f, 0.28f, 0.80f, false),
        BodyPlan.Swift => new(0.50f, 0.14f, 0.00f, 0.12f, 0.03f, 0.30f, 0.02f, 0.18f, 0.40f, false),
        BodyPlan.Raptor => new(0.45f, 0.17f, 0.00f, 0.12f, 0.08f, 0.35f, 0.15f, 0.32f, 0.90f, false),
        BodyPlan.Owl => new(0.50f, 0.22f, 0.00f, 0.20f, 0.05f, 0.20f, 0.12f, 0.34f, 0.90f, false),
        BodyPlan.Grouse => new(0.55f, 0.22f, 0.00f, 0.11f, 0.05f, 0.25f, 0.12f, 0.28f, 0.80f, false),
        BodyPlan.Waterfowl => new(0.55f, 0.17f, 0.20f, 0.10f, 0.12f, 0.10f, 0.06f, 0.22f, 0.60f, true),
        BodyPlan.Gull => new(0.50f, 0.15f, 0.00f, 0.12f, 0.12f, 0.20f, 0.15f, 0.20f, 0.50f, true),
        BodyPlan.Wader => new(0.45f, 0.15f, 0.05f, 0.10f, 0.25f, 0.15f, 0.40f, 0.20f, 0.55f, true),
        BodyPlan.Heron => new(0.40f, 0.12f, 0.35f, 0.08f, 0.22f, 0.10f, 0.55f, 0.30f, 0.85f, true),
        _ => new(0.45f, 0.16f, 0f, 0.13f, 0.1f, 0.35f, 0.18f, 0.3f, 0.7f, false),
    };

    /// <summary>
    /// The meshes of one species. <see cref="Shoulder"/> is where the wing nodes go, in node space;
    /// <see cref="SwimDepth"/> is how far below the origin the water line sits on a swimmer.
    /// </summary>
    public sealed record Parts(ArrayMesh Body, ArrayMesh WingA, ArrayMesh WingB, Vector3 Shoulder, float SwimDepth);

    private static readonly Dictionary<int, Parts> Cache = new();
    private static StandardMaterial3D? _material;

    /// <summary>The same shaded vertex-colour material every figure uses.</summary>
    public static StandardMaterial3D Material => _material ??= HumanMeshBuilder.Material();

    public static Parts Get(BirdSpecies s)
    {
        if (Cache.TryGetValue(s.Index, out var cached)) return cached;
        var p = For(s.Body);
        float L = s.Length;
        var dark = new Color(0.16f, 0.15f, 0.14f);
        var legColour = s.Body is BodyPlan.Heron or BodyPlan.Wader ? s.Accent.Lerp(dark, 0.5f) : dark;

        // authored facing +Z, origin between the feet
        float r = p.Radius * L;
        float bodyLen = p.Body * L;
        float legs = Mathf.Max(p.Legs * L, 0.01f);
        float cy = legs + r;
        var tail = new Vector3(0, cy, -bodyLen * 0.5f);
        var chest = new Vector3(0, cy + r * 0.15f, bodyLen * 0.5f);

        var b = new MeshScratch();
        b.Tube(tail, chest, r * 0.75f, r, s.Back, 6);
        b.Box(new Vector3(0, cy - r * 0.45f, bodyLen * 0.08f), new Vector3(r * 1.4f, r * 0.7f, bodyLen * 0.75f), s.Belly);

        // neck and head: long-necked plans lift the head up and forward
        var neckTop = chest + new Vector3(0, p.Neck * L * 0.85f + r * 0.4f, p.Neck * L * 0.3f);
        if (p.Neck > 0) b.Tube(chest, neckTop, r * 0.45f, r * 0.35f, s.Back, 5);
        float hr = p.Head * L * 0.5f;
        var head = neckTop + new Vector3(0, hr * 0.6f, hr * 0.6f);
        b.Box(head, new Vector3(hr * 1.9f, hr * 1.9f, hr * 2.1f), s.Belly.Lerp(s.Back, 0.5f));
        b.Box(head + new Vector3(0, hr * 0.8f, 0), new Vector3(hr * 1.95f, hr * 0.5f, hr * 2.15f), s.Accent);
        float bill = Mathf.Max(p.Bill * L, 0.006f);
        b.Tube(head + new Vector3(0, -hr * 0.1f, hr), head + new Vector3(0, -hr * 0.25f, hr + bill),
            hr * 0.35f, hr * 0.08f, p.AccentBill ? s.Accent : dark, 4);

        // tail: a flat fan behind the body
        float tl = p.Tail * L;
        b.Box(tail + new Vector3(0, r * 0.1f, -tl * 0.45f), new Vector3(r * 1.3f, r * 0.12f, tl), s.Back);

        // legs, two thin tubes to the ground
        foreach (float x in new[] { -r * 0.4f, r * 0.4f })
            b.Tube(new Vector3(x, cy - r * 0.6f, 0), new Vector3(x, 0, bodyLen * 0.05f), Mathf.Max(r * 0.08f, 0.003f), legColour, 3);

        var parts = new Parts(b.Build(), Wing(s, p, r, +1), Wing(s, p, r, -1),
            // node space: MeshScratch.Build turns +Z into −Z, so the shoulder's z flips
            new Vector3(0, cy + r * 0.5f, -bodyLen * 0.12f),
            cy - r * 0.35f);
        Cache[s.Index] = parts;
        return parts;
    }

    /// <summary>
    /// One wing, origin at the shoulder, lying along ±X: an inner arm and a swept, tapered hand.
    /// Rotating its node about Z flaps it.
    /// </summary>
    private static ArrayMesh Wing(BirdSpecies s, Plan p, float r, int side)
    {
        float span = Mathf.Max((s.Wingspan - r * 2f) * 0.5f, s.Length * 0.4f);
        float chord = p.Chord * s.Length;
        float t = Mathf.Max(chord * 0.08f, 0.004f);
        var w = new MeshScratch();
        w.Box(new Vector3(side * span * 0.26f, 0, -chord * 0.05f), new Vector3(span * 0.52f, t, chord), s.Back);
        w.Box(new Vector3(side * span * 0.74f, 0, -chord * 0.22f), new Vector3(span * 0.48f, t * 0.8f, chord * p.Taper), s.Back.Lerp(s.Accent, 0.15f));
        return w.Build();
    }
}
