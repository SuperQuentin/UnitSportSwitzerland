using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// Server-side object container streaming (#689), the Star Citizen idea: a vehicle or a dropped
/// item nobody has been near for a while is written into its tile's container and freed, and comes
/// back exactly as it was when a player returns, and after a restart. Before, the server deleted
/// them (3 km away for 5 or 15 minutes) and nothing outlived the session.
///
/// <para>
/// The bookkeeping is <see cref="ContainerBook"/> (pure, crash-tested in tier 0): every entity has
/// a stable oid and is live or filed, exactly once. This node only feeds it: who is where, what is
/// still, which tiles to wake, and turns its records back into spawns. At
/// <c>user://containers/server</c>, or <c>--containers-dir</c>; switchable as
/// <see cref="Systems.Containers"/>, and off on a fixture world unless a directory is given, so a
/// check never finds the last run's cars.
/// </para>
/// </summary>
public partial class ObjectContainers : Node
{
    public const string NodeName = "Containers";

    /// <summary>How often players and entities are looked at, s.</summary>
    private double _period = 5;

    /// <summary>How often what is live is written down, s: the most a hard crash can lose of how things moved.</summary>
    private double _checkpointPeriod = 60;

    public static ObjectContainers? Instance { get; private set; }

    private readonly ContainerBook _book;
    private readonly VehicleManager _vehicles;
    private readonly DroppedItems _items;
    private readonly RadioManager? _radios;
    private readonly Node _players;
    private readonly string _dir;

    /// <summary>Per live entity: seconds with nobody near, seconds standing still, and where it was last look.</summary>
    private readonly Dictionary<ulong, (double Lonely, double Still, GlobalPos At)> _watch = new();
    /// <summary>Nodes being freed because they were filed: their leaving is not a removal.</summary>
    private static readonly HashSet<ulong> Filing = new();
    private readonly List<TileId> _playerTiles = new();
    private readonly HashSet<string> _carriers = new();
    private double _sinceLook, _sinceCheckpoint;

    private ObjectContainers(string dir, VehicleManager vehicles, DroppedItems items, RadioManager? radios, Node players)
    {
        Name = NodeName;
        _dir = dir;
        _book = new ContainerBook(new ContainerFileDisk(dir));
        _vehicles = vehicles;
        _items = items;
        _radios = radios;
        _players = players;
    }

    /// <summary>Server: the containers, when this run keeps them (see the class summary), else null.</summary>
    public static ObjectContainers? CreateServer(Node world, VehicleManager vehicles, DroppedItems items, RadioManager? radios, Node players)
    {
        if (!Systems.On(Systems.Containers)) return null;
        string? dir = CmdArgs.Value("--containers-dir");
        if (dir == null && Systems.FixtureCourse != null) return null;
        var containers = new ObjectContainers(ProjectSettings.GlobalizePath(dir ?? "user://containers/server"), vehicles, items, radios, players);
        world.AddChild(containers);
        return containers;
    }

    /// <summary>A vehicle whose node name this is lies asleep in a container (a dormant slot's, #499, stays awake meanwhile).</summary>
    public bool IsFiled(string name) => _book.IsFiled(name);

    /// <summary>Every name asleep in a container now.</summary>
    public IEnumerable<string> FiledNames => _book.FiledNamesList;

    /// <summary>This node is leaving the world into a container, not for good: a woken slot is still awake, a boat is not gone.</summary>
    public static bool IsFiling(Node node) => Filing.Contains(node.GetInstanceId());

