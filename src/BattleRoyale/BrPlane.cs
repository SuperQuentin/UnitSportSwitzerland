using Godot;
using UnitSport.Avatar;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The cargo plane as every client sees it (#207): the mesh and its engines' drone, placed each frame
/// by <see cref="BrManager"/> from <see cref="BrFlight"/>. Nothing about it is networked.
/// </summary>
public partial class BrPlane : Node3D
{
    private float _bank;

    public BrPlane()
    {
        Name = "CargoPlane";
        TopLevel = true;
        AddChild(new MeshInstance3D { Name = "Body", Mesh = CargoPlaneMeshBuilder.Build(), MaterialOverride = Items.ItemDefs.Material });
        // four turboprops: the piston loop pitched down, heard from kilometres away
        AddChild(new AudioStreamPlayer3D
        {
            Name = "Engines", Stream = Audio.SfxSynth.Engine, PitchScale = 0.55f, VolumeDb = 8f, UnitSize = 90f,
            MaxDistance = 7000f, Bus = Audio.SfxBus.Name, Autoplay = true,
        });
    }

    /// <summary>Where it is and where it is heading; a slow wing rock so it does not look pinned to a rail.</summary>
    public void Fly(Vector3 at, float yaw)
    {
        _bank = 0.03f * Mathf.Sin((float)Time.GetTicksMsec() / 2300f);
        GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, _bank)), at);
    }
}
