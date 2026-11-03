using Godot;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// A chest in the world (#1227, rewards plan 2), on the client: the body, the lid as its own node turned by a tween
/// (never a rebuilt mesh), and a seal's band of light, shown while the chest is sealed for this player (set on a
/// change only). <see cref="Open"/> swings the lid (0.6 s), sends a light shaft up in the tier's colour, then the
/// chest shrinks away: it is gone for this player alone.
/// </summary>
public partial class ChestNode : Node3D
{
    public const float OpenAngle = 1.9f, OpenSeconds = 0.6f, GoneAfter = 2.6f;

    public RewardSpot? Spot { get; private set; }
    private Node3D _lid = null!;
    private MeshInstance3D _band = null!;
    private bool _sealedShown;

    /// <summary>The chest is opening (the reveal plays): nothing to interact with any more.</summary>
    public bool Opening { get; private set; }

    public static ChestNode Build(ChestTier tier, bool sealedBand, float lidAngle, RewardSpot? spot = null)
    {
        var node = new ChestNode { Name = "Chest", Spot = spot };
        var material = RewardMeshes.Material();
        node.AddChild(new MeshInstance3D { Name = "Body", Mesh = RewardMeshes.ChestBody(tier), MaterialOverride = material });
        node._lid = new Node3D { Name = "Lid", Position = RewardMeshes.LidHinge, Rotation = new Vector3(-lidAngle, 0, 0) };
        node._lid.AddChild(new MeshInstance3D { Mesh = RewardMeshes.ChestLid(tier), MaterialOverride = material });
        node.AddChild(node._lid);
        node._band = new MeshInstance3D { Name = "Seal", Mesh = RewardMeshes.SealBand(), MaterialOverride = RewardMeshes.GlowMaterial(), Visible = sealedBand };
        node._sealedShown = sealedBand;
        node.AddChild(node._band);
        return node;
    }

    /// <summary>Shows or hides the seal's band (only when it changes).</summary>
    public void SetSealed(bool shown)
    {
        if (shown == _sealedShown) return;
        _sealedShown = shown;
        _band.Visible = shown;
    }

    /// <summary>The reveal: the lid swings, the shaft rises, the chest goes; <paramref name="gone"/> runs when it has.</summary>
    public void Open(ChestTier tier, Action? gone = null)
    {
        if (Opening) return;
        Opening = true;
        SetSealed(false);
        var shaft = new MeshInstance3D { Name = "Shaft", Mesh = RewardMeshes.Shaft(), MaterialOverride = RewardMeshes.ShaftMaterial(tier),
            Position = new Vector3(0, RewardMeshes.ChestH, 0), Scale = new Vector3(0.2f, 0.05f, 0.2f), Transparency = 0.1f };
        AddChild(shaft);
        var light = new OmniLight3D { LightColor = RewardMeshes.TierColor(tier), LightEnergy = 0, OmniRange = 7, Position = new Vector3(0, 1, 0) };
        AddChild(light);
        var t = CreateTween();
        t.SetParallel();
        t.TweenProperty(_lid, "rotation:x", -OpenAngle, OpenSeconds).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        t.TweenProperty(shaft, "scale", Vector3.One, 0.8f).SetDelay(0.25f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(light, "light_energy", 2.5f, 0.4f).SetDelay(0.25f);
        t.TweenProperty(shaft, "transparency", 1f, 1.2f).SetDelay(1.5f);
        t.TweenProperty(light, "light_energy", 0f, 1.0f).SetDelay(1.6f);
        t.TweenProperty(this, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.4f).SetDelay(GoneAfter - 0.4f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
        t.Chain().TweenCallback(Callable.From(() =>
        {
            Visible = false;
            gone?.Invoke();
        }));
    }

    /// <summary>The lid's turn now (checks: it swung open).</summary>
    public float LidAngle => -_lid.Rotation.X;
}