    public override void _Ready()
    {
        Instance = this;
        // a check (tools/containernetcheck.sh) cannot wait minutes: everything a dozen times faster
        if (CmdArgs.Has("--containers-quick"))
        {
            _period = 1;
            _checkpointPeriod = 5;
            ContainerRules.SleepAfter = 5;
            ContainerRules.StillFor = 2;
        }
        int filed;
        try { filed = _book.Load(Now); }
        catch (Exception e)
        {
            GD.PushError($"[containers] could not read {_dir}: {e.Message}; starting empty");
            filed = 0;
        }
        GD.Print($"[containers] {filed} asleep in {_book.FiledTiles.Count} tiles ({_dir})");

        _vehicles.Keeps = v => v.Oid != 0 && !v.Name.ToString().StartsWith(BattleRoyale.BrLoot.VehiclePrefix, StringComparison.Ordinal);
        _vehicles.Filed = _book.IsFiled;
        _vehicles.Removing = v => Removed(v.Oid, v.Name);
        _items.Keeps = i => i.Oid != 0 && !i.Proxy;
        _items.Removing = i => Removed(i.Oid, i.Name);
        _vehicles.ChildEnteredTree += OnEntered;
        _vehicles.ChildExitingTree += OnExiting;
        _items.ChildEnteredTree += OnEntered;
        _items.ChildExitingTree += OnExiting;
        if (_radios != null)
        {
            _radios.Keeps = r => r.Oid != 0;
            _radios.Removing = r => Removed(r.Oid, r.Name);
            _radios.ChildEnteredTree += OnEntered;
            _radios.ChildExitingTree += OnExiting;
            foreach (var child in _radios.GetChildren()) OnEntered(child);
        }
        foreach (var child in _vehicles.GetChildren()) OnEntered(child);
        foreach (var child in _items.GetChildren()) OnEntered(child);
        DormantVehicles.Instance?.RestoreAwake(LoadAwake(), _book.FiledNamesList);
    }

    // ---- the dormant slots woken (#499), kept across restarts --------------------------------

    private string AwakePath => System.IO.Path.Combine(_dir, "awake.json");

    /// <summary>The woken slots the last run left (<c>owner|ordinal</c> → when, and where its car stood), or none.</summary>
    public Dictionary<string, AwakeSlot> LoadAwake()
    {
        try
        {
            return System.IO.File.Exists(AwakePath)
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AwakeSlot>>(System.IO.File.ReadAllText(AwakePath)) ?? new()
                : new();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[containers] {AwakePath} does not read: {e.Message}");
            return new();
        }
    }

    /// <summary>
    /// Writes the woken slots: a car park's woken car outlives the session now, so its bay must
    /// not be woken again after a restart (two of the same car). Small, written whole, at once.
    /// </summary>
    public void SaveAwake(IReadOnlyDictionary<string, AwakeSlot> awake)
    {
        try { SaveQueue.WriteAtomic(AwakePath, System.Text.Json.JsonSerializer.Serialize(awake)); }
        catch (Exception e) { GD.PushError($"[containers] could not write {AwakePath}: {e.Message}"); }
    }

    /// <summary>
    /// The server stops: everything that may sleep goes into its container now, whoever is near,
    /// so a restart brings it all back. A claim in flight or a fall still going stays out (a crash
    /// would lose it the same way).
    /// </summary>
    public override void _ExitTree()
    {
        _vehicles.ChildEnteredTree -= OnEntered;
        _items.ChildEnteredTree -= OnEntered;
        if (_radios != null) _radios.ChildEnteredTree -= OnEntered;
        try
        {
            int filed = SleepWhere(force: true);
            GD.Print($"[containers] shutting down: {filed} put to sleep, {_book.LiveCount} left live");
        }
        catch (Exception e) { GD.PushError($"[containers] shutdown save failed: {e.Message}"); }
        _vehicles.ChildExitingTree -= OnExiting;
        _items.ChildExitingTree -= OnExiting;
        _vehicles.Keeps = null;
        _vehicles.Filed = null;
        _vehicles.Removing = null;
        _items.Keeps = null;
        _items.Removing = null;
        if (_radios != null)
        {
            _radios.ChildExitingTree -= OnExiting;
            _radios.Keeps = null;
            _radios.Removing = null;
        }
        if (Instance == this) Instance = null;
    }

    /// <summary>The server's wall clock, Unix seconds (<see cref="VehicleState.Now"/>): container stamps outlive the process.</summary>
    private static double Now => VehicleState.Now;

    // ---- the book follows the world ----------------------------------------------------------

