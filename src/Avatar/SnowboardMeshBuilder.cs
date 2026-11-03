using System;
using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A snowboard and the rider standing across it (#922).
///
/// <para>
/// A 1.56 m twin-tip all-mountain board, 25 cm at the waist, both ends kicked up: like the skis'
/// rockered tip, the kick is what says <i>board</i> at this polygon count. Two bindings with their
/// highbacks on the heel side. Built facing +Z (the board's nose), origin on the snow under its
/// middle, like <see cref="SkierMeshBuilder"/>.
/// </para>
///
/// <para>
/// The rider is <see cref="HumanPose.Boarding"/> turned a quarter turn, the chest to the toe edge, in
/// its own node.
/// </para>
/// </summary>
public static class SnowboardMeshBuilder
{
    private const float Length = 1.56f;
    private const float Width = 0.25f;
    private const float Thickness = 0.02f;
    private const float Kick = 0.16f;
    /// <summary>Binding centres either side of the middle, a shoulder-wide stance: the <see cref="HumanPose.Boarding"/> ankles.</summary>
    private const float Stance = 0.27f;

    /// <summary>The board and bindings, nose to +Z, the highbacks on the heel side (+X for a rider facing −X).</summary>
    public static ArrayMesh Board(SkiPalette p, bool facingRight)
    {
        var s = new MeshScratch();
        float flat = Length / 2f - Kick;
        s.Box(new Vector3(0, Thickness / 2f, 0), new Vector3(Width, Thickness, flat * 2f), p.Ski);
        foreach (float end in new[] { -1f, 1f })
        {
            // turned up about the end of the flat, so the kick starts where the base stops
            var up = new Vector3(0, MathF.Sin(0.45f), end * MathF.Cos(0.45f));
            var from = new Vector3(0, Thickness / 2f, end * flat);
            s.Box(from + up * (Kick / 2f), new Vector3(Width * 0.9f, Thickness, Kick + 0.02f),
                p.Ski, new Basis(Vector3.Right, -end * 0.45f));
        }
        // facing the right of the travel (+Z) is facing −X; the heels, and the highbacks, are on +X
        float heel = facingRight ? 1f : -1f;
        foreach (float z in new[] { -Stance, Stance })
        {
            s.Box(new Vector3(0, 0.045f, z), new Vector3(Width * 0.92f, 0.05f, 0.13f), p.Binding);
            s.Box(new Vector3(heel * Width * 0.40f, 0.13f, z), new Vector3(0.025f, 0.15f, 0.11f), p.Binding);
        }
        return s.Build();
    }

    /// <summary>The figure: the board and the crouched rider across it, chest to the right of the travel if <paramref name="facingRight"/>.</summary>
    public static Node3D Snowboarder(HumanPalette rider, SkiPalette gear, bool facingRight)
    {
        var root = new Node3D { Name = "Snowboarder" };
        var material = HumanMeshBuilder.FigureMaterial();
        root.AddChild(new MeshInstance3D { Name = "Board", Mesh = Board(gear, facingRight), MaterialOverride = material });
        root.AddChild(new MeshInstance3D
        {
            Name = "Rider",
            Mesh = HumanMeshBuilder.Build(rider, HumanPose.Boarding, includeLegs: true, helmet: true),
            MaterialOverride = material,
            // the figure faces +Z; a quarter turn takes its chest to −X, the right of a board facing +Z
            Rotation = new Vector3(0, facingRight ? -Mathf.Pi / 2f : Mathf.Pi / 2f, 0),
            Position = new Vector3(0, 0.03f, 0),
        });
        Core.ShowcaseTrace.Mark();
        return root;
    }
}
