using Godot;

namespace UnitSport.Movie;

/// <summary>
/// The movie editor's preview player looks through this (#779): a camera inside the player's
/// viewport that copies the studio's own camera every frame, after it moved. The studio camera stays
/// where it always was (in the world, an anchor for the terrain, the one the probes read); only
/// what it sees is drawn into the player.
/// </summary>
public partial class PreviewCamera : Camera3D
{
    private readonly Camera3D _source;

    public PreviewCamera(Camera3D source)
    {
        _source = source;
        Name = "PreviewCamera";
        // after the studio camera and the look-through pose it was given this frame
        ProcessPriority = 1000;
        ProcessMode = ProcessModeEnum.Always;
        Current = true;
    }

    public override void _Process(double delta)
    {
        if (!IsInstanceValid(_source)) return;
        GlobalTransform = _source.GlobalTransform;
        Fov = _source.Fov;
        Near = _source.Near;
        Far = _source.Far;
    }
}
