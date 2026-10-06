using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Interiors;

/// <summary>
/// A forklift standing in a hall, asleep (#630): the dormant look of the very vehicle it wakes as
/// (<see cref="DormantLooks"/>, built once and shared with the yards' forklifts), on a
/// <see cref="DormantBody"/> that carries its <see cref="VehicleSlot"/> — so aiming at it wakes it
/// through the same <c>VehicleReach</c> -> <c>DormantVehicles.Wake</c> path as a car park's car.
/// A child of its <see cref="InteriorNode"/>, so it comes and goes with the building.
///
/// <para>
/// It hides, and stops being solid, the moment the real vehicle exists (the node of that name
/// arrives in the vehicles: <see cref="Woke"/>), on every peer, and a late joiner never draws it
/// at all: <see cref="_EnterTree"/> looks.
/// </para>
/// </summary>
public partial class ParkedForklift : Node3D
{
    /// <summary>Every hall forklift in the tree, by <c>owner|ordinal</c> (<c>DormantVehicles</c>' key).</summary>
    private static readonly Dictionary<string, ParkedForklift> All = new();

    public VehicleSlot Slot { get; private set; }
    private DormantBody? _body;

    private string Key => $"{Slot.Owner}|{Slot.Ordinal}";

    public static ParkedForklift? Create(InteriorLayout layout, int index, VehicleSlot slot)
    {
        if (DormantLooks.For(DormantLooks.KeyOf(slot), () => Player.Rideable.Create(Player.RideKind.Forklift), drawn: DisplayServer.GetName() != "headless") is not { } look)
            return null;
        var node = new ParkedForklift { Name = $"HallForklift{index}", Slot = slot, Transform = HallForklifts.LocalFrame(layout, layout.Furniture[index]) };
        var body = new DormantBody { Name = "Body", Slot = slot };
        foreach (var (pose, shape) in look.Boxes)
            body.AddChild(new CollisionShape3D { Shape = shape, Transform = pose });
        node._body = body;
        node.AddChild(body);
        if (look.Mesh != null) node.AddChild(new MeshInstance3D { Name = "Look", Mesh = look.Mesh });
        return node;
    }

    public override void _EnterTree()
    {
        All[Key] = this;
        // a late joiner, or a hall built again after it was woken: the vehicle is already in the world
        if (VehicleManager.Instance?.GetNodeOrNull(Slot.NodeName) != null) Show(false);
    }

    public override void _ExitTree()
    {
        if (All.TryGetValue(Key, out var me) && me == this) All.Remove(Key);
    }

    /// <summary>The vehicle of this slot is in the world (<c>DormantVehicles.OnVehicleAdded</c>): undraw the sleeper.</summary>
    public static void Woke(string key)
    {
        if (All.TryGetValue(key, out var node)) node.Show(false);
    }

    private void Show(bool shown)
    {
        Visible = shown;
        if (_body != null)
            foreach (var shape in _body.GetChildren().OfType<CollisionShape3D>())
                shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !shown);
    }
}
