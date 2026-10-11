using Godot;

namespace UnitSport.Movie;

/// <summary>
/// The world systems the replay records (#775). A manager whose children are world objects (cars,
/// birds, dropped items: usually an <c>IOriginContainer</c>) joins with one line,
/// <c>ReplayWatch.Container(this)</c>, and every mesh under each child is recorded as it is drawn. A
/// lone object that lives somewhere else (a door leaf in a building) joins with
/// <c>ReplayWatch.Object(this)</c>. Nodes leave by themselves when they leave the tree.
/// <para>
/// The studio hides every watched node while it is open (<see cref="HideLive"/>): what it shows of the
/// world is the recording, not the world going on.
/// </para>
/// </summary>
public static class ReplayWatch
{
    /// <summary>Containers: each child is one recorded object.</summary>
    public static readonly List<Node3D> Containers = new();
    /// <summary>Lone objects, each recorded as itself.</summary>
    public static readonly List<Node3D> Objects = new();
    private static readonly Dictionary<ulong, bool> Hidden = new();

    public static void Container(Node3D container) => Join(Containers, container);
    public static void Object(Node3D obj) => Join(Objects, obj);

    private static readonly StringName Version = "replay_version";

    /// <summary>
    /// An instancer recorded as it is drawn (#782), a lone object of its own whose look is a copy of
    /// its instances: the dormant fleet's car parks, a hall's parked cars. Called when it is made and
    /// again every time its instances change (a parked car undrawn as it wakes), which is what tells
    /// the recorder to copy them again. One the game sets every frame (critters, tracers) never calls
    /// this and is not recorded: that would be a copy a frame.
    /// </summary>
    public static void Instanced(MultiMeshInstance3D node)
    {
        if (node.Multimesh is not { } mm) return;
        int version = InstancedVersion(mm);
        mm.SetMeta(Version, version + 1);
        if (version > 0) return;
        // joined each time it enters the tree, as a door leaf joins in its _EnterTree
        node.TreeEntered += () => Object(node);
        if (node.IsInsideTree()) Object(node);
    }

    /// <summary>How many times <see cref="Instanced"/> was called on <paramref name="mm"/>'s instancer: 0, not recorded.</summary>
    public static int InstancedVersion(MultiMesh mm) => mm.GetMeta(Version, 0).AsInt32();

    private static void Join(List<Node3D> list, Node3D node)
    {
        if (list.Contains(node)) return;
        list.Add(node);
        node.TreeExiting += () => list.Remove(node);
    }

    /// <summary>The live world's watched objects out of sight, for the studio (#775). Undone by <see cref="ShowLive"/>.</summary>
    public static void HideLive()
    {
        foreach (var n in Containers.Concat(Objects))
        {
            if (!GodotObject.IsInstanceValid(n) || Hidden.ContainsKey(n.GetInstanceId())) continue;
            Hidden[n.GetInstanceId()] = n.Visible;
            n.Visible = false;
        }
    }

    public static void ShowLive()
    {
        foreach (var (id, visible) in Hidden)
            if (GodotObject.InstanceFromId(id) is Node3D n) n.Visible = visible;
        Hidden.Clear();
    }
}
