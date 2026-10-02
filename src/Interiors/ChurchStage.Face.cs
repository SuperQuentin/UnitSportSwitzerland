using Godot;
using static UnitSport.Interiors.InteriorMeshBuilder;

namespace UnitSport.Interiors;

/// <summary>
/// Who the dancing congregation looks at (#370): once up, each turns, where it stands, toward the
/// nearest player in front of it (between it and the altar, or to its sides) and follows them; with
/// nobody in front, as when a player walks back past the row, it turns back to the rat. Eased, and
/// only while dancing: seated, and the frame the music stops, every figure faces the altar exactly
/// as it was built.
/// </summary>
public partial class ChurchStage
{
    /// <summary>How far a congregant looks for someone to face, m.</summary>
    private const float FaceRange = 12f;

    /// <summary>How fast a congregant turns, 1/s (eased toward its target).</summary>
    private const float TurnRate = 5f;

    /// <summary>A probe's stand-ins for players (<c>ChurchStageProbe</c>); null in the game.</summary>
    internal static Func<IEnumerable<Node3D>>? ProbeWatchers;

    private Node3D[] _roots = Array.Empty<Node3D>();
    private float[] _yaw = Array.Empty<float>();
    private float[] _yawWant = Array.Empty<float>();
    private double _faceScan;
    private readonly List<Vector3> _watchers = new();

    /// <summary>The current yaw of figure <paramref name="f"/> off its built facing, radians (for the probe).</summary>
    internal float YawOf(int f) => _yaw[f];

    internal IReadOnlyList<Figure> Figures => _figures;

    private void InitFacing(Node3D[] roots)
    {
        _roots = roots;
        _yaw = new float[roots.Length];
        _yawWant = new float[roots.Length];
    }

    /// <summary>Where a congregant stands when up, in its own frame: the point it turns about.</summary>
    internal Vector3 StandSpot(int f) =>
        new(0, 0, _figures[f].Parts[PersonParts.Torso].Pivot.Z + StandForward);

    /// <summary>Ten times a second: whom each standing congregant should face.</summary>
    private void ScanFacing()
    {
        _watchers.Clear();
        var watchers = ProbeWatchers?.Invoke() ?? Items.RadioManager.Instance?.Players?.Invoke();
        if (watchers != null)
            foreach (var w in watchers)
                if (IsInstanceValid(w) && w.IsInsideTree()) _watchers.Add(ToLocal(w.GlobalPosition));
        var rat = _rat >= 0 ? _figures[_rat].Frame.Origin : Vector3.Zero;
        for (int f = 0; f < _figures.Length; f++)
        {
            if (_figures[f].Kind != FigureKind.Person) continue;
            var frame = _figures[f].Frame;
            var spot = StandSpot(f);
            var inv = frame.AffineInverse();
            Vector3? best = null;
            float bestDist = FaceRange;
            foreach (var w in _watchers)
            {
                var local = inv * w - spot;
                local.Y = 0;
                float d = local.Length();
                // in front: on the altar side of where it stands, not behind its back
                if (local.Z < 0.2f || d >= bestDist) continue;
                bestDist = d;
                best = local;
            }
            var look = best ?? (inv * rat - spot);
            _yawWant[f] = Mathf.Atan2(look.X, look.Z);
        }
    }

    /// <summary>Turns congregant <paramref name="f"/> toward its target, weighted by how far up it is.</summary>
    private void Face(int f, float stand, float dt)
    {
        _yaw[f] = Mathf.LerpAngle(_yaw[f], _yawWant[f] * stand, 1f - Mathf.Exp(-TurnRate * dt));
        var spot = StandSpot(f);
        _roots[f].Transform = _figures[f].Frame
            * new Transform3D(new Basis(Vector3.Up, _yaw[f]), spot - new Basis(Vector3.Up, _yaw[f]) * spot);
    }

    private void StepFacing(float stand, double delta)
    {
        if ((_faceScan -= delta) <= 0)
        {
            _faceScan = 0.1;
            ScanFacing();
        }
        for (int f = 0; f < _figures.Length; f++)
            if (_figures[f].Kind == FigureKind.Person) Face(f, stand, (float)delta);
    }

    /// <summary>Everyone faces the altar again, as built.</summary>
    private void StopFacing()
    {
        for (int f = 0; f < _roots.Length; f++)
        {
            _roots[f].Transform = _figures[f].Frame;
            _yaw[f] = _yawWant[f] = 0f;
        }
        _faceScan = 0;
    }
}
