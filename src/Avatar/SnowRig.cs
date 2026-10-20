using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>
/// A skier or a snowboarder that moves (#933): the body from <see cref="SnowPose"/>, rebuilt into
/// one mesh in place whenever the pose changes by a step (<see cref="SnowPose.Look.Key"/>), so a
/// rider standing still or holding a line costs nothing.
///
/// <para>
/// The skier's skis and poles are in the same surface as the body: they follow its feet and
/// hands (a plough's V, a skating ski, a planted pole). The board does not move against the body
/// except in the roll the whole figure takes, so it is its own mesh, built once; the rider stands
/// on it a quarter turn round, its chest to the toe edge.
/// </para>
/// </summary>
public partial class SnowRig : Node3D
{
    private readonly HumanPalette _palette;
    private readonly SkiPalette _gear;
    private readonly bool _board;
    private readonly bool _facingRight;
    private readonly MeshInstance3D _body;
    private readonly ArrayMesh _mesh = new();
    private readonly MeshScratch _scratch = new();
    private readonly Vector3[] _joints = new Vector3[20];
    private readonly SnowPose.Ski[] _skis = new SnowPose.Ski[2];
    private readonly SnowPose.Pole[] _poles = new SnowPose.Pole[2];
    private (int, int, int, int, int, bool)? _shown;

    /// <summary>The pose last drawn.</summary>
    public SnowPose.Look Look { get; private set; }

    /// <summary>Between the eyes, in this node's frame: where first person looks from.</summary>
    public Vector3 Eye { get; private set; } = new(0, 1.4f, 0.3f);

    private SnowRig(HumanPalette palette, SkiPalette gear, bool board, bool facingRight)
    {
        _palette = palette;
        _gear = gear;
        _board = board;
        _facingRight = facingRight;
        var material = HumanMeshBuilder.FigureMaterial();
        if (board)
        {
            Name = "Snowboarder";
            AddChild(new MeshInstance3D { Name = "Board", Mesh = SnowboardMeshBuilder.Board(gear, facingRight), MaterialOverride = material });
        }
        else Name = "Skier";
        _body = new MeshInstance3D { Name = "Rider", Mesh = _mesh, MaterialOverride = material };
        // the board's rider faces its toe edge: a quarter turn takes the figure's +Z to −X, the right of a board facing +Z
        if (board) _body.Rotation = new Vector3(0, facingRight ? -Mathf.Pi / 2f : Mathf.Pi / 2f, 0);
        if (board) _body.Position = new Vector3(0, 0.03f, 0);
        AddChild(_body);
        Show(new SnowPose.Look(0f, 0f, 0f, 0f, 0f, false));
    }

    public static SnowRig Skier(HumanPalette palette, SkiPalette gear) => new(palette, gear, board: false, facingRight: true);

    public static SnowRig Boarder(HumanPalette palette, SkiPalette gear, bool facingRight) => new(palette, gear, board: true, facingRight);

    /// <summary>Draws <paramref name="look"/>; nothing happens when it is the pose already shown.</summary>
    public void Show(in SnowPose.Look look)
    {
        Look = look;
        var key = look.Key;
        if (_shown == key) return;
        _shown = key;
        _scratch.Clear();
        if (_board)
        {
            // the rider's own frame: the nose is at +X when facing right (its chest at −X of the board)
            float toe = Mathf.Clamp(look.Lean / 1.1f, -1f, 1f) * (_facingRight ? -1f : 1f);
            SnowPose.Boarder(look, toe, _facingRight ? 1f : -1f, _joints);
            HumanMeshBuilder.AppendJoints(_scratch, _palette, _joints, helmet: true);
        }
        else
        {
            SnowPose.Skier(look, _joints, _skis, _poles);
            HumanMeshBuilder.AppendJoints(_scratch, _palette, _joints, helmet: true);
            foreach (var ski in _skis) AppendSki(_scratch, _gear, ski);
            foreach (var pole in _poles) AppendPole(_scratch, _gear, pole);
        }
        _scratch.BuildInto(_mesh);
        var eye = (_joints[SnowPose.HeadBase] + _joints[SnowPose.HeadTop]) * 0.5f + new Vector3(0, 0, 0.12f);
        Eye = _board ? _body.Transform * eye : eye;
        Core.ShowcaseTrace.Mark();
    }

