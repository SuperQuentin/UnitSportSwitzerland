using Godot;
using UnitSport.Items;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// <c>--debugcheck</c> (#339): drives the debug menu the way a player does, through the input
/// pipeline: F9, mouse clicks on its switches, Space, Esc, <c>/debug</c>.
///
/// <para>
/// Offline (<c>--debugcheck --systems ui</c>, tier quick): F9 opens it; a click hides the ground of
/// every tile; Space does not flip the switch clicked last (no keyboard focus); Esc closes it and
/// does not open the pause menu, and the ground stays hidden; <c>/debug</c> opens it again; the clay
/// view survives a <c>/style</c> restyle and Normal gives the new style's shader back.
/// </para>
///
/// <para>
/// Online (<c>tools/debugcheck.sh</c>: a server with <c>--admin-password</c> and this client,
/// tier net): refused before <c>/login</c>, offered after it, and losing admin
/// (<c>/admin remove</c>) closes it and turns every tool off.
/// </para>
///
/// <para>
/// The view picker is set through its <c>ItemSelected</c> signal, not by clicking into its popup.
/// </para>
/// </summary>
public partial class DebugMenuCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--debugcheck");

    private const string Password = "debugcheck";

    private readonly DebugMenu _menu;
    private readonly ChunkManager _chunks;

    public DebugMenuCheck(ItemController items, DebugMenu menu, ChunkManager chunks) : base(items, "debugcheck")
    {
        _menu = menu;
        _chunks = chunks;
        Name = "DebugMenuCheck";
    }

    public DebugMenuCheck() : this(null!, null!, null!) { }

    public override async void _Ready()
    {
        bool online = CmdArgs.Has("--connect");
        if (online)
        {
            if (!await Joined(150)) return;
            await Online();
        }
        else
        {
            if (!await Until(() => Chat != null && TileGround() != null, 90))
            {
                Fail("no tile ground built");
                return;
            }
            Chat!.LineReceived += (line, _) => _heard.Add(line);
            await Offline();
        }
        await Finish(0.5);
    }

    // ------------------------------------------------------------------------------------
    // scenarios
    // ------------------------------------------------------------------------------------

    private async Task Offline()
    {
        await Key(Godot.Key.F9);
        Expect(_menu.IsOpen, "F9 opens the menu");

        var ground = _menu.SwitchBox("ground");
        await Click(ground);
        Expect(!ground.ButtonPressed, "a click turns the Ground switch off");
        Expect((_chunks.HiddenLayers & TileLayers.Ground) != 0, "the click hides the ground layer");
        Expect(TileGround() is { Visible: false }, "a loaded tile's ground mesh is hidden");
        Expect(GetViewport().GuiGetFocusOwner() == null, $"no control holds the keyboard ({GetViewport().GuiGetFocusOwner()?.Name})");

        await Key(Godot.Key.Space);
        Expect(!ground.ButtonPressed, "Space (jump) does not flip the switch clicked last");

        await Key(Godot.Key.Escape);
        Expect(!_menu.IsOpen, "Esc closes the menu");
        Expect(!PauseMenuOpen(), "Esc on the debug menu does not open the pause menu");
        Expect(TileGround() is { Visible: false }, "the ground stays hidden with the menu closed");

        Chat!.Send("/debug");
        await Frames(3);
        Expect(_menu.IsOpen, "/debug opens it again");
        await Click(ground);
        Expect(TileGround() is { Visible: true }, "a second click shows the ground again");

        await Restyle();

        await Key(Godot.Key.F9);
        Expect(!_menu.IsOpen, "F9 closes it");
    }

    /// <summary>Clay on, a restyle, clay still on; Normal gives back the shader of the style now applied.</summary>
    private async Task Restyle()
    {
        await PickView(DebugViewMode.Clay);
        Expect(GroundShader() == DebugView.Clay, "clay replaces the ground's shader");

        var from = StyleKit.Applied;
        var to = from == VisualStyle.Cartoon ? VisualStyle.Ps1 : VisualStyle.Cartoon;
        Chat!.Send($"/style {(to == VisualStyle.Cartoon ? "cartoon" : "ps1")}");
        if (!await Until(() => StyleKit.Applied == to, 10))
        {
            Expect(false, $"/style moved the world from {from} to {to} (still {StyleKit.Applied})");
            return;
        }
        await Frames(3);
        Expect(GroundShader() == DebugView.Clay, $"clay survives the restyle to {to}");
        Expect(StyleKit.Material(MaterialRole.Terrain).Shader == DebugView.Clay, "a material made meanwhile is clay too");

        await PickView(DebugViewMode.Normal);
        var want = GD.Load<Shader>(StyleKit.Resolve(to, MaterialRole.Terrain).Path);
        Expect(GroundShader() == want, $"Normal gives back {to}'s terrain shader ({GroundShader()?.ResourcePath})");
    }

    private async Task Online()
    {
        await Key(Godot.Key.F9);
        Expect(!_menu.IsOpen, "F9 is refused to a player who is not an admin");
        Chat!.Send("/debug");
        Expect(await Until(() => _heard.Any(l => l.Contains("admins on a server")), 5), "/debug is refused too");
        Expect(!_menu.IsOpen, "still closed");

        Chat.Send($"/login {Password}");
        if (!await Until(() => Permissions.IsAdmin, 15))
        {
            Expect(false, "/login makes this client an admin");
            return;
        }
        await Key(Godot.Key.F9);
        Expect(_menu.IsOpen, "F9 opens it for an admin");
        await Click(_menu.SwitchBox("ground"));
        await Click(_menu.SwitchBox("tiles"));
        await PickView(DebugViewMode.Clay);
        Expect((_chunks.HiddenLayers & TileLayers.Ground) != 0 && _menu.Overlay.Tiles && _menu.View == DebugViewMode.Clay,
            "ground hidden, tile boundaries and clay on");

        Chat.Send($"/admin remove {CmdArgs.Value("--name")}");
        if (!await Until(() => !Permissions.IsAdmin, 15))
        {
            Expect(false, "/admin remove takes admin away");
            return;
        }
        await Frames(3);
        Expect(!_menu.IsOpen, "losing admin closes the menu");
        Expect(_chunks.HiddenLayers == TileLayers.None, "and shows every layer again");
        Expect(!_menu.Overlay.Tiles && _menu.View == DebugViewMode.Normal, "and turns the overlay and the clay view off");
        Expect(_menu.SwitchBox("ground").ButtonPressed && !_menu.SwitchBox("tiles").ButtonPressed, "and puts the switches back");
        Expect(GroundShader() is { } shader && shader != DebugView.Clay, "the ground has its own shader again");
        await Key(Godot.Key.F9);
        Expect(!_menu.IsOpen, "F9 is refused again");
    }

    // ------------------------------------------------------------------------------------
    // input, through the same pipeline as a player's
    // ------------------------------------------------------------------------------------

    private async Task Key(Key key)
    {
        foreach (bool pressed in new[] { true, false })
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });
            await Frames(2);
        }
    }

    /// <summary>A left click at the middle of a control, pushed into the viewport as the mouse would.</summary>
    private async Task Click(Control control)
    {
        var at = control.GetGlobalRect().GetCenter();
        foreach (bool pressed in new[] { true, false })
        {
            GetViewport().PushInput(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at,
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
            }, inLocalCoords: true);
            await Frames(2);
        }
    }

    private async Task PickView(DebugViewMode mode)
    {
        _menu.ViewPicker.Select((int)mode);
        _menu.ViewPicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)mode);
        await Frames(2);
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    // ------------------------------------------------------------------------------------
    // what is looked at
    // ------------------------------------------------------------------------------------

    private readonly List<(Terrain.Format.TileId Id, int Stride)> _tiles = new();

    /// <summary>The ground mesh of a loaded tile, or null while none is built.</summary>
    private MeshInstance3D? TileGround()
    {
        _chunks.ListTiles(_tiles);
        foreach (var (id, _) in _tiles)
            if (_chunks.GroundAt(id) is { Mesh: not null } ground) return ground;
        return null;
    }

    private Shader? GroundShader() =>
        (TileGround()?.Mesh?.SurfaceGetMaterial(0) as ShaderMaterial)?.Shader;

    private bool PauseMenuOpen() => GetTree().Root.FindChild("Menus", true, false) is CanvasLayer { Visible: true };
}
