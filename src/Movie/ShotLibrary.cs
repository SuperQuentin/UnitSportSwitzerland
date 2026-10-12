using Godot;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The shot library (#777): every shot of the movie as a compact row (a small still, its name, its rig)
/// under its group's heading, the picked one lit. Hovering a row shows the bigger live picture beside it;
/// clicking picks it. Rebuilt only when the shots change, never per frame.
/// </summary>
public partial class ShotLibrary : VBoxContainer
{
    private readonly ShotThumbs _thumbs;
    private readonly VBoxContainer _rows;
    private readonly PanelContainer _hoverPanel;
    private readonly TextureRect _hoverPicture;
    private readonly Label _hoverName;
    private readonly List<(Button Row, TextureRect Still)> _shown = new();
    private double _shape = double.NaN;
    private int _picked = -1;

    /// <summary>A row was clicked: the shot to pick.</summary>
    public event Action<int>? Picked;
    /// <summary>The add buttons, for the studio to wire (a new shot from the view, a new group).</summary>
    public Button AddShot { get; }
    public Button AddGroup { get; }

    public ShotLibrary(ShotThumbs thumbs)
    {
        _thumbs = thumbs;
        AddThemeConstantOverride("separation", 4);
        CustomMinimumSize = new Vector2(230, 0);
        var buttons = UiKit.HBox(4);
        AddShot = UiKit.Button("Shot");
        AddShot.Icon = Icons.Plus;
        AddShot.TooltipText = "A new shot from the view, at the playhead (N)";
        AddGroup = UiKit.Button("Group");
        AddGroup.Icon = Icons.Folder;
        AddGroup.TooltipText = "Put the picked shot in a new group";
        buttons.AddChild(AddShot);
        buttons.AddChild(AddGroup);
        AddChild(buttons);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = UiKit.VBox(2);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        AddChild(scroll);
        AddChild(UiKit.Text("Hover a shot to preview it", UiTheme.FontTiny, UiTheme.TextFaint));

        // the bigger picture, floating to the right of the list while a row is hovered
        _hoverPanel = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, TopLevel = true, ZIndex = 10 };
        _hoverPanel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.95f, 8, 6));
        var box = UiKit.VBox(4);
        _hoverName = UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, bold: true);
        box.AddChild(_hoverName);
        _hoverPicture = new TextureRect { CustomMinimumSize = ShotThumbs.HoverSize, StretchMode = TextureRect.StretchModeEnum.Scale, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore };
        box.AddChild(_hoverPicture);
        _hoverPanel.AddChild(box);
        AddChild(_hoverPanel);
        _thumbs.Updated += ShowStills;
    }

    public override void _ExitTree() => _thumbs.Updated -= ShowStills;

    /// <summary>The rows as the project has them now; cheap when nothing changed.</summary>
    public void Refresh(MovieProject p, int picked)
    {
        double shape = p.Cameras.Count;
        foreach (var cam in p.Cameras) shape = shape * 1.0003 + cam.Name.GetHashCode() * 1e-9 + cam.Group.GetHashCode() * 1e-10 + (int)cam.Rig + cam.Target * 0.1;
        if (shape == _shape && picked == _picked) return;
        bool rebuilt = shape != _shape;
        _shape = shape;
        _picked = picked;
        if (rebuilt) Rebuild(p);
        for (int i = 0; i < _shown.Count; i++) Light(_shown[i].Row, i == picked);
    }

    private void Rebuild(MovieProject p)
    {
        foreach (var child in _rows.GetChildren()) child.QueueFree();
        _shown.Clear();
        for (int i = 0; i < p.Cameras.Count; i++) _shown.Add((null!, null!));
        // groups in the order they first appear; shots without one under "Shots"
        var groups = p.Cameras.Select(c => c.Group).Distinct().OrderBy(g => g.Length == 0 ? 1 : 0).ToList();
        foreach (string group in groups)
        {
            var head = UiKit.Text(group.Length == 0 ? "Shots" : group, UiTheme.FontSmall, UiTheme.TextDim, bold: true);
            _rows.AddChild(head);
            for (int i = 0; i < p.Cameras.Count; i++)
                if (p.Cameras[i].Group == group) _shown[i] = MakeRow(p, i);
        }
        ShowStills();
    }

    private (Button, TextureRect) MakeRow(MovieProject p, int index)
    {
        var cam = p.Cameras[index];
        var row = new Button { ToggleMode = false, CustomMinimumSize = new Vector2(0, 40), FocusMode = FocusModeEnum.All, TooltipText = $"{cam.Name}: pick it" };
        var line = UiKit.HBox(6);
        line.SetAnchorsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 4; line.OffsetRight = -4;
        line.MouseFilter = MouseFilterEnum.Ignore;
        var still = new TextureRect
        {
            CustomMinimumSize = new Vector2(56, 32), StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore,
        };
        line.AddChild(still);
        var name = UiKit.Text(cam.Name, UiTheme.FontSmall, UiTheme.Text);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Ignore;
        line.AddChild(name);
        string rig = cam.Relative ? cam.Rig.ToString() : "World";
        var tag = UiKit.Text(rig, UiTheme.FontTiny, cam.Relative ? new Color(0.45f, 0.85f, 0.7f) : new Color(0.7f, 0.68f, 0.95f));
        tag.MouseFilter = MouseFilterEnum.Ignore;
        line.AddChild(tag);
        row.AddChild(line);
        row.Pressed += () => Picked?.Invoke(index);
        // dragged onto the timeline's shot row (#778): the movie cuts to it there
        row.SetDragForwarding(Callable.From((Vector2 _) => DragShot(index, cam.Name)), Callable.From((Vector2 _, Variant _) => false), Callable.From((Vector2 _, Variant _) => { }));
        row.MouseEntered += () => Hover(index, row, cam.Name);
        row.MouseExited += () => Hover(-1, row, "");
        row.FocusEntered += () => Hover(index, row, cam.Name);
        row.FocusExited += () => Hover(-1, row, "");
        _rows.AddChild(row);
        return (row, still);
    }

    /// <summary>What a dragged row carries: the shot, and a label under the pointer.</summary>
    private Variant DragShot(int index, string name)
    {
        var label = UiKit.Text($"Shot: {name}", UiTheme.FontSmall, UiTheme.Amber, bold: true);
        SetDragPreview(label);
        return new Godot.Collections.Dictionary { ["shot"] = index };
    }

    private void Hover(int index, Control row, string name)
    {
        _thumbs.Hovered = index;
        _hoverPanel.Visible = index >= 0;
        if (index < 0) return;
        _hoverName.Text = name;
        _hoverPicture.Texture = _thumbs.HoverTexture;
        var r = row.GetGlobalRect();
        _hoverPanel.GlobalPosition = new Vector2(r.End.X + 12, Math.Max(8, r.Position.Y - 40));
    }

    private void ShowStills()
    {
        for (int i = 0; i < _shown.Count; i++)
            if (_shown[i].Still is { } still && IsInstanceValid(still)) still.Texture = _thumbs.Still(i);
    }

    private static void Light(Button row, bool on)
    {
        if (row == null || !IsInstanceValid(row)) return;
        row.Modulate = on ? new Color(1.15f, 1.05f, 0.75f) : Colors.White;
    }
}