    /// <summary>Every pose the body takes (#933), held still to look at, the whole figure rolled by its lean as the ride rolls it.</summary>
    [Showcase("Figures", "Snow poses")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        var rider = HumanPalette.ForRider(3);
        var gear = SkiPalette.ForRider(3);
        foreach (var (name, look) in new (string, SnowPose.Look)[]
        {
            ("Ski - stance", new(0f, 0f, 0f, 0f, 0f, false)),
            ("Ski - carving left", new(0.2f, 0.7f, 0f, 0f, 0f, false)),
            ("Ski - snowplough", new(0f, 0f, 1f, 0f, 0f, false)),
            ("Ski - skating push", new(0f, 0f, 0f, 1.45f, 0f, false)),
            ("Ski - schuss", new(1f, 0f, 0f, 0f, 0f, false)),
            ("Ski - landing", new(0f, 0f, 0f, 0f, 1f, false)),
            ("Ski - in the air", new(0.3f, 0f, 0f, 0f, 0f, true)),
        })
            yield return (name, () => Posed(Skier(rider, gear), look));
        foreach (var (name, look) in new (string, SnowPose.Look)[]
        {
            ("Board - stance", new(0f, 0f, 0f, 0f, 0f, false)),
            ("Board - toe edge", new(0.2f, -0.8f, 0f, 0f, 0f, false)),
            ("Board - heel edge", new(0.2f, 0.8f, 0f, 0f, 0f, false)),
            ("Board - pushing", new(0f, 0f, 0f, 1.4f, 0f, false)),
            ("Board - tuck", new(1f, 0f, 0f, 0f, 0f, false)),
        })
            yield return (name, () => Posed(Boarder(rider, gear, facingRight: true), look));
    }

    private static Node3D Posed(SnowRig rig, SnowPose.Look look)
    {
        rig.Show(look);
        rig.Rotation = new Vector3(0, 0, look.Lean);
        return rig;
    }

    // the ski: SkierMeshBuilder's 1.70 m, the boot behind centre, the tip rockered up
    private const float SkiWidth = 0.088f, SkiThickness = 0.022f, TailRun = 1.70f * 0.46f, TipRun = 1.70f * 0.54f;

    private static void AppendSki(MeshScratch s, SkiPalette p, SnowPose.Ski ski)
    {
        var turn = new Basis(Vector3.Up, ski.Yaw);
        var boot = ski.Boot + new Vector3(0, SkiThickness * 0.5f, 0);
        float flatTip = TipRun - 0.22f;
        s.Box(boot + turn * new Vector3(0, 0, (flatTip - TailRun) * 0.5f), new Vector3(SkiWidth, SkiThickness, flatTip + TailRun), p.Ski, turn);
        s.Tube(boot + turn * new Vector3(0, 0, flatTip), boot + turn * new Vector3(0, 0.075f, TipRun), SkiWidth * 0.5f, SkiWidth * 0.32f, p.Ski, 4);
        s.Box(boot + new Vector3(0, 0.045f, 0), new Vector3(SkiWidth + 0.012f, 0.045f, 0.24f), p.Binding, turn);
    }

    private static void AppendPole(MeshScratch s, SkiPalette p, SnowPose.Pole pole)
    {
        var along = (pole.Basket - pole.Grip).Normalized();
        s.Tube(pole.Grip, pole.Basket, 0.010f, 0.008f, p.Pole, 4);
        s.Tube(pole.Grip - along * 0.075f, pole.Grip, 0.014f, p.Grip, 5);
        s.Ring(pole.Basket - along * 0.09f, -along, 0.010f, 0.042f, 0.006f, p.Grip, 6);
    }
}
