using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Interiors;

/// <summary>
/// Every car asleep in one interior's car park (#558, PR 3): the dormant look of the very vehicle
/// each wakes as (<see cref="DormantLooks"/>, built once per kind and shared with the streets' car
/// parks), on a <see cref="DormantBody"/> per bay that carries its <see cref="VehicleSlot"/> — so
/// aiming at one wakes it through the same <c>VehicleReach</c> -> <c>DormantVehicles.Wake</c> path as
/// a street car park's car and a hall's forklift (<see cref="ParkedForklift"/>).
///
/// <para>
/// Built once with the interior and drawn cheaply: one <see cref="MultiMesh"/> per look (a car park
/// of 40 bays is a dozen draw calls, not 40 nodes' worth), one shared box shape per look and one
/// collision body per bay. A child of its <see cref="InteriorNode"/>, so it comes and goes with the
/// building. Nothing here runs per frame.
/// </para>
///
/// <para>
/// A car undraws, and stops being solid, the moment the real vehicle exists (the node of that name
/// arrives in the vehicles: <see cref="Woke"/>), on every peer; a late joiner, or an interior built
/// again afterwards, never draws it (<see cref="_EnterTree"/> looks). When the server puts the slot
/// back to sleep (<c>DormantVehicles.Restock</c>: the car was driven off and gone for
/// <c>RespawnSeconds</c>) every peer draws it again (<see cref="Slept"/>).
/// </para>
/// </summary>
public partial class ParkedCars : Node3D
{
    private sealed class Bay
    {
        public VehicleSlot Slot;
        public DormantBody Body = null!;
        public MultiMesh? Mm;
        public int Instance;
        public Transform3D Pose;
        public bool Shown = true;
        public string Key => $"{Slot.Owner}|{Slot.Ordinal}";
    }

    /// <summary>Every bay car in the tree, by <c>owner|ordinal</c> (<c>DormantVehicles</c>' key).</summary>
    private static readonly Dictionary<string, (ParkedCars Cars, int Bay)> All = new();

    private readonly List<Bay> _bays = new();

    public int Count => _bays.Count;

    /// <summary>The slots of the cars asleep here, drawn or not.</summary>
    public IEnumerable<VehicleSlot> Slots => _bays.Select(b => b.Slot);

    /// <summary>Whether the car of <paramref name="ordinal"/> is drawn (and solid) now.</summary>
    public bool IsDrawn(int ordinal) => _bays.FirstOrDefault(b => b.Slot.Ordinal == ordinal)?.Shown == true;

    /// <summary>The cars of the interior, or null where it has no car park bay to fill.</summary>
    public static ParkedCars? Create(InteriorLayout layout, WorldOrigin origin)
    {
        var cars = new ParkedCars { Name = "HallCars" };
        var groups = new Dictionary<DormantLooks.Key, (DormantLook Look, List<Bay> Bays)>();
        for (int i = 0; i < layout.Furniture.Count; i++)
        {
            if (!HallCars.IsBayCar(layout, layout.Furniture[i]) || HallCars.SlotOf(layout, i, origin) is not { } slot) continue;
            if (DormantVehicles.LookOf(slot) is not { } look) continue;
            var pose = HallCars.LocalFrame(layout, layout.Furniture[i]);
            var body = new DormantBody { Name = $"Bay{i}", Slot = slot, Transform = pose };
            foreach (var (boxPose, shape) in look.Boxes)
                body.AddChild(new CollisionShape3D { Shape = shape, Transform = boxPose });
            cars.AddChild(body);
            var bay = new Bay { Slot = slot, Body = body, Pose = pose };
            cars._bays.Add(bay);
            var key = DormantLooks.KeyOf(slot);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = (look, new List<Bay>());
            group.Bays.Add(bay);
        }
        if (cars._bays.Count == 0) { cars.Free(); return null; }

        foreach (var (look, bays) in groups.Values)
        {
            if (look.Mesh == null) continue;   // a server draws nothing
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = look.Mesh,
                InstanceCount = bays.Count,
            };
            for (int k = 0; k < bays.Count; k++)
            {
                mm.SetInstanceTransform(k, bays[k].Pose);
                bays[k].Mm = mm;
                bays[k].Instance = k;
            }
            cars.AddChild(new MultiMeshInstance3D { Name = $"Look{bays[0].Slot.KindId}", Multimesh = mm });
        }
        return cars;
    }

    public override void _EnterTree()
    {
        for (int i = 0; i < _bays.Count; i++)
        {
            var bay = _bays[i];
            All[bay.Key] = (this, i);
            // a late joiner, or an interior built again after its car was woken: the vehicle is
            // already in the world, or this peer knows the slot is awake
            if (VehicleManager.Instance?.GetNodeOrNull(bay.Slot.NodeName) != null || DormantVehicles.Instance?.IsAwake(bay.Slot) == true)
                Show(bay, false);
        }
    }

    public override void _ExitTree()
    {
        foreach (var bay in _bays)
            if (All.TryGetValue(bay.Key, out var me) && me.Cars == this) All.Remove(bay.Key);
    }

    /// <summary>The vehicle of this slot is in the world (<c>DormantVehicles.OnVehicleAdded</c>): undraw the sleeper.</summary>
    public static void Woke(string key)
    {
        if (All.TryGetValue(key, out var at)) at.Cars.Show(at.Cars._bays[at.Bay], false);
    }

    /// <summary>
    /// The slot sleeps again (<c>DormantVehicles.Slept</c>): its car is drawn once more, unless the
    /// vehicle is somehow back in the world already.
    /// </summary>
    public static void Slept(string key)
    {
        if (!All.TryGetValue(key, out var at)) return;
        var bay = at.Cars._bays[at.Bay];
        if (VehicleManager.Instance?.GetNodeOrNull(bay.Slot.NodeName) == null) at.Cars.Show(bay, true);
    }

    private void Show(Bay bay, bool shown)
    {
        if (bay.Shown == shown) return;
        bay.Shown = shown;
        // a zero scale draws nothing; the instance's place is kept for when it comes back
        bay.Mm?.SetInstanceTransform(bay.Instance, shown ? bay.Pose : new Transform3D(Basis.Identity.Scaled(Vector3.Zero), bay.Pose.Origin));
        foreach (var child in bay.Body.GetChildren())
            if (child is CollisionShape3D shape) shape.SetDeferred(CollisionShape3D.PropertyName.Disabled, !shown);
    }
}
