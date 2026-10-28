using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A screen's side curtains follow the picture (#1298), as a cinema's side masking does: at the screen's edges for a
/// scope film (2.39:1, the screen's own shape) and the slides, closing in over the black bars of a narrower one (a 16:9
/// video, a shared monitor) to frame it. Where the room's height made the screen narrower than the stage, they frame it
/// from the start. Each side is its own node over a mesh built once (<see cref="InteriorMeshBuilder.Curtain"/>),
/// stretched from its outer edge along the screen: nothing rebuilt as it moves. It moves on a critically damped spring,
/// so it starts, turns and settles smoothly whatever the picture asks, and is written only while it moves. The picture's
/// width comes from the hall's feed (<see cref="ICinemaFeed.ContentWidth"/>), measured from its frames.
/// </summary>
public partial class InteriorNode
{
    private sealed class Curtains
    {
        public int Hall;
        public MeshInstance3D Left = null!, Right = null!;
        public Vector3 LeftEdge, RightEdge;
        public Basis Along;
        /// <summary>Half the screen's picture width and where a curtain's inner edge is drawn back, from the middle, m.</summary>
        public float HalfPicture, Inner;

        /// <summary>How far a side closes in to frame a picture <paramref name="width"/> of the screen's wide.</summary>
        public float ClosedFor(float width) => Math.Max(0f, Inner - HalfPicture * Math.Clamp(width, NarrowestPicture, 1f));
        /// <summary>How far each side is closed in now, its speed, and where it is going, m.</summary>
        public float Now, Speed, To;
    }

    private readonly List<Curtains> _curtains = new();

    /// <summary>The spring's stiffness, 1/s: about 3 s from drawn back to framing a 16:9 picture, with no overshoot.</summary>
    private const float CurtainOmega = 1.6f;

    /// <summary>Never closer than this share of the picture: a picture measured too narrow (a dark start) keeps some.</summary>
    private const float NarrowestPicture = 0.4f;

    /// <summary>Hall <paramref name="hall"/>'s two side curtains, drawn back, for its screen <paramref name="p"/>.</summary>
    private static void AddCurtains(InteriorNode node, FurniturePlan p, int hall, float halfPicture)
    {
        float y0 = node.Layout.FloorY(p.Floor) + p.Lift;
        var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
        var at = new Vector3(p.X, y0, p.Z);
        MeshInstance3D Side(int side)
        {
            var mi = new MeshInstance3D
            {
                Name = side < 0 ? "CurtainLeft" : "CurtainRight",
                Mesh = BuildMesh(InteriorMeshBuilder.Curtain(p, y0, side), node._lit!),
            };
            node.AddChild(mi);
            return mi;
        }
        var c = new Curtains
        {
            Hall = hall,
            Left = Side(-1),
            Right = Side(1),
            LeftEdge = at + basis * new Vector3(InteriorMeshBuilder.CurtainEdge(p, -1), 0, 0),
            RightEdge = at + basis * new Vector3(InteriorMeshBuilder.CurtainEdge(p, 1), 0, 0),
            Along = basis,
            HalfPicture = halfPicture,
            Inner = InteriorMeshBuilder.CurtainEdge(p, 1) - InteriorMeshBuilder.CurtainReach,
        };
        // framing the screen's own picture from the start: no movement when the cinema is built
        c.Now = c.To = c.ClosedFor(1f);
        Stretch(c.Left, c.LeftEdge, c.Along, c.Now);
        Stretch(c.Right, c.RightEdge, c.Along, c.Now);
        node._curtains.Add(c);
    }

    /// <summary>
    /// Hall <paramref name="hall"/>'s picture is <paramref name="width"/> of the screen's width (1: all of it, the curtains
    /// drawn back): the curtains go to its edges. Per frame is fine: it only sets where they are going.
    /// </summary>
    public void FrameCurtains(int hall, float width)
    {
        foreach (var c in _curtains)
            if (c.Hall == hall)
                c.To = c.ClosedFor(width);
    }

    /// <summary>Per frame from <see cref="CinemaBooths"/>: each curtain a step along its spring, written only while it moves.</summary>
    public void MoveCurtains(double delta)
    {
        float dt = Math.Min((float)delta, 0.05f);
        foreach (var c in _curtains)
        {
            if (Math.Abs(c.To - c.Now) < 0.0005f && Math.Abs(c.Speed) < 0.0005f) continue;
            // critically damped: x'' = w²(to - x) - 2w x'
            c.Speed += (CurtainOmega * CurtainOmega * (c.To - c.Now) - 2f * CurtainOmega * c.Speed) * dt;
            c.Now += c.Speed * dt;
            if (Math.Abs(c.To - c.Now) < 0.0005f && Math.Abs(c.Speed) < 0.0005f) { c.Now = c.To; c.Speed = 0; }
            Stretch(c.Left, c.LeftEdge, c.Along, c.Now);
            Stretch(c.Right, c.RightEdge, c.Along, c.Now);
        }
    }

    /// <summary>A side's node stretched along the screen from its outer edge, so its folds reach <paramref name="closed"/> m further in.</summary>
    private static void Stretch(MeshInstance3D side, Vector3 edge, Basis along, float closed)
    {
        float k = (InteriorMeshBuilder.CurtainReach + closed) / InteriorMeshBuilder.CurtainReach;
        var m = along * Basis.FromScale(new Vector3(k, 1, 1)) * along.Transposed();
        side.Transform = new Transform3D(m, edge - m * edge);
    }
}