    private void OnEntered(Node node)
    {
        long oid = node switch
        {
            VehicleBody v when _vehicles.Keeps?.Invoke(v) == true => v.Oid,
            DroppedItem i when _items.Keeps?.Invoke(i) == true => i.Oid,
            RadioBody r when _radios?.Keeps?.Invoke(r) == true => r.Oid,
            _ => 0,
        };
        if (oid == 0) return;
        // a woken one is already live in the book: only its name may have changed
        if (_book.IsLive(oid)) { _book.Renamed(oid, node.Name); return; }
        // a new one is written down once it is ready: its position is set up in its _Ready
        Callable.From(() =>
        {
            if (!IsInstanceValid(node) || !node.IsInsideTree() || Record(node) is not { } rec) return;
            try { _book.Track(rec); }
            catch (Exception e) { GD.PushError($"[containers] could not write the checkpoint: {e.Message}"); }
        }).CallDeferred();
    }

    private void OnExiting(Node node)
    {
        ulong id = node.GetInstanceId();
        _watch.Remove(id);
        if (Filing.Remove(id)) return;
        long oid = OidOf(node);
        if (oid != 0) Removed(oid, node.Name);
    }

    private void Removed(long oid, string name)
    {
        try { _book.Untrack(oid, name); }
        catch (Exception e) { GD.PushError($"[containers] could not write the checkpoint: {e.Message}"); }
    }

    /// <summary>What the book keeps of a node: null for anything it does not persist.</summary>
    private ContainerRecord? Record(Node node)
    {
        switch (node)
        {
            case VehicleBody v when _vehicles.Keeps?.Invoke(v) == true:
            {
                var state = v.Capture() with { Velocity = Vector3.Zero, SpawnedAt = 0 };
                return new ContainerRecord(v.Oid, 0, ContainerRecord.Vehicle, v.Name, state.Position.E, state.Position.N, Now,
                    GD.VarToStr(state.ToDict()));
            }
            case DroppedItem i when _items.Keeps?.Invoke(i) == true:
            {
                var state = i.Capture();
                return new ContainerRecord(i.Oid, 0, ContainerRecord.Item, i.Name, state.Position.E, state.Position.N, Now,
                    GD.VarToStr(state.ToDict()));
            }
            case RadioBody r when _radios?.Keeps?.Invoke(r) == true:
            {
                // asleep silent: nobody is there to hear it, and the clock it played by is this run's
                var state = r.Capture() with { Playing = false, StartedAt = 0 };
                return new ContainerRecord(r.Oid, 0, ContainerRecord.Radio, r.Name, state.Position.E, state.Position.N, Now,
                    GD.VarToStr(state.ToDict()));
            }
            default:
                return null;
        }
    }

    // ---- sleeping and waking -----------------------------------------------------------------

