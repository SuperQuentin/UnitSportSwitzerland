using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The driver at a car's or a heavy's wheel (#1202): its body and its head as two skinned meshes
/// on one set of bones, so the head goes on the own-body layer apart from the body (#833) while
/// both move together. Posed from the wheel and the pedals whenever they move, the bones only;
/// a restyle to or from a lit style (#311) is a new look.
/// </summary>
public sealed class DriverFigure
{
    public SkinnedFigure Body { get; }
    public SkinnedFigure Head { get; }

    private readonly HumanPalette _palette;
    private readonly DriverSeat _seat;
    private (float Wheel, float Throttle, float Brake) _pose = (float.NaN, 0f, 0f);
    private bool _smooth;

    /// <summary>Both meshes, children for the caller to add, at <paramref name="offset"/> in its frame.</summary>
    public DriverFigure(HumanPalette palette, DriverSeat seat, Material material, Vector3 offset = default)
    {
        _palette = palette;
        _seat = seat;
        Body = new SkinnedFigure { Name = "Driver", MaterialOverride = material, Position = offset };
        Head = new SkinnedFigure { Name = "DriverHead", MaterialOverride = material, Position = offset };
        Show();
    }

    private void Show()
    {
        _smooth = HumanMeshBuilder.SmoothFigures;
        Body.Show(new FigureLook(_palette, Head: false));
        Head.Show(new FigureLook(_palette, Body: false));
        Body.Share(Head);
        _pose = (float.NaN, 0f, 0f);
    }

    /// <summary>The hands on the rim at <paramref name="wheel"/> (rad), the feet on the pedals.</summary>
    public void Pose(float wheel, float throttle, float brake)
    {
        if (HumanMeshBuilder.SmoothFigures != _smooth) Show();
        var pose = (wheel, throttle, brake);
        if (pose == _pose) return;
        _pose = pose;
        HumanMeshBuilder.PoseDriver(Body, _seat, wheel, throttle, brake);
    }
}
