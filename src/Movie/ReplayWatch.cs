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
