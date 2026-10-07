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
    private RadioSparkles? _backSparkles;

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
            _backItem.AddChild(_backSparkles = new RadioSparkles { Layers = _walker.Layers });
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
        var at = m.Chest + back * 0.18f - up * 0.07f;
        var frame = new Transform3D(basis.Scaled(Vector3.One * 0.85f), at);

        // it dances (a little less than on the ground) and sparkles on the back too
        var play = RadioPlay.Decode(HeldRadio);
        bool playing = play.HasValue;
        var groove = playing ? RadioGroove.Of(play!.Value.CdId, play.Value.StartedAt, Net.ClockSync.ServerNow) : RadioGroove.Silent;
        if (groove.Beating) frame *= RadioBody.Bounce(groove.Phase, groove.Beat, 0.11f, 0.55f * groove.BounceScale);
        _backItem.Transform = frame;
        _backSparkles?.Step(playing, groove, (float)GetProcessDeltaTime());
    }

    private static ArrayMesh? _strapMesh;

    /// <summary>
    /// The harness, in the radio's own frame (face -Z once built; authored with the body toward -Z
    /// here): per side a closed loop of webbing from the handle up over a padded shoulder, down
    /// the front, under the arm and back to a tab at the radio's lower corner (#725), plus a
    /// chest strap with its buckle and a slider on each front run.
    /// </summary>
    [Core.Showcase("Items", "Radio backpack straps")]
    private static ArrayMesh StrapMesh()
    {
        if (_strapMesh != null) return _strapMesh;
        var s = new Avatar.MeshScratch();
        var webbing = new Color(0.10f, 0.10f, 0.11f);
        var pad = new Color(0.20f, 0.06f, 0.07f);
        var metal = new Color(0.62f, 0.64f, 0.66f);
        const float W = 0.016f;
        foreach (float x in new[] { -0.12f, 0.12f })
        {
            float side = Mathf.Sign(x);
            // the loop: handle, top of the shoulder, front, chest, under the arm, the lower corner
            var handle = new Vector3(x, 0.16f, -0.02f);
            var top = new Vector3(x * 1.1f, 0.30f, -0.12f);
            var front = new Vector3(x * 1.2f, 0.27f, -0.32f);
            var chest = new Vector3(x * 1.12f, 0.06f, -0.37f);
            var waist = new Vector3(x * 1.2f, -0.04f, -0.33f);
            var armpit = new Vector3(side * 0.21f, -0.06f, -0.22f);
            var corner = new Vector3(side * 0.19f, -0.085f, -0.09f);
            s.Tube(handle, top, W, webbing);
            s.Tube(top, front, W * 1.7f, pad);            // the padded part over the shoulder
            s.Tube(front, chest, W, webbing);
            s.Tube(chest, waist, W, webbing);
            s.Tube(waist, armpit, W, webbing);
            s.Tube(armpit, corner, W, webbing);
            // a slider on the front run, and the tabs it is sewn to on the radio
            s.Box(front.Lerp(chest, 0.75f), new Vector3(0.036f, 0.012f, 0.012f), metal);
            s.Box(handle + new Vector3(0, -0.02f, -0.004f), new Vector3(0.034f, 0.04f, 0.008f), webbing);
            s.Box(corner + new Vector3(0, 0.012f, 0.004f), new Vector3(0.04f, 0.036f, 0.008f), webbing);
        }
        // the chest strap across both front runs, and its buckle in the middle
        var l = new Vector3(-0.137f, 0.14f, -0.355f);
        var r = new Vector3(0.137f, 0.14f, -0.355f);
        s.Tube(l, r, W * 0.8f, webbing);
        s.Box(new Vector3(0, 0.14f, -0.36f), new Vector3(0.05f, 0.03f, 0.014f), metal);
        s.Box(new Vector3(0, 0.14f, -0.368f), new Vector3(0.02f, 0.012f, 0.004f), new Color(0.80f, 0.12f, 0.10f));
        return _strapMesh = s.Build();
    }

    /// <summary>The radio as it is worn (#725): the boombox and its harness together, in the model viewer.</summary>
    [Core.Showcase("Items", "Radio backpack")]
    private static Node3D ShowcaseBackpack()
    {
        var radio = new MeshInstance3D { Mesh = RadioBody.Mesh(), MaterialOverride = ItemDefs.Material };
        radio.AddChild(new MeshInstance3D { Mesh = StrapMesh(), MaterialOverride = ItemDefs.Material });
        return radio;
    }
}
