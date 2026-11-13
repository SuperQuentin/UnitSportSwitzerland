using Godot;
using UnitSport.Items;
using UnitSport.Styles;
using UnitSport.Terrain;
using UnitSport.Terrain.Earth;

namespace UnitSport.Core;

/// <summary>
/// <c>--debugcheck</c> (#339): drives the debug menu the way a player does, through the input
/// pipeline: F9, mouse clicks on its switches, Space, Esc, <c>/debug</c>.
///
/// <para>
/// Offline (<c>--debugcheck --systems ui</c>, tier quick): F9 opens it; a click hides the ground of
/// every tile; Space does not flip the switch clicked last (no keyboard focus); Esc closes it and
/// does not open the pause menu, and the ground stays hidden; <c>/debug</c> opens it again; the clay
/// view survives a <c>/style</c> restyle and Normal gives the new style's shader back. Then every
/// tool is clicked on and off, each asserted on what it draws or hides (#1743).
/// </para>
///
/// <para>
/// In either frame: under <c>--frame earth</c> the ground is the earth world's leaves (their meshes and
/// furnished nodes, the globe for the far view), and the same clicks are held to them.
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
    private readonly EarthWorld? _earth;

    public DebugMenuCheck(ItemController items, DebugMenu menu, ChunkManager chunks, EarthWorld? earth = null) : base(items, "debugcheck")
    {
        _menu = menu;
        _chunks = chunks;
        _earth = earth;
        Name = "DebugMenuCheck";
    }

    public DebugMenuCheck() : this(null!, null!, null!) { }

    private string World => _earth != null ? "leaf" : "tile";

    public override async void _Ready()
    {
        bool online = CmdArgs.Has("--connect");
        if (online)
        {
            if (!await Joined(150)) return;
            // a leaf's ground drawn before the tools are looked at
            if (_earth != null && !await Until(() => GroundMesh() != null, 180))
            {
                Fail("no leaf ground built");
                return;
            }
            await Online();
        }
        else
        {
            if (!await Until(() => Chat != null && GroundMesh() != null, _earth != null ? 240 : 90))
            {
                Fail($"no {World} ground built");
                return;
            }
            // the leaves round the camera settled, the camera placed over them (a real region's leaves land one by one)
            if (_earth != null && !await Until(() => _earth.Idle && GetViewport().GetCamera3D() is { } cam
                    && _earth.Ground.TryGetHeight(cam.GlobalPosition, out float h) && cam.GlobalPosition.Y - h < 1000, 240))
            {
                Fail($"the leaves round the camera never settled ({_earth.Status})");
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
        Expect((HiddenLayers & TileLayers.Ground) != 0, "the click hides the ground layer");
        Expect(GroundMesh() is { Visible: false }, $"a loaded {World}'s ground mesh is hidden");
        Expect(GetViewport().GuiGetFocusOwner() == null, $"no control holds the keyboard ({GetViewport().GuiGetFocusOwner()?.Name})");

        await Key(Godot.Key.Space);
        Expect(!ground.ButtonPressed, "Space (jump) does not flip the switch clicked last");

        await Key(Godot.Key.Escape);
        Expect(!_menu.IsOpen, "Esc closes the menu");
        Expect(!PauseMenuOpen(), "Esc on the debug menu does not open the pause menu");
        Expect(GroundMesh() is { Visible: false }, "the ground stays hidden with the menu closed");

        Chat!.Send("/debug");
        await Frames(3);
        Expect(_menu.IsOpen, "/debug opens it again");
        await Click(ground);
        Expect(GroundMesh() is { Visible: true }, "a second click shows the ground again");

        await Restyle();
        await LanePlan();
        await Tools();

        await Key(Godot.Key.F9);
        Expect(!_menu.IsOpen, "F9 closes it");
    }

    /// <summary>The lane plan (#1181): a click draws it once (legend shown), a second clears it; it is not redrawn per frame.</summary>
    private async Task LanePlan()
    {
        var lanes = _menu.Lanes;
        var box = _menu.SwitchBox("lanes");
        await Click(box);
        Expect(box.ButtonPressed && lanes.On, "a click turns the lane plan on");
        if (!await Until(() => lanes.Builds > 0, 15))
        {
            Expect(false, "the lane plan is drawn once switched on");
            return;
        }
        Expect(lanes.LegendShown, "with its legend");
        int builds = lanes.Builds;
        await Frames(30);
        Expect(lanes.Builds == builds, $"and not drawn again while the camera stays ({lanes.Builds - builds} more)");
        GD.Print($"[debugcheck] lane plan: {lanes.StripQuads} strip quads, {lanes.MouthLabels} labels, {lanes.Arrows} arrows");
        // on the leaves the plan is read from their stand-in road records: a furnished leaf with roads draws strips
        if (_earth?.LeafNodeWithRoads() != null) Expect(lanes.StripQuads > 0, "the lane plan draws the leaves' streets");
        await Click(box);
        Expect(!lanes.On && lanes.Triangles == 0 && !lanes.LegendShown, "a second click clears it and hides the legend");
    }

    /// <summary>Clay on, a restyle, clay still on; Normal gives back the shader of the style now applied.</summary>
    private async Task Restyle()
    {
        await PickView(DebugViewMode.Clay);
        Expect(GroundShader() == DebugView.Clay, "clay replaces the ground's shader");

        var from = StyleKit.Applied;
        var to = from == VisualStyle.Cartoon ? VisualStyle.Ps1 : VisualStyle.Cartoon;
        // a solid object's material (#1069): unshaded in PS1 only, and it follows a live restyle
        var solid = StyleKit.Solid(new StandardMaterial3D());
        Expect(solid.ShadingMode == ShadingIn(from), $"a solid is {ShadingIn(from)} in {from}");
        Chat!.Send($"/style {(to == VisualStyle.Cartoon ? "cartoon" : "ps1")}");
        if (!await Until(() => StyleKit.Applied == to, 10))
        {
            Expect(false, $"/style moved the world from {from} to {to} (still {StyleKit.Applied})");
            return;
        }
        await Frames(3);
        Expect(GroundShader() == DebugView.Clay, $"clay survives the restyle to {to}");
        Expect(StyleKit.Material(MaterialRole.Terrain).Shader == DebugView.Clay, "a material made meanwhile is clay too");
        Expect(solid.ShadingMode == ShadingIn(to), $"the solid follows the restyle to {to} ({solid.ShadingMode})");

        await PickView(DebugViewMode.Normal);
        var want = GD.Load<Shader>(StyleKit.Resolve(to, MaterialRole.Terrain).Path);
        Expect(GroundShader() == want, $"Normal gives back {to}'s terrain shader ({GroundShader()?.ResourcePath})");
    }

    /// <summary>
    /// Every other tool clicked on, held to what it draws or hides, and clicked off again (#1743): the overlays, the
    /// terrain layers, real and generated (leaves or tiles), the far view, the views, freezing and the readout.
    /// </summary>
    private async Task Tools()
    {
        var overlay = _menu.Overlay;

        await Toggle("tiles", true);
        Expect(await Until(() => overlay.GroundLines > 0, 5), $"{(_earth != null ? "leaf edges" : "tile boundaries")} are drawn ({overlay.GroundLines} lines)");
        await Toggle("tiles", false);
        Expect(await Until(() => overlay.GroundLines == 0, 3), "and cleared");

        await Toggle("labels", true);
        Expect(await Until(() => overlay.LabelsShown > 0, 5), $"{World} labels are shown ({overlay.LabelsShown})");
        await Toggle("labels", false);

        await Toggle("origin", true);
        Expect(await Until(() => overlay.GroundLines > 0, 5), $"the {(_earth != null ? "earth anchor's" : "world origin's")} circle is drawn ({overlay.GroundLines} lines)");
        await Toggle("origin", false);

        // a layer that stands on the ground: the roads of a loaded tile or leaf
        if (RoadNode() is { } roads)
        {
            await Toggle("roads", false);
            Expect(roads.RoadMesh is { Visible: false }, $"Roads off hides a {World}'s roads");
            await Toggle("roads", true);
            Expect(roads.RoadMesh is { Visible: true }, "and on shows them");
        }
        else GD.Print($"[debugcheck] no {World} with roads loaded: the roads switch not checked");

        if (_earth != null)
        {
            if (_earth.LeafMesh(real: true) is { } real)
            {
                await Toggle("real", false);
                Expect(!real.Visible, "Real leaves off hides a real leaf");
                await Toggle("real", true);
                Expect(real.Visible, "and on shows it");
            }
            if (_earth.LeafMesh(real: false) is { } fill)
            {
                await Toggle("generated", false);
                Expect(!fill.Visible, "Fill leaves off hides a fill leaf");
                await Toggle("generated", true);
                Expect(fill.Visible, "and on shows it");
            }
            if (GetTree().Root.FindChild("Globe", true, false) is World.Globe globe)
            {
                await Toggle("horizon", false);
                Expect(globe.DebugHidden, "Globe off hides the globe");
                await Toggle("horizon", true);
                Expect(!globe.DebugHidden, "and on shows it");
            }
            else GD.Print("[debugcheck] no globe: its switch not checked");
            Expect(_menu.SwitchBox("curvature").Disabled && _menu.SwitchBox("curvature").TooltipText.Contains("Not on the earth world"),
                "Earth curvature is disabled on the earth world, with its reason");
        }
        else if (_chunks.Horizon is { } horizon)
        {
            await Toggle("horizon", false);
            Expect(!horizon.Visible, "Far horizon off hides the horizon");
            await Toggle("horizon", true);
        }

        await Toggle("freeze", true);
        Expect(_earth != null ? _earth.Frozen : _chunks.FreezeRings, $"Freeze holds the {World} streaming");
        await Seconds(0.4);
        Expect(_menu.ReadoutText.Contains("FROZEN"), "and the readout says so");
        await Toggle("freeze", false);
        Expect(_earth != null ? !_earth.Frozen : !_chunks.FreezeRings, "and lets it go");

        string readout = _menu.ReadoutText;
        Expect(readout.Contains(_earth != null ? "leaf   F" : "tile   "), $"the readout names the {World} under the camera ({readout.Replace('\n', '|')})");
        Expect(!readout.Contains("ground not loaded"), "and the ground under it");

        await PickView(DebugViewMode.VertexColours);
        Expect(GroundShader() is { } colours && colours != DebugView.Clay && colours.Code.Contains("COLOR.rgb"), "vertex colours replace the ground's shader");
        await PickView(DebugViewMode.Overdraw);
        Expect(GetViewport().DebugDraw == Viewport.DebugDrawEnum.Overdraw, "overdraw is the viewport's");

        // wireframe builds the ground again: Godot draws the lines of a mesh made after the switch only
        var before = GroundMesh()?.Mesh;
        await PickView(DebugViewMode.Wireframe);
        Expect(GetViewport().DebugDraw == Viewport.DebugDrawEnum.Wireframe, "wireframe is the viewport's");
        Expect(await Until(() => GroundMesh() is { Mesh: { } now } && now != before, 60), $"and the {World} meshes are built again for it");
        await PickView(DebugViewMode.Normal);
        Expect(GetViewport().DebugDraw == Viewport.DebugDrawEnum.Disabled, "Normal clears the viewport's draw");

        // the chat's "/debug <tools>": the --debugview words, and "off" for all
        Chat!.Send("/debug origin,noroads,clay");
        await Frames(3);
        Expect(overlay.WorldOrigin && _menu.SwitchBox("origin").ButtonPressed && (HiddenLayers & TileLayers.Roads) != 0 && _menu.View == DebugViewMode.Clay,
            "/debug origin,noroads,clay switches those on");
        Chat.Send("/debug off");
        await Frames(3);
        Expect(!overlay.WorldOrigin && HiddenLayers == TileLayers.None && _menu.View == DebugViewMode.Normal && _menu.IsOpen,
            "/debug off turns every tool off and leaves the panel open");
    }

    private static BaseMaterial3D.ShadingModeEnum ShadingIn(VisualStyle style) =>
        style == VisualStyle.Ps1 ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel;

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
        await Click(_menu.SwitchBox("lanes"));
        await PickView(DebugViewMode.Clay);
        Expect((HiddenLayers & TileLayers.Ground) != 0 && _menu.Overlay.Tiles && _menu.View == DebugViewMode.Clay,
            "ground hidden, tile boundaries and clay on");

        Chat.Send($"/admin remove {CmdArgs.Value("--name")}");
        if (!await Until(() => !Permissions.IsAdmin, 15))
        {
            Expect(false, "/admin remove takes admin away");
            return;
        }
        await Frames(3);
        Expect(!_menu.IsOpen, "losing admin closes the menu");
        Expect(HiddenLayers == TileLayers.None, "and shows every layer again");
        Expect(!_menu.Overlay.Tiles && _menu.View == DebugViewMode.Normal, "and turns the overlay and the clay view off");
        Expect(!_menu.Lanes.On && !_menu.SwitchBox("lanes").ButtonPressed, "and the lane plan");
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
        // a switch below the fold: brought into the panel's view first, as a player scrolls to it
        if (control.GetParent() is { } parent && FindScroll(parent) is { } scroll)
        {
            scroll.EnsureControlVisible(control);
            await Frames(2);
        }
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

    private static ScrollContainer? FindScroll(Node from)
    {
        for (var n = from; n != null; n = n.GetParent())
            if (n is ScrollContainer s) return s;
        return null;
    }

    /// <summary>A switch clicked into the state wanted (no click when it is already there).</summary>
    private async Task Toggle(string key, bool on)
    {
        var box = _menu.SwitchBox(key);
        if (box.ButtonPressed != on) await Click(box);
        Expect(box.ButtonPressed == on, $"a click turns {box.Text} {(on ? "on" : "off")}");
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

    private TileLayers HiddenLayers => _earth?.HiddenLayers ?? _chunks.HiddenLayers;

    /// <summary>The ground mesh of a loaded tile (a leaf under earth), or null while none is built.</summary>
    private MeshInstance3D? GroundMesh()
    {
        if (_earth != null) return _earth.LeafMesh();
        _chunks.ListTiles(_tiles);
        foreach (var (id, _) in _tiles)
            if (_chunks.GroundMeshAt(id) is { Mesh: not null } ground) return ground;
        return null;
    }

    /// <summary>A loaded tile's (or leaf's) node with roads drawn.</summary>
    private ChunkNode? RoadNode()
    {
        if (_earth != null) return _earth.LeafNodeWithRoads();
        _chunks.ListTiles(_tiles);
        foreach (var (id, _) in _tiles)
            if (_chunks.GroundMeshAt(id)?.GetParent() is ChunkNode { RoadMesh: not null } node) return node;
        return null;
    }

    private Shader? GroundShader()
    {
        var ground = GroundMesh();
        // a leaf's material is its instance's override; a tile's is its surface's
        var material = ground?.MaterialOverride ?? ground?.Mesh?.SurfaceGetMaterial(0);
        return (material as ShaderMaterial)?.Shader;
    }

    private bool PauseMenuOpen() => GetTree().Root.FindChild("Menus", true, false) is CanvasLayer { Visible: true };
}
