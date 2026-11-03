using Godot;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>
/// Makes one of a shot's props liftable by a tractor beam (#882, <see cref="Prop.Beamable"/>): the
/// <see cref="IBeamable"/> seam of #861 over a node that has no physics of its own (an item set down,
/// a box, a seated figure, a placed look). A beam over it pulls it up to the hatch and holds it there,
/// turning slowly; let go, it comes down at the beam's gentle rate to where it stood. The director's
/// props are this peer's alone (the films run offline), so it is always movable here.
/// </summary>
public partial class BeamableProp : Node3D
{
    private readonly Node3D _target;
    private readonly float _restY;
    private bool _lifted;

    public BeamableProp(Node3D target)
    {
        _target = target;
        _restY = target.GlobalPosition.Y;
        Name = $"Beamable{target.Name}";
    }

    public BeamableProp() : this(new Node3D()) { }

    public override void _EnterTree() => TractorBeams.Register(Thing);
    public override void _ExitTree() => TractorBeams.Unregister(Thing);

    private Lift? _thing;
    private Lift Thing => _thing ??= new Lift(this);

    /// <summary>The seam itself: a plain object, so the node's own lifetime decides when it is registered.</summary>
    private sealed class Lift : IBeamable
    {
        private readonly BeamableProp _p;
        public Lift(BeamableProp p) => _p = p;

        private bool Alive => IsInstanceValid(_p._target) && _p._target.IsInsideTree();

        public Vector3 BeamPosition => Alive ? _p._target.GlobalPosition : new Vector3(0, -1e6f, 0);
        public bool BeamMovable => Alive;
        public bool BeamGrounded => !Alive || _p._target.GlobalPosition.Y <= _p._restY + 0.02f;

        public void BeamMove(Vector3 velocity, float dt)
        {
            if (!Alive) return;
            var t = _p._target;
            if (!_p._lifted)
            {
                _p._lifted = true;
                GD.Print($"[trailer] {t.Name} taken by a beam");
            }
            var at = t.GlobalPosition + velocity * dt;
            // never under where it stood: the beam lowers it onto the ground it was put on
            if (at.Y < _p._restY) at.Y = _p._restY;
            t.GlobalPosition = at;
            t.RotateY(0.8f * dt);
        }

        public void BeamRelease()
        {
            if (Alive) _p._target.GlobalPosition = _p._target.GlobalPosition with { Y = _p._restY };
        }
    }
}
