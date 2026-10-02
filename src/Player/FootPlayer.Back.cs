using Godot;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>Something carried on the back (#261): the radio, once it is put away, still playing.</summary>
public partial class FootPlayer
{
    /// <summary>
    /// What hangs on this player's back, as an <see cref="ItemId"/> (0 = nothing): a radio carried
    /// but not in the hand. Written by the owner (<c>ItemController</c>), replicated on change so
    /// everyone sees it there, and bouncing when it plays (<see cref="HeldRadio"/>).
    /// </summary>
    [Export] public int BackItemId { get; set; }

    private MeshInstance3D? _backItem;

    /// <summary>
    /// Hangs the back item on the figure's chest frame of this frame's pose, so it rides the
    /// gait, the dance and the hit jolt. A child of the body mesh: hidden with it in first person
    /// and gone with it when the body is rebuilt.
    /// </summary>
    private void PlaceBack(in Avatar.HumanMeshBuilder.GaitMounts m)
    {
        if (_walker == null) return;
        if (BackItemId != (int)ItemId.Radio)
        {
            if (_backItem != null && IsInstanceValid(_backItem)) _backItem.Visible = false;
            return;
        }
        if (_backItem == null || !IsInstanceValid(_backItem) || _backItem.GetParent() != _walker)
        {
            _backItem = new MeshInstance3D
            {
                Name = "BackItem", Mesh = RadioBody.Mesh(), MaterialOverride = ItemDefs.Material,
                Layers = _walker.Layers,
            };
            _backItem.AddChild(new MeshInstance3D { Name = "Straps", Mesh = StrapMesh(), MaterialOverride = ItemDefs.Material, Layers = _walker.Layers });
            _walker.AddChild(_backItem);
        }
        _backItem.Visible = true;

        // the chest's frame in the body mesh's space: the figure faces -Z, so its back is +Z
        var up = (m.Chest - m.Hip).Normalized();
        if (!up.IsFinite() || up.LengthSquared() < 0.5f) up = Vector3.Up;
        var across = (m.ShoulderR - m.ShoulderL) with { Y = 0 };
        across = across.LengthSquared() > 1e-4f ? across.Normalized() : Vector3.Right;
        var back = across.Cross(up).Normalized();
        if (back.Z < 0) back = -back;
        // the radio's face (its -Z) out from the back, standing up the spine, tipped a little forward at the top
        var z = -back;
        var x = up.Cross(z).Normalized();
        var basis = new Basis(x, z.Cross(x), z) * new Basis(Vector3.Right, -0.12f);
        var at = m.Chest + back * 0.2f + up * 0.02f;
        var frame = new Transform3D(basis.Scaled(Vector3.One * 0.85f), at);

        // it dances on the back too, a little less than on the ground
        if (RadioPlay.Decode(HeldRadio) is { } play
            && RadioBody.BeatOf(play.CdId, play.StartedAt, Net.ClockSync.ServerNow, out float phase, out int beat, out _, out _))
            frame *= RadioBody.Bounce(phase, beat, 0.11f, 0.55f);
        _backItem.Transform = frame;
    }

    private static ArrayMesh? _strapMesh;

    /// <summary>Two webbing straps from the radio's handle over the shoulders, in the radio's own frame (face -Z).</summary>
    private static ArrayMesh StrapMesh()
    {
        if (_strapMesh != null) return _strapMesh;
        var s = new Avatar.MeshScratch();
        var webbing = new Color(0.10f, 0.10f, 0.11f);
        foreach (float x in new[] { -0.12f, 0.12f })
        {
            // authored face +Z (the build turns it): up from the handle, over the shoulder toward
            // the body (-Z here) and down the front
            s.Tube(new Vector3(x, 0.16f, -0.02f), new Vector3(x * 1.1f, 0.30f, -0.12f), 0.012f, webbing);
            s.Tube(new Vector3(x * 1.1f, 0.30f, -0.12f), new Vector3(x * 1.2f, 0.27f, -0.32f), 0.012f, webbing);
            s.Tube(new Vector3(x * 1.2f, 0.27f, -0.32f), new Vector3(x * 1.1f, 0.02f, -0.38f), 0.012f, webbing);
        }
        return _strapMesh = s.Build();
    }
}
