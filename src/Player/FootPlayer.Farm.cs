using Godot;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Working the fields from a machine (#494): {kneel} lowers or raises the implement or the header;
/// while it is down and moving, the local driver's peer sweeps the working bar over the ground
/// once a physics tick (<see cref="FarmWork.Sweep"/>). A drill sows the seed in the pack, the
/// mower puts hay in the pack, the combine fills its tank; the auger unloads the tank into a
/// tipping trailer standing under it or towed alongside by another player, and a loaded tank or
/// trailer is sold at a farm co-op ({destination} / X: a tipping trailer tips its bin to deliver).
/// On foot, E at a loaded tank or trailer takes a sack (held, or with {sprint}: ten).
/// </summary>
public partial class FootPlayer
{
    /// <summary>The working bar's middle last tick, world; null when nothing was working.</summary>
    private Vector3? _workLast;
    /// <summary>Seed a drill has used toward the next whole item, and hay the mower has cut toward the next bale.</summary>
    private float _seedOwed, _hayOwed;
    private float _farmToastCooldown, _augerTimer;
    /// <summary>A delivery asked of the server and not answered yet (s left to wait): nothing is sold twice.</summary>
    private float _deliverWait;
    /// <summary>How long the tipping trailer's bin stays up, s (the rig takes 3 s to rise).</summary>
    private float _tipTimer;
    private const float TipHold = 7f;
    /// <summary>Sacks offered to another player's driven tipping trailer, waiting for its answer (s left), and how many.</summary>
    private float _augerWait;
    private int _augerOffered;
    /// <summary>Both machines must be this slow for the auger to unload into a driven trailer, m/s (~11 km/h).</summary>
    public const float AugerDrivenSpeed = 3f;

    /// <summary>Sacks one swing of the auger moves into the trailer, and how often it swings, s.</summary>
    private const int AugerBatch = 8;
    private const float AugerEvery = 2f;
    /// <summary>How far round a tipping trailer's body or a combine's tank a walker reaches its sacks, m.</summary>
    private const float SackReach = 1.4f;

    /// <summary>Sweeps made from a machine since this player was made, and the cells they worked (for the checks).</summary>
    public int FarmStrokes { get; private set; }
    public int FarmCells { get; private set; }
    /// <summary>Seed bags a drill has taken, bales the mower has given and sacks taken on foot (for the checks).</summary>
    public int FarmSeedUsed { get; private set; }
    public int FarmHayCut { get; private set; }
    /// <summary>Bales the mower cut with no room for them in the pack: left on the field.</summary>
    public int FarmHayLeft { get; private set; }
    public int FarmSacksTaken { get; private set; }

    private void FarmToast(string text)
    {
        if (_farmToastCooldown > 0f) return;
        _farmToastCooldown = 4f;
        Announced?.Invoke(text, false);
    }

    // ---- in the seat ------------------------------------------------------------------------------

    /// <summary>The farm machine's own keys: {kneel} lowers or raises, {destination} / {car_door} delivers or swings the auger.</summary>
    private bool HandleFarmInput(InputEvent e, Truck truck)
    {
        if (e.IsActionPressed(PlayerInput.Kneel)) { ToggleLowered(truck); return true; }
        if (e.IsActionPressed(PlayerInput.Destination) || e.IsActionPressed(PlayerInput.CarDoor)) { FarmAction(truck); return true; }
        return false;
    }

    /// <summary>{kneel}: the implement or the header down to work, or up for the road.</summary>
    public void ToggleLowered(Truck truck)
    {
        if (truck.Spec.Tool == FarmTool.None && truck.Implement == null)
        {
            FarmToast(InputHints.Format("Nothing to lower: back up to a plough, drill or mower and {couple}"));
            return;
        }
        truck.Lowered = !truck.Lowered;
        _workLast = null;
        string what = truck.Implement is { } imp ? imp.Label.ToLowerInvariant() : "header";
        Announced?.Invoke(truck.Lowered ? $"{what} DOWN" : $"{what} UP", true);
    }

    /// <summary>The load this machine can deliver: the combine's tank, else the coupled tipping trailer's.</summary>
    public Tank FarmLoad(Truck truck) => truck.Spec.TankItems > 0 ? truck.Tank : truck.TrailerTank;

    /// <summary>Stopped by a farm co-op with something to sell, no delivery under way.</summary>
    public bool CanDeliver(Truck truck) => _deliverWait <= 0f && !truck.Tipping && GroundSpeed < 1.5f && FarmLoad(truck).Items > 0 && FarmMarket.NearCoop(GlobalPosition);

    /// <summary>The francs the last delivery paid, and how many deliveries were paid (for the checks).</summary>
    public int FarmFrancsPaid { get; private set; }
    public int FarmDeliveries { get; private set; }

    /// <summary>
    /// {destination} / {car_door} in a farm machine: with a tipping trailer coupled, tip its bin
    /// (at a co-op that delivers the load); in a combine, deliver at a co-op, else swing the auger.
    /// </summary>
    public void FarmAction(Truck truck)
    {
        if (truck.Spec.TankItems <= 0 && truck.TrailerCapacity > 0) { Tip(truck); return; }
        if (CanDeliver(truck)) { Deliver(truck); return; }
        if (truck.Spec.TankItems > 0)
        {
            truck.AugerOut = !truck.AugerOut;
            _augerTimer = 0f;
            Announced?.Invoke(truck.AugerOut ? "AUGER OUT" : "AUGER IN", true);
            return;
        }
        FarmToast(FarmLoad(truck).Items > 0 ? "Drive the load to a farm co-op to sell it" : "Nothing to deliver");
    }

    /// <summary>
    /// The tipping trailer's delivery gesture: stopped, its bin tips up for a few seconds (the
    /// pose's tipping bit, <see cref="Truck.Tipping"/>). By a farm co-op the load pours out into it
    /// and is sold; anywhere else nothing is poured (the harvest is not thrown on the ground).
    /// </summary>
    public void Tip(Truck truck)
    {
        if (truck.Tipping || _deliverWait > 0f) return;
        if (GroundSpeed >= 1.5f) { FarmToast("Stop to tip the trailer"); return; }
        var load = FarmLoad(truck);
        bool coop = FarmMarket.NearCoop(GlobalPosition);
        truck.Tipping = true;
        _tipTimer = TipHold;
        Announced?.Invoke("TIPPING", true);
        if (load.Items <= 0) FarmToast("The trailer is empty: nothing to tip");
        else if (!coop) FarmToast("Not at a farm co-op: the load stays in the trailer");
        else Deliver(truck);
    }

    /// <summary>Sells the whole load at the co-op (<see cref="FarmMarket.Deliver"/>): the tank is emptied once the francs are paid.</summary>
    private void Deliver(Truck truck)
    {
        var load = FarmLoad(truck);
        var item = FarmTables.YieldOf(load.Crop);
        if (item == ItemId.None || _deliverWait > 0f) return;
        bool combine = truck.Spec.TankItems > 0;
        _deliverWait = 10f;
        FarmMarket.Deliver(this, GlobalPosition, item, load.Items, francs =>
        {
            _deliverWait = 0f;
            if (francs <= 0) { Announced?.Invoke("The co-op does not take it now", false); return; }
            FarmFrancsPaid = francs;
            FarmDeliveries++;
            if (_ride is not Truck t) return;
            if (combine) t.SetTank(MachineLoad.Take(t.Tank, load.Items, out _));
            else if (t.SetTrailerTank(MachineLoad.Take(t.TrailerTank, load.Items, out _)))
            {
                TrailerCode = t.TrailerCode;
                RefreshVisual(force: true);
            }
            Announced?.Invoke($"DELIVERED {load.Items} × {ItemDefs.Get(item)?.Name ?? "sacks"}: +{francs} CHF", true);
        });
    }

    /// <summary>
    /// Owner, after the train has moved: the working bar from last tick to this one is worked
    /// (<see cref="FarmWork.Sweep"/>), its yield or seed settled; the auger unloads.
    /// </summary>
    private void StepFarm(Truck truck, float dt)
    {
        if (Npc || SeatIndex != 0 || !IsMultiplayerAuthority()) return;
        // the bin comes back down after a while, or as soon as the tractor drives off
        if (truck.Tipping && ((_tipTimer -= dt) <= 0f || GroundSpeed > 2f)) truck.Tipping = false;
        if (truck.Spec.TankItems > 0) StepAuger(truck, dt);
        var tool = truck.WorkTool;
        if (tool == FarmTool.None) { _workLast = null; return; }
        var bar = ToGlobal(truck.WorkBarNode);
        var from = _workLast;
        _workLast = bar;
        if (from is not { } a || GroundSpeed < 0.2f || a.DistanceSquaredTo(bar) > 25f) return;

        var seed = CropKind.None;
        var seedItem = ItemId.None;
        if (tool == FarmTool.Sow)
        {
            (seedItem, seed) = SeedInPack();
            if (seed == CropKind.None) { FarmToast("No seed in your pack: the drill sows nothing"); return; }
        }
        if (tool == FarmTool.Harvest)
        {
            var tank = truck.Tank;
            if (tank.Items >= truck.TankCapacity) { FarmToast("TANK FULL: swing the auger out over a tipping trailer"); return; }
            // a tank holds one crop: a field of another is left standing, not cut and lost
            if (!tank.Empty && FarmWork.CellAt(bar) is { Stage: FieldStage.Ripe } cell && cell.Crop != CropKind.None && cell.Crop != tank.Crop)
            {
                FarmToast($"The tank holds {tank.Crop}: empty it before harvesting {cell.Crop}");
                return;
            }
        }

        var stroke = MachineWork.Sweep(tool, a, bar, truck.WorkWidth, seed);
        FarmStrokes++;
        FarmCells += stroke.Cells;
        if (stroke.Cells <= 0 && stroke.Units <= 0f) return;
        switch (tool)
        {
            case FarmTool.Sow:
                int due = MachineLoad.SeedDue(ref _seedOwed, stroke.Units);
                if (due > 0) FarmSeedUsed += ItemController.Instance?.Inventory.TakePlain(seedItem, due) ?? 0;
                break;
            case FarmTool.Mow:
                _hayOwed += stroke.Units;
                int bales = (int)Mathf.Floor(_hayOwed);
                if (bales > 0)
                {
                    _hayOwed -= bales;
                    // only what fits: Give drops the rest at the player's feet, and a driver's feet are
                    // inside the tractor — dropped bales lifted it off the ground and threw it about
                    int room = ItemController.Instance?.Inventory.Room(ItemId.HayBale) ?? 0;
                    int given = Mathf.Min(room, bales);
                    if (given > 0) ItemController.Instance!.Give(new ItemStack(ItemId.HayBale, given));
                    FarmHayCut += given;
                    FarmHayLeft += bales - given;
                    if (given < bales) FarmToast("Your pack is full: the hay is left on the field");
                }
                break;
            case FarmTool.Harvest:
                var next = MachineLoad.Add(truck.Tank, stroke.Crop, stroke.Units, truck.TankCapacity, out var refused);
                truck.SetTank(next);
                if (refused == TankRefusal.Full) FarmToast("TANK FULL");
                else if (refused == TankRefusal.OtherCrop) FarmToast($"The tank holds {truck.Tank.Crop}: that {stroke.Crop} is lost");
                break;
        }
    }

    /// <summary>The first seed in the pack, the hotbar's first: its item and the crop it sows.</summary>
    private static (ItemId Item, CropKind Crop) SeedInPack()
    {
        if (ItemController.Instance?.Inventory is not { } inv) return (ItemId.None, CropKind.None);
        for (int i = 0; i < inv.Capacity; i++)
        {
            var stack = inv[i];
            if (stack.IsEmpty || stack.Data != null) continue;
            var crop = FarmTables.CropOf(stack.Id);
            if (crop != CropKind.None) return (stack.Id, crop);
        }
        return (ItemId.None, CropKind.None);
    }

    /// <summary>
    /// The auger out over a tipping trailer: a batch of sacks into it every couple of seconds. A
    /// parked one is claimed and parked back; one another player is towing alongside is asked
    /// through the server (<see cref="PassengerService.OfferAuger"/>): its driver's peer owns its
    /// load, takes what fits and says how much, and only that leaves the tank.
    /// </summary>
    private void StepAuger(Truck truck, float dt)
    {
        if (_augerWait > 0f && (_augerWait -= dt) <= 0f) _augerOffered = 0;
        if (!truck.AugerOut || truck.Tank.Items <= 0 || Vehicles is not { } vehicles) { _augerTimer = 0f; return; }
        _augerTimer += dt;
        if (_augerTimer < AugerEvery || vehicles.Claiming || _augerOffered > 0) return;
        _augerTimer = 0f;
        var spout = ToGlobal(Avatar.FarmMeshBuilder.AugerSpout(truck.Train.Bodies[0].CgAt));
        if (TipperAt(spout, 0.3f) is not { } target)
        {
            if (DrivenTipperAt(spout, 0.6f) is { } driver)
            {
                if (GroundSpeed > AugerDrivenSpeed || driver.WorldVelocity.Length() > AugerDrivenSpeed)
                {
                    FarmToast("Too fast: keep both machines slow to unload on the move");
                    return;
                }
                if (driver.RideModel is Truck { TrailerTank: var held } && held.Items > 0 && held.Crop != truck.Tank.Crop)
                {
                    FarmToast($"That trailer holds {held.Crop}");
                    return;
                }
                if (PassengerService.Instance is not { } passengers) return;
                _augerOffered = Mathf.Min(AugerBatch, truck.Tank.Items);
                _augerWait = 5f;
                passengers.OfferAuger(driver.Name, (int)truck.Tank.Crop, _augerOffered);
                return;
            }
            FarmToast("Bring a tipping trailer under the auger to unload");
            return;
        }
        var into = TrailerCatalog.TankOf(target.Trailer!.Code);
        if (into.Items > 0 && into.Crop != truck.Tank.Crop) { FarmToast($"That trailer holds {into.Crop}"); return; }
        if (into.Items >= target.Trailer.Spec.TankItems) { FarmToast("The trailer is full"); return; }
        vehicles.Claim(target, state =>
        {
            int cap = TrailerCatalog.For(state.Train)?.TankItems ?? 0;
            if (_ride is not Truck t || cap <= 0) { Vehicles?.Park(state); return; }
            var batch = t.Tank with { Items = Mathf.Min(AugerBatch, t.Tank.Items), Partial = 0f };
            var (_, to, moved) = MachineLoad.Transfer(batch, TrailerCatalog.TankOf(state.Train), cap);
            Vehicles?.Park(state with { Train = TrailerCatalog.WithTank(state.Train, to) });
            if (moved > 0) t.SetTank(MachineLoad.Take(t.Tank, moved, out _));
        });
    }

    /// <summary>
    /// Another player's driven train whose tipping trailer has <paramref name="point"/> over its bin
    /// (its sections as drawn on this peer), or null.
    /// </summary>
    private FootPlayer? DrivenTipperAt(Vector3 point, float margin)
    {
        foreach (var node in GetTree().GetNodesInGroup(Group))
            if (node is FootPlayer { Npc: false } p && p != this && p.SeatIndex == 0 && p.TipperBinHas(point, margin)) return p;
        return null;
    }

    /// <summary>The tipping trailer's bin section as drawn on this peer (its node's frame: on the ground under its centre of mass, facing −Z), or null.</summary>
    public Transform3D? TipperBinFrame()
    {
        if (RideModel is not Truck { Trailer.TankItems: > 0 } t) return null;
        int k = t.SectionCount - 1;
        return k >= 1 && _sections.Count >= k ? _sections[k - 1].GlobalTransform : null;
    }

    /// <summary>Whether <paramref name="point"/> is over the bin of the tipping trailer this player tows (within <paramref name="margin"/> m).</summary>
    public bool TipperBinHas(Vector3 point, float margin)
    {
        if (RideModel is not Truck { Spec.TankItems: 0, Trailer.TankItems: > 0 } t) return false;
        int k = t.SectionCount - 1;
        if (k < 1 || _sections.Count < k) return false;
        var body = t.Train.Bodies[k];
        var local = _sections[k - 1].GlobalTransform.AffineInverse() * point;
        float hw = body.Spec.Width * 0.5f + margin;
        return Mathf.Abs(local.X) <= hw && local.Z >= -body.CgAt - margin && local.Z <= body.Spec.Length - body.CgAt + margin
            && local.Y > -1f && local.Y < 5f;
    }

    /// <summary>
    /// The driver of a tipping trailer, on its own peer: <paramref name="from"/>'s combine offers
    /// <paramref name="count"/> sacks of <paramref name="crop"/>. Taken if its auger is out over
    /// this bin (as this peer sees it), both are slow and it fits; returns how many were taken.
    /// </summary>
    public int AugerArrived(FootPlayer? from, CropKind crop, int count)
    {
        if (Npc || SeatIndex != 0 || !IsMultiplayerAuthority() || _ride is not Truck { Spec.TankItems: 0 } t || t.TrailerCapacity <= 0) return 0;
        if (from?.RideModel is not Truck { Spec.TankItems: > 0, AugerOut: true } combine) return 0;
        if (GroundSpeed > AugerDrivenSpeed + 0.5f || t.Tipping) return 0;
        // the combine's copy lags its owner a little: a looser margin than the owner's own test
        var spout = from.ToGlobal(Avatar.FarmMeshBuilder.AugerSpout(combine.Train.Bodies[0].CgAt));
        if (!TipperBinHas(spout, 1.5f)) return 0;
        var (_, to, moved) = MachineLoad.Transfer(new Tank(crop, Mathf.Clamp(count, 0, AugerBatch)), t.TrailerTank, t.TrailerCapacity);
        if (moved <= 0) return 0;
        if (t.SetTrailerTank(to))
        {
            TrailerCode = t.TrailerCode;
            RefreshVisual(force: true);
        }
        FarmSacksAugered += moved;
        return moved;
    }

    /// <summary>The combine's owner: the trailer's driver took <paramref name="moved"/> of the sacks offered; they leave the tank.</summary>
    public void AugerTaken(int moved)
    {
        int offered = _augerOffered;
        _augerOffered = 0;
        _augerWait = 0f;
        if (moved <= 0 || _ride is not Truck { Spec.TankItems: > 0 } t) return;
        t.SetTank(MachineLoad.Take(t.Tank, Mathf.Min(moved, offered), out int taken));
        FarmSacksAugered += taken;
    }

    /// <summary>Sacks moved by the auger into or out of a driven trailer on this peer (for the checks).</summary>
    public int FarmSacksAugered { get; private set; }

    /// <summary>
    /// A parked tipping trailer whose body has <paramref name="point"/> over it (within
    /// <paramref name="margin"/> m of its sides and ends), or null.
    /// </summary>
    private VehicleBody? TipperAt(Vector3 point, float margin)
    {
        if (Vehicles == null) return null;
        foreach (var node in Vehicles.GetChildren())
        {
            if (node is not VehicleBody { Wrecked: false, Trailer: { Spec.TankItems: > 0 } t } v) continue;
            int k = t.Spec.Sections.Length - 1;
            var body = t.Bodies[k];
            var local = (v.GlobalTransform * t.NodeLocal(k)).AffineInverse() * point;
            float hw = body.Spec.Width * 0.5f + margin;
            if (Mathf.Abs(local.X) <= hw && local.Z >= -body.CgAt - margin && local.Z <= body.Spec.Length - body.CgAt + margin
                && local.Y > -1f && local.Y < 5f)
                return v;
        }
        return null;
    }

    /// <summary>A parked combine with grain in its tank, the walker standing at the tank's left side, or null.</summary>
    private VehicleBody? CombineTankAt(Vector3 point)
    {
        if (Vehicles == null) return null;
        foreach (var node in Vehicles.GetChildren())
        {
            if (node is not VehicleBody { Wrecked: false, Ride: Truck { Spec.TankItems: > 0 } t } v || t.Tank.Items <= 0) continue;
            float cg = t.Train.Bodies[0].CgAt;
            var spot = v.ToGlobal(new Vector3(-(t.Spec.Sections[0].Width * 0.5f + 0.6f), 0f, -(cg - 5.8f)));
            if (new Vector2(spot.X - point.X, spot.Z - point.Z).Length() < SackReach && Mathf.Abs(spot.Y - point.Y) < 2f) return v;
        }
        return null;
    }

    // ---- on foot ----------------------------------------------------------------------------------

    private VehicleBody? _sackSource;
    private float _sackScan, _sackHeld = -1f;

    /// <summary>A loaded tank or trailer at hand (looked for four times a second), or null: what E takes a sack from.</summary>
    public VehicleBody? SackSource => _sackSource is { } v && IsInstanceValid(v) ? v : null;

    /// <summary>Per physics tick on foot: the tank at hand, and a held E taking the rest of ten.</summary>
    private void TickFarmFoot(float dt)
    {
        _farmToastCooldown = Mathf.Max(0f, _farmToastCooldown - dt);
        if (_deliverWait > 0f) _deliverWait = Mathf.Max(0f, _deliverWait - dt);
        if (_ride != null || Npc) { _sackSource = null; _sackHeld = -1f; return; }
        if ((_sackScan -= dt) <= 0f)
        {
            _sackScan = 0.25f;
            _sackSource = TipperAt(GlobalPosition, SackReach) ?? CombineTankAt(GlobalPosition);
        }
        if (_sackHeld < 0f) return;
        if (!PlayerInput.Held(PlayerInput.InteractMount)) { _sackHeld = -1f; return; }
        if ((_sackHeld += dt) >= 0.6f)
        {
            _sackHeld = -1f;
            if (SackSource is { } v) TakeSacks(v, 9);
        }
    }

    /// <summary>E at a loaded tank or trailer: one sack (ten with {sprint}; held, ten in all). False when there is none at hand.</summary>
    private bool TryFarmTank()
    {
        if (SackSource is not { } v || Vehicles is not { } vehicles) return false;
        if (vehicles.Claiming) return true;
        bool ten = PlayerInput.Held(PlayerInput.Sprint);
        TakeSacks(v, ten ? 10 : 1);
        _sackHeld = ten ? -1f : 0f;
        return true;
    }

    /// <summary>Takes up to <paramref name="n"/> sacks from a parked tank or trailer into the pack (claimed and parked back with the rest).</summary>
    private void TakeSacks(VehicleBody v, int n)
    {
        if (Vehicles is not { } vehicles || vehicles.Claiming || !IsInstanceValid(v)) return;
        vehicles.Claim(v, state =>
        {
            int taken;
            Tank tank;
            if (state.Kind == RideKind.Trailer)
            {
                tank = MachineLoad.Take(TrailerCatalog.TankOf(state.Train), n, out taken);
                var crop = TrailerCatalog.TankOf(state.Train).Crop;
                Vehicles?.Park(state with { Train = TrailerCatalog.WithTank(state.Train, tank) });
                GiveHarvest(crop, taken);
                return;
            }
            var spec = HeavyCatalog.For(state.Kind);
            var held = MachineLoad.TankFromFlags(state.Flags);
            tank = MachineLoad.Take(held, n, out taken);
            int flags = (state.Flags & ~MachineLoad.FlagMask) | MachineLoad.TankFlags(tank);
            float load = spec is { TankItems: > 0 } ? tank.Items / (float)spec.TankItems : state.Load;
            Vehicles?.Park(state with { Flags = flags, Load = load });
            GiveHarvest(held.Crop, taken);
        });
    }

    private void GiveHarvest(CropKind crop, int count)
    {
        var item = FarmTables.YieldOf(crop);
        if (count <= 0 || item == ItemId.None) return;
        FarmSacksTaken += count;
        ItemController.Instance?.Give(new ItemStack(item, count));
        Announced?.Invoke($"+{count} {ItemDefs.Get(item)?.Name ?? item.ToString()}", true);
    }
}

/// <summary>
/// The farm machines' door to <see cref="FarmWork.Sweep"/> (#494), with a stand-in the checks
/// set (<see cref="FakeSweep"/>) while the field side is not there to answer.
/// </summary>
public static class MachineWork
{
    /// <summary>A check's field: answers in place of <see cref="FarmWork.Sweep"/> when set.</summary>
    public static System.Func<FarmTool, Vector3, Vector3, float, CropKind, FarmStroke>? FakeSweep { get; set; }

    public static FarmStroke Sweep(FarmTool tool, Vector3 a, Vector3 b, float width, CropKind seed) =>
        FakeSweep?.Invoke(tool, a, b, width, seed) ?? FarmWork.Sweep(tool, a, b, width, seed);
}
