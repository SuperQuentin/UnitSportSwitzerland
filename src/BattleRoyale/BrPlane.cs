using Godot;
using UnitSport.Avatar;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The cargo plane as every client sees it (#207): the military freighter's model (#420, the same
/// aircraft players fly: <see cref="AirlinerRig.CreateFreighter"/>), gear up, propellers turning, the
/// ramp and the para doors open, and its engines' drone; placed each frame by <see cref="BrManager"/>
/// from <see cref="BrFlight"/>. Only the model: its flight is the server's scripted line, nothing
/// about it is networked.
/// </summary>
public partial class BrPlane : Node3D
{
    private float _bank;
    private readonly AirlinerRig _rig;

    /// <summary>The fuselage's middle, node space of the model: the plane's position is there (the hold, the camera).</summary>
    private static readonly Vector3 Middle = AircraftMeshBuilder.Flip(new Vector3(0, FreighterLayout.CentreY, (FreighterLayout.NoseZ + FreighterLayout.TailZ) * 0.5f));

    /// <summary>Where the jumpers leave from, from the plane's position: the lip of the ramp, open level with the hold floor in flight (#420).</summary>
    public static readonly Vector3 Ramp =
        AircraftMeshBuilder.Flip(new Vector3(0, FreighterLayout.FloorY, FreighterLayout.RampHingeZ - FreighterLayout.RampLength)) - Middle;

    private static readonly AirlinerLook Look = new()
    {
        Gear = 0f,
        Spool = 1f,
        Doors = (byte)(1 << FreighterLayout.RampDoor | 1 << FreighterLayout.ParaDoorL | 1 << FreighterLayout.ParaDoorR),
        Lights = AirlinerLights.Nav | AirlinerLights.Beacon | AirlinerLights.Strobe,
        Airborne = true,
    };

    public BrPlane()
    {
        Name = "CargoPlane";
        TopLevel = true;
        _rig = AirlinerRig.CreateFreighter();
        _rig.Name = "Body";
        _rig.Position = -Middle;
        AddChild(_rig);
        _rig.Show(Look, 0f);
        // four turboprops: the piston loop pitched down, heard from kilometres away
        AddChild(new AudioStreamPlayer3D
        {
            Name = "Engines", Stream = Audio.SfxSynth.Engine, PitchScale = 0.55f, VolumeDb = 8f, UnitSize = 90f,
            MaxDistance = 7000f, Bus = Audio.SfxBus.Name, Autoplay = true,
        });
    }

    public override void _Process(double delta) => _rig.Show(Look, (float)delta);

    /// <summary>Where it is and where it is heading; a slow wing rock so it does not look pinned to a rail.</summary>
    public void Fly(Vector3 at, float yaw)
    {
        _bank = 0.03f * Mathf.Sin((float)Time.GetTicksMsec() / 2300f);
        GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, _bank)), at);
    }
}
