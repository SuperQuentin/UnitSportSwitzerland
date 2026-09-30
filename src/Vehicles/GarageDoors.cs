using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// The roll-up door of every garage (<see cref="BuildingKind.Garage"/>) and the lit bay behind it.
/// Client only, no RPC: a door opens while any car (the local player's or a remote one) stands in
/// front of it, which every peer derives from positions it already has, so they all agree.
///
/// <para>
/// The facade draws the frame, step and neon sign (<see cref="BuildingMeshBuilder"/>); this adds a
/// node per garage door, <b>parented to the tile's own node</b> under a deterministic name, so it
/// unloads with the tile. The bay is a few quads just in front of the wall, under the door: the
/// building has no hole, the open door shows a tool wall and a neon strip instead of an interior.
/// </para>
/// </summary>
public partial class GarageDoors : Node
{
    /// <summary>A car this close to the doorway, in front of it, opens the door.</summary>
    public const float OpenReach = 10f;
    /// <summary>Seconds for a full roll up or down.</summary>
    private const float RollSeconds = 1f;
    /// <summary>How often the car positions are checked; the roll itself animates every frame.</summary>
    private const double CheckEvery = 0.2;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Dictionary<TileId, List<Door>> _tiles = new();
    private readonly List<Vector3> _cars = new();
    private ShaderMaterial _material = null!;
    private double _sinceCheck = CheckEvery;

    private sealed class Door
    {
        public required Node3D Root;
        public required Node3D Leaf;
        public required Vector3 World;
        public required Vector3 Outward;
        public required float HalfWidth;
        public float Open;   // 0 shut .. 1 rolled up
        public bool Wanted;
    }

    public GarageDoors(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "GarageDoors";
        _chunks = chunks;
        _origin = origin;
    }

    public GarageDoors() : this(null!, null!) { }

    public override void _Ready()
    {
        // the building shader, so the doors get the same snap, dither, sun and neon as the facade
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ps1_building.gdshader") };
        FogUniforms.Apply(_material);
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnloaded += OnUnloaded;
        _chunks.TerrainReplaced += OnReplaced;
    }

    public override void _ExitTree()
    {
        if (_chunks == null) return;
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnloaded -= OnUnloaded;
        _chunks.TerrainReplaced -= OnReplaced;
    }

    // ---- tile lifecycle ------------------------------------------------------------------------

    private void OnFurnished(TileId id, ChunkNode node, DoorSpot[] doors)
    {
        // a tile rebuilt at another stride commits the same doors again: keep what is there
        var list = new List<Door>();
        var tileOrigin = _origin.ToWorld(id.MinE, id.MaxN, 0);
        foreach (var d in doors)
        {
            if (d.Width <= 0 || d.Kind != BuildingKind.Garage) continue;
            string name = $"GarageDoor_{d.Index}";
            if (node.GetNodeOrNull<Node3D>(name) is not { } root)
            {
                root = Build(d);
                root.Name = name;
                node.AddChild(root);
            }
            var old = _tiles.GetValueOrDefault(id)?.Find(x => x.Root == root);
            list.Add(old ?? new Door
            {
                Root = root,
                Leaf = root.GetNode<Node3D>("Leaf"),
                World = tileOrigin + d.Position,
                Outward = d.Outward,
                HalfWidth = d.Width / 2,
            });
        }
        if (list.Count > 0) _tiles[id] = list;
        else _tiles.Remove(id);
    }

    private void OnUnloaded(TileId id) => _tiles.Remove(id);   // the nodes went with the tile's node

    private void OnReplaced(Func<TileId, bool>? affected)
    {
        if (affected == null) { _tiles.Clear(); return; }
        foreach (var id in _tiles.Keys.Where(affected).ToList()) _tiles.Remove(id);
    }

    // ---- opening ---------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_tiles.Count == 0) return;

        _sinceCheck += delta;
        if (_sinceCheck >= CheckEvery)
        {
            _sinceCheck = 0;
            _cars.Clear();
            foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
                if (n is FootPlayer p && CarCatalog.IsCar(p.Ride)) _cars.Add(p.GlobalPosition);
            foreach (var doors in _tiles.Values)
                foreach (var d in doors) d.Wanted = CarInFront(d);
        }

