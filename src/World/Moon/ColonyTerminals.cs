using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The terminals of an empty colony (#1944): a console in a room whose screen shows its log (<see cref="ColonyLogs"/>).
/// E at it (pad Y; VR: reach out and grip, all through <c>FootPlayer.TryInteract</c>) shows the first page on the screen
/// itself (a <see cref="Label3D"/>: readable in VR, no menu), E again the next, after the last the screen goes back to its
/// title. What a page shows is this client's (reading is nobody else's business); a bot has nothing to read. Built with
/// the colony, in its frame.
/// </summary>
public partial class ColonyTerminals : Node3D
{
    /// <summary>How near a terminal a walker must stand to read it, metres (flat).</summary>
    public const float Reach = 1.7f;

    private static readonly List<ColonyTerminals> All = new();

    private sealed class Terminal
    {
        public Node3D Node = null!;
        public Label3D Screen = null!;
        public ColonyLogs.Log Log = null!;
        public int Page = -1;
    }

    private readonly List<Terminal> _terminals = new();

    public ColonyTerminals(MoonColony colony, ColonyPlan plan)
    {
        Name = "Terminals";
        var used = new HashSet<int>();
        foreach (var log in ColonyLogs.For(colony.Theme))
        {
            int module = -1;
            if (log.AtSealed && plan.Sealed is { } s) module = s;
            else
            {
                // a biodome first for its greenhouse, then the first module of that room not taken yet
                for (int i = 0; i < plan.Modules.Count && module < 0; i++)
                    if (plan.Rooms[i] == log.Room && plan.Modules[i].Kind == ModuleKind.Biodome && !used.Contains(i)) module = i;
                for (int i = 0; i < plan.Modules.Count && module < 0; i++)
                    if (plan.Rooms[i] == log.Room && !used.Contains(i)) module = i;
            }
            if (module < 0) continue;
            used.Add(module);
            var m = plan.Modules[module];
            var (local, facing, floor) = Spot(m);
            var t = new Transform3D(new Basis(Vector3.Up, -(float)m.Yaw), new Vector3((float)m.X, 0, (float)m.Z));
            var at = t * (local + Vector3.Up * floor);
            var dir = (t.Basis * facing).Normalized();
            _terminals.Add(Make(at, dir, log, colony.Theme));
        }
    }

    /// <summary>Where a module's terminal stands in its node frame, the way its screen faces (toward the reader), its floor.</summary>
    private static (Vector3 At, Vector3 Facing, float Floor) Spot(ColonyModule m)
    {
        float f = ColonyKit.FloorY, l = (float)m.Length, r = (float)m.Width / 2;
        return m.Kind switch
        {
            // between the map table and a doorway (the consoles ring the wall)
            ModuleKind.Hub => (new Vector3(0, 0, 1.75f), Vector3.Back, f),
            ModuleKind.Can => (new Vector3(1.3f, 0, l / 2 - 1.1f), Vector3.Left, f),
            ModuleKind.Airlock => (new Vector3(1.3f, 0, 0.4f), Vector3.Left, f),
            ModuleKind.Dome => (new Vector3(-r * 0.55f, 0, 0), Vector3.Right, f),
            ModuleKind.Biodome => (new Vector3(3.0f, 0, -(r - 5)), Vector3.Left, 1.25f),
            _ => (new Vector3(0, 0, 0), Vector3.Forward, f),
        };
    }

    private Terminal Make(Vector3 at, Vector3 facing, ColonyLogs.Log log, ColonyTheme theme)
    {
        var node = new Node3D { Name = "Terminal", Transform = new Transform3D(Basis.LookingAt(facing, Vector3.Up), at) };
        var mi = new MeshInstance3D { Mesh = Desk(theme == ColonyTheme.ColdWar) };
        MeshScratch.Paint(mi, HumanMeshBuilder.Material(), ColonyKit.WindowMaterial());
        node.AddChild(mi);
        var body = new StaticBody3D { Name = "TerminalBody" };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.9f, 1.2f, 0.6f) }, Position = new Vector3(0, 0.6f, 0) });
        node.AddChild(body);
        bool cold = theme == ColonyTheme.ColdWar;
        var screen = new Label3D
        {
            Name = "Screen", Text = log.Title, FontSize = 32, PixelSize = 0.00085f, OutlineSize = 0,
            Modulate = cold ? new Color(1f, 0.72f, 0.25f) : new Color(0.45f, 1f, 0.55f),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Shaded = false, DoubleSided = false, AlphaCut = Label3D.AlphaCutMode.Discard,
            // just in front of the screen (the reader is at the node's -Z; the desk is built turned, its screen at +z), upright
            // so the tilted glass never hides a line; the text runs to the reader's right, the node's -X, from the top left
            Transform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(0.3f, 1.33f, -0.05f)),
        };
        node.AddChild(screen);
        AddChild(node);
        return new Terminal { Node = node, Screen = screen, Log = log };
    }

    private static ArrayMesh? _desk, _coldDesk;

    /// <summary>A console: a desk, a screen tilted back facing the reader (−Z after the build's half turn), a keyboard; the Cold War's with tape reels.</summary>
    internal static ArrayMesh Desk(bool cold)
    {
        if ((cold ? _coldDesk : _desk) is { } done) return done;
        var s = new MeshScratch();
        Color body = new Color(cold ? "5b6046" : "3a3d41") with { A = 0 }, dark = new Color("1b1d20") with { A = 0 }, glass = new Color(cold ? "2a1e08" : "0b1e10") with { A = 0 };
        s.Box(new(0, 0.38f, 0), new(0.9f, 0.76f, 0.55f), body);
        s.Box(new(0, 0.79f, 0.12f), new(0.8f, 0.04f, 0.3f), dark);
        var tilt = new Basis(Vector3.Right, 0.3f);
        s.Box(new(0, 1.12f, -0.12f), new(0.74f, 0.56f, 0.12f), body, tilt);
        // the screen's dark glass (the log is drawn over it)
        s.Box(new(0, 1.13f, -0.06f), new(0.66f, 0.46f, 0.02f), glass, tilt);
        if (cold)
            foreach (float x in new[] { -0.2f, 0.2f })
                s.Ring(new(x, 1.55f, -0.15f), Vector3.Back, 0.03f, 0.13f, 0.04f, dark, 12);
        var m = s.Build();
        if (cold) _coldDesk = m; else _desk = m;
        return m;
    }

    public override void _EnterTree() => All.Add(this);

    public override void _ExitTree() => All.Remove(this);

    private static (ColonyTerminals Set, Terminal T)? Nearest(Vector3 at)
    {
        (ColonyTerminals, Terminal)? best = null;
        float bestD = Reach;
        foreach (var set in All)
            foreach (var t in set._terminals)
            {
                var p = t.Node.GlobalPosition;
                float d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
                if (d < bestD && Mathf.Abs(p.Y + 0.5f - at.Y) < 2f)
                {
                    bestD = d;
                    best = (set, t);
                }
            }
        return best;
    }

    /// <summary>E at a terminal (pad Y; VR: grip): its next page, back to its title after the last.</summary>
    public static bool TryUse(Player.FootPlayer p)
    {
        if (Nearest(p.GlobalPosition) is not { } n) return false;
        var t = n.T;
        t.Page = t.Page + 1 >= t.Log.Pages.Length ? -1 : t.Page + 1;
        t.Screen.Text = t.Page < 0 ? t.Log.Title : $"{t.Log.Title}   {t.Page + 1}/{t.Log.Pages.Length}\n\n{t.Log.Pages[t.Page]}";
        return true;
    }

    /// <summary>The prompt at a terminal: read it, the next page, or done.</summary>
    public static Interaction? InteractionFor(Player.FootPlayer p)
    {
        if (Nearest(p.GlobalPosition) is not { } n) return null;
        var t = n.T;
        string what = t.Page < 0 ? "Read the terminal" : t.Page + 1 < t.Log.Pages.Length ? "Next page" : "Close the log";
        return new Interaction(InputHints.Prompt(PlayerInput.InteractMount, what), Outline.Of(t.Node));
    }

    // ---- for the checks ---------------------------------------------------------------------------------------------

    public static IReadOnlyList<ColonyTerminals> Sets => All;

    public int Count => _terminals.Count;

    public Vector3 At(int i) => _terminals[i].Node.GlobalPosition;

    /// <summary>The way a terminal's screen faces (world, toward its reader).</summary>
    public Vector3 Facing(int i) => -_terminals[i].Node.GlobalBasis.Z;

    public string Shown(int i) => _terminals[i].Screen.Text;
}