    public override void _Process(double delta)
    {
        _sinceLook += delta;
        if (_sinceLook < _period) return;
        double step = _sinceLook;
        _sinceLook = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            PlayerTiles();
            Wake();
            Watch(step);
            SleepWhere(force: false);
            _sinceCheckpoint += step;
            if (_sinceCheckpoint >= _checkpointPeriod)
            {
                _sinceCheckpoint = 0;
                Checkpoint();
            }
        }
        catch (Exception e) { GD.PushError($"[containers] {e.Message}"); }
        Net.ServerStats.Ran("object containers", t0);
    }

    private void PlayerTiles()
    {
        _playerTiles.Clear();
        foreach (var child in _players.GetChildren())
            if (child is FootPlayer p && FootPlayer.NetId(p.Name) is > 0)
            {
                var at = p.Global;
                var tile = TileId.FromLv95(at.E, at.N);
                if (!_playerTiles.Contains(tile)) _playerTiles.Add(tile);
            }
    }

    private void Wake()
    {
        foreach (var id in ContainerRules.ToWake(_playerTiles, _book.HasFile))
        {
            var records = _book.Wake(id, Now);
            int back = 0;
            foreach (var rec in records)
                if (Spawn(rec)) back++;
            GD.Print($"[containers] tile {id} woke: {back} of {records.Count} back");
        }
    }

    /// <summary>A woken record back in the world, its oid kept; a record that does not read is dropped (and said).</summary>
    private bool Spawn(ContainerRecord rec)
    {
        try
        {
            var dict = GD.StrToVar(rec.Data).AsGodotDictionary();
            string? name = rec.Kind == ContainerRecord.Vehicle
                ? _vehicles.Restore(VehicleState.FromDict(dict) with { Oid = rec.Oid })
                : rec.Kind == ContainerRecord.Item ? _items.Restore(DropState.FromDict(dict) with { Oid = rec.Oid })
                : _radios?.Restore(RadioState.FromDict(dict) with { Oid = rec.Oid });
            if (name != null) return true;
        }
        catch (Exception e) { GD.PushWarning($"[containers] {rec.Name} does not read: {e.Message}"); }
        // not spawned: it must not stay live in the book either
        _book.Untrack(rec.Oid, rec.Name);
        return false;
    }

    /// <summary>Who has been alone, and still, for how long.</summary>
    private void Watch(double step)
    {
        foreach (var node in Entities())
        {
            ulong id = node.GetInstanceId();
            var at = ((Net.IInterestEntity)node).InterestAt;
            var tile = TileId.FromLv95(at.E, at.N);
            if (!_watch.TryGetValue(id, out var w)) w = (0, 0, at);
            double lonely = ContainerRules.Lonely(w.Lonely, step, tile, _playerTiles);
            double still = ContainerRules.Still(w.Still, step, at.DistanceTo(w.At));
            _watch[id] = (lonely, still, at);
        }
    }

    private IEnumerable<Node> Entities()
    {
        foreach (var child in _vehicles.GetChildren())
            if (child is VehicleBody v && !v.IsQueuedForDeletion() && _vehicles.Keeps?.Invoke(v) == true) yield return v;
        foreach (var child in _items.GetChildren())
            if (child is DroppedItem i && !i.IsQueuedForDeletion() && _items.Keeps?.Invoke(i) == true) yield return i;
        if (_radios != null)
            foreach (var child in _radios.GetChildren())
                if (child is RadioBody r && !r.IsQueuedForDeletion() && _radios.Keeps?.Invoke(r) == true) yield return r;
    }

    /// <summary>
    /// Puts to sleep everything that may: alone and still long enough, or everything not busy when
    /// <paramref name="force"/> (the server stopping). Filed first, freed after. Returns how many.
    /// </summary>
    private int SleepWhere(bool force)
    {
        // a vehicle carrying another in its hold keeps it: neither sleeps (#418)
        _carriers.Clear();
        foreach (var child in _vehicles.GetChildren())
            if (child is VehicleBody { InHold: true } held) _carriers.Add(held.Carrier);

        var nodes = new List<Node>();
        var records = new List<ContainerRecord>();
        foreach (var node in Entities())
        {
            bool busy = node switch
            {
                VehicleBody v => v.Wrecked || v.InHold || _vehicles.IsClaimed(v.Name) || _carriers.Contains("v:" + v.Name),
                DroppedItem i => !i.Settled || _items.IsClaimedOnServer(i.Name),
                // one still playing finishes its CD first (force, at shutdown, files it silent)
                RadioBody r => !r.Settled || _radios!.IsClaimedOnServer(r.Name) || r.Playing && !force,
                _ => true,
            };
            if (busy) continue;
            if (!force)
            {
                if (!_watch.TryGetValue(node.GetInstanceId(), out var w) || !ContainerRules.MaySleep(w.Lonely, w.Still, busy)) continue;
            }
            // a woken car back in its own bay becomes the slot's scenery again: no container, no node
            // (its leaving takes it off the book, OnExiting)
            if (node is VehicleBody bay && DormantVehicles.Instance?.TryResleep(bay) == true)
            {
                bay.QueueFree();
                continue;
            }
            if (Record(node) is not { } rec) continue;
            nodes.Add(node);
            records.Add(rec);
        }
        if (records.Count == 0) return 0;
        var filed = _book.Sleep(records, Now);
        foreach (var node in nodes)
        {
            long oid = OidOf(node);
            if (!filed.Contains(oid)) continue;
            Filing.Add(node.GetInstanceId());
            node.QueueFree();
        }
        if (!force) GD.Print($"[containers] {filed.Count} put to sleep; {_book.LiveCount} live, {_book.FiledCount} asleep");
        return filed.Count;
    }

    private static long OidOf(Node node) => node switch
    {
        VehicleBody v => v.Oid,
        DroppedItem i => i.Oid,
        RadioBody r => r.Oid,
        _ => 0,
    };

    /// <summary>Writes where everything live stands now.</summary>
    private void Checkpoint()
    {
        foreach (var node in Entities())
            if (Record(node) is { } rec) _book.Update(rec);
        _book.FlushCheckpoint();
    }
}
