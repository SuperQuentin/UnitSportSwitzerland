using Godot;

namespace UnitSport.Interiors;

/// <summary>The projection POC's (CinemaProjectionPoc) mannequin walking to and fro across the beam: its parts move as nodes, nothing is rebuilt.</summary>
public partial class ProjectionWalker : Node3D
{
    private readonly Material _mat;
    private Node3D _legL = null!, _legR = null!;
    private float _t;

    public ProjectionWalker(Material mat) => _mat = mat;
    public ProjectionWalker() : this(null!) { }

    public override void _Ready()
    {
        MeshInstance3D Part(Mesh m, Vector3 p, Node parent)
        {
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = _mat, Position = p };
            parent.AddChild(mi);
            return mi;
        }
        Part(new CapsuleMesh { Radius = 0.22f, Height = 0.8f }, new Vector3(0, 1.25f, 0), this);
        Part(new SphereMesh { Radius = 0.13f, Height = 0.26f }, new Vector3(0, 1.82f, 0), this);
        foreach (float side in new[] { -1f, 1f })
        {
            var hip = new Node3D { Position = new Vector3(side * 0.11f, 0.9f, 0) };
            AddChild(hip);
            Part(new CapsuleMesh { Radius = 0.08f, Height = 0.9f }, new Vector3(0, -0.45f, 0), hip);
            if (side < 0) _legL = hip; else _legR = hip;
        }
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        // across the beam and back in 8 s, a stride a second and a half
        float x = 4.5f * Mathf.Sin(_t * Mathf.Tau / 8f);
        float dir = Mathf.Cos(_t * Mathf.Tau / 8f) >= 0 ? 1 : -1;
        Position = Position with { X = x };
        Rotation = new Vector3(0, dir > 0 ? Mathf.Pi / 2 : -Mathf.Pi / 2, 0);
        float swing = 0.45f * Mathf.Sin(_t * Mathf.Tau / 1.5f);
        _legL.Rotation = new Vector3(swing, 0, 0);
        _legR.Rotation = new Vector3(-swing, 0, 0);
    }
}