        float step = (float)delta / RollSeconds;
        foreach (var doors in _tiles.Values)
            foreach (var d in doors)
            {
                float target = d.Wanted ? 1f : 0f;
                if (d.Open == target) continue;
                d.Open = Mathf.MoveToward(d.Open, target, step);
                if (!IsInstanceValid(d.Leaf)) continue;
                // rolls into the lintel: squash from the top, never quite to nothing
                d.Leaf.Scale = new Vector3(1, Mathf.Max(0.03f, 1 - d.Open), 1);
            }
    }

    private bool CarInFront(Door d)
    {
        foreach (var c in _cars)
        {
            var rel = c - d.World;
            if (Mathf.Abs(rel.Y) > 4f) continue;
            float outward = rel.X * d.Outward.X + rel.Z * d.Outward.Z;
            if (outward < -1f) continue;   // behind the wall: not this garage's business
            float along = Mathf.Abs(rel.X * -d.Outward.Z + rel.Z * d.Outward.X);
            if (new Vector2(Mathf.Max(0, along - d.HalfWidth), outward).Length() <= OpenReach) return true;
        }
        return false;
    }

    // ---- meshes ----------------------------------------------------------------------------------

    /// <summary>Root in the door's frame: X along the wall, Y up, Z out of the wall.</summary>
    private Node3D Build(DoorSpot d)
    {
        var o = d.Outward;
        var root = new Node3D
        {
            Position = d.Position,
            Basis = new Basis(new Vector3(-o.Z, 0, o.X), Vector3.Up, o),
        };
        float hw = d.Width / 2, h = d.Height;

        var bay = new Quads();
        var back = new Color(0.72f, 0.74f, 0.78f); // colours are sRGB, linearised like the facade's
        bay.Rect(-hw, hw, 0, h, 0.015f, back);
        // tool wall: a pegboard with a few spanners and a red roll cab under it
        bay.Rect(-hw * 0.85f, hw * 0.15f, 0.95f, 1.95f, 0.02f, new Color(0.80f, 0.66f, 0.46f));
        for (int i = 0; i < 5; i++)
        {
            float x = -hw * 0.75f + i * hw * 0.18f;
            bay.Rect(x, x + 0.05f, 1.2f + (i % 2) * 0.1f, 1.7f, 0.025f, new Color(0.92f, 0.93f, 0.95f));
        }
        bay.Rect(hw * 0.3f, hw * 0.85f, 0, 0.95f, 0.02f, new Color(0.88f, 0.16f, 0.12f));
        for (int i = 1; i < 4; i++)
            bay.Rect(hw * 0.33f, hw * 0.82f, i * 0.23f, i * 0.23f + 0.03f, 0.025f, new Color(0.45f, 0.06f, 0.05f));
        // a jack-stand stripe on the floor edge and the neon tube along the top
        bay.Rect(-hw, hw, 0, 0.08f, 0.02f, new Color(0.85f, 0.70f, 0.05f));
        bay.Rect(-hw * 0.9f, hw * 0.9f, h - 0.3f, h - 0.2f, 0.025f, Colors.White, BuildingMeshBuilder.NeonFlag);
        root.AddChild(new MeshInstance3D { Name = "Bay", Mesh = bay.ToMesh(_material) });

        // the slatted leaf, hanging from the lintel so squashing it rolls it up
        var leaf = new Quads();
        var metal = new Color(0.80f, 0.82f, 0.85f);
        int slats = Mathf.Max(4, Mathf.RoundToInt(h / 0.22f));
        float sh = h / slats;
        for (int i = 0; i < slats; i++)
        {
            float top = -i * sh, mid = top - sh * 0.35f, bottom = top - sh;
            // each slat is a shallow ridge: a lit upper face and a shaded lower one
            leaf.Quad(new(-hw, mid, 0.07f), new(hw, mid, 0.07f), new(hw, top, 0.045f), new(-hw, top, 0.045f), metal);
            leaf.Quad(new(-hw, bottom, 0.045f), new(hw, bottom, 0.045f), new(hw, mid, 0.07f), new(-hw, mid, 0.07f), metal * 0.8f);
        }
        leaf.Rect(-hw, hw, -h, -h + 0.06f, 0.075f, new Color(0.2f, 0.2f, 0.22f)); // bottom rail
        root.AddChild(new MeshInstance3D { Name = "Leaf", Position = new Vector3(0, h, 0), Mesh = leaf.ToMesh(_material) });
        return root;
    }

    /// <summary>Double-sided quads with the facade's vertex layout (no windows, a UV2.y flag).</summary>
    private sealed class Quads
    {
        private readonly List<Vector3> _v = new();
        private readonly List<Color> _c = new();
        private readonly List<Vector2> _uv2 = new();

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, float flag = 0f)
        {
            // both windings: the building shader culls back faces
            _v.AddRange([a, b, c, a, c, d, a, c, b, a, d, c]);
            var lin = col.SrgbToLinear();
            for (int i = 0; i < 12; i++) { _c.Add(lin); _uv2.Add(new Vector2(0, flag)); }
        }

        public void Rect(float x0, float x1, float y0, float y1, float z, Color col, float flag = 0f) =>
            Quad(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z), col, flag);

        public ArrayMesh ToMesh(Material material)
        {
            var uvs = new Vector2[_v.Count];
            Array.Fill(uvs, new Vector2(0, -1)); // no window grid
            return ChunkNode.ToArrayMesh(
                new BuildingMeshBuilder.MeshData(_v.ToArray(), _c.ToArray(), uvs, _uv2.ToArray(), new float[_v.Count * 4]), material);
        }
    }
}
