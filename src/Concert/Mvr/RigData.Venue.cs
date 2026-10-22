using Godot;

namespace UnitSport.Concert.Mvr;

public sealed partial class RigData
{
    /// <summary>
    /// The whole rig moved by <paramref name="by"/> (#980): fixtures (their place, home and yoke axis)
    /// and objects, as a file placed with its building round the pitch rather than at our deck.
    /// </summary>
    public void Move(Transform3D by)
    {
        foreach (var f in Fixtures)
        {
            f.Pos = V(by * V(f.Pos));
            f.Home = V(by.Basis * V(f.Home));
            f.Up = V(by.Basis * V(f.Up));
        }
        MoveObjects(by);
    }
}
