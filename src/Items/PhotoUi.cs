using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Items;

/// <summary>
/// The Polaroid's screens, over the inventory (layer 13): the print developing near the bottom of
/// the screen when the camera is at the eye, a print looked at full size (<see cref="Inspect"/>),
/// and the album (<see cref="OpenAlbum"/>): every photo in the pack plus every photo ever taken on
/// this machine. Docs: <c>docs/notes/items/polaroid.md</c>.
/// </summary>
public partial class PhotoUi : CanvasLayer
{
    private const float CardPx = 132f;

    private readonly ItemController _items;
    private Control _root = null!;

    // developing card
    private TextureRect _developing = null!;
    private ShaderMaterial _developMat = null!;
    private float _developSlide;
    private bool _developOn;

    // the viewer
    private Control _viewer = null!;
    private TextureRect _viewerImage = null!;
    private Label _viewerCaption = null!;
    private Button _copyButton = null!;
    private string? _viewing;

    // the album
    private Control _album = null!;
    private GridContainer _grid = null!;
    private Label _albumCount = null!;

    public PhotoUi(ItemController items) => _items = items;
    public PhotoUi() : this(null!) { }

    /// <summary>The viewer or the album is open: they take the input, the inventory under them waits.</summary>
    public bool Blocking => _viewer.Visible || _album.Visible;

    public bool AlbumOpen => _album.Visible;
    public string? Viewing => _viewer.Visible ? _viewing : null;

    public override void _Ready()
    {
        Layer = 13;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        _developMat = PhotoVisuals.Developing2D();
        _developing = new TextureRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspect,
            Material = _developMat,
            Size = new Vector2(CardPx, CardPx * PhotoStore.CardH / PhotoStore.CardW),
        };
        _root.AddChild(_developing);

        BuildViewer();
        BuildAlbum();
    }

    // ---- developing ------------------------------------------------------------------------------

    /// <summary>The print comes up from the bottom edge of the screen and develops there.</summary>
    public void StartDevelop(Texture2D print)
    {
        _developing.Texture = print;
        _developMat.SetShaderParameter("develop", 0f);
        _developSlide = 0f;
        _developOn = true;
        _developing.Visible = true;
    }

    public void SetDevelop(float t) => _developMat.SetShaderParameter("develop", t);

    public void EndDevelop() => _developOn = false;

    // ---- the viewer ------------------------------------------------------------------------------

    private void BuildViewer()
    {
        _viewer = new ColorRect { Color = new Color(0, 0, 0, 0.72f), Visible = false };
        _viewer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_viewer);

        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        box.AddThemeConstantOverride("separation", 10);
        _viewer.AddChild(box);

        _viewerImage = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddChild(_viewerImage);

        _viewerCaption = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _viewerCaption.AddThemeFontSizeOverride("font_size", 18);
        _viewerCaption.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _viewerCaption.AddThemeConstantOverride("outline_size", 5);
        box.AddChild(_viewerCaption);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 16);
        _copyButton = new Button { Text = "Print a copy" };
        _copyButton.Pressed += PrintCopy;
        row.AddChild(_copyButton);
        var hint = new Label { Text = "Any key or click to close", VerticalAlignment = VerticalAlignment.Center };
        hint.AddThemeFontSizeOverride("font_size", 13);
        hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.72f, 0.76f));
        row.AddChild(hint);
        box.AddChild(row);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24) });
    }

    /// <summary>Shows a print large, with its caption, until any key or click.</summary>
    public void Inspect(string? id)
    {
        if (!PhotoStore.IsValidId(id))
        {
            _items.Ui.Toast("This photo has faded to nothing.");
            return;
        }
        _viewing = id;
        _viewerImage.Texture = PhotoStore.Texture(id) ?? PhotoVisuals.Blank;
        _viewerCaption.Text = PhotoStore.Caption(id!);
        // a copy of an archived photo: film is unlimited, only a free slot is needed
        _copyButton.Visible = _album.Visible && PhotoStore.PathOf(id!) is { } p && p.StartsWith(PhotoStore.LocalDir);
        _viewer.Visible = true;
        UiFocus.Set(this, true);
    }

    private void CloseViewer()
    {
        _viewer.Visible = false;
        if (!_album.Visible) UiFocus.Set(this, false);
    }

    private void PrintCopy()
    {
        if (_viewing == null) return;
        bool room = _items.Inventory.Room(ItemId.Photo, _viewing) >= 1;
        if (_items.Give(new ItemStack(ItemId.Photo, 1, _viewing)) > 0) _items.Ui.Toast("No room in your pack.");
        else if (room) _items.Ui.Toast("A fresh print is in your pack.");
        CloseViewer();
        RefreshAlbum();
    }

    // ---- the album -------------------------------------------------------------------------------

    private void BuildAlbum()
    {
        var centre = new CenterContainer { Visible = false };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(centre);
        _album = centre;

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.08f, 0.07f, 0.06f, 0.97f), 6, 20, 16));
        centre.AddChild(panel);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        panel.AddChild(col);

        var head = new HBoxContainer();
        var title = UiTheme.Title("Album");
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        _albumCount = new Label { VerticalAlignment = VerticalAlignment.Center };
        _albumCount.AddThemeFontSizeOverride("font_size", 13);
        _albumCount.AddThemeColorOverride("font_color", new Color(0.62f, 0.64f, 0.68f));
        head.AddChild(_albumCount);
        var close = new Button { Text = "Close" };
        close.Pressed += CloseAlbum;
        head.AddChild(close);
        col.AddChild(head);

        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(5 * 128 + 4 * 12 + 16, 3 * 176),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        col.AddChild(scroll);
        _grid = new GridContainer { Columns = 5 };
        _grid.AddThemeConstantOverride("h_separation", 12);
        _grid.AddThemeConstantOverride("v_separation", 12);
        scroll.AddChild(_grid);
    }

    /// <summary>Photos in the pack first (marked), then the rest of the archive, newest first.</summary>
    public IReadOnlyList<string> AlbumIds()
    {
        var inPack = new List<string>();
        for (int i = 0; i < Inventory.Size; i++)
            if (_items.Inventory[i] is { Id: ItemId.Photo, Data: { } d } && !inPack.Contains(d)) inPack.Add(d);
        return inPack.Concat(PhotoStore.Archive().Where(id => !inPack.Contains(id))).ToList();
    }

    public void OpenAlbum()
    {
        RefreshAlbum();
        _album.Visible = true;
        UiFocus.Set(this, true);
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
        (_grid.GetChildCount() > 0 ? _grid.GetChild(0).GetChild(0) as Control : null)?.GrabFocus();
    }

    public void CloseAlbum()
    {
        _viewer.Visible = false;
        _album.Visible = false;
        UiFocus.Set(this, false);
        if (!_items.Ui.IsOpen) MouseCapture.Capture();
    }

    private void RefreshAlbum()
    {
        foreach (var child in _grid.GetChildren()) child.QueueFree();
        var ids = AlbumIds();
        var pack = new HashSet<string>();
        for (int i = 0; i < Inventory.Size; i++)
            if (_items.Inventory[i] is { Id: ItemId.Photo, Data: { } d }) pack.Add(d);
        _albumCount.Text = $"{ids.Count} photo(s), {pack.Count} in your pack   ";

        foreach (var id in ids)
        {
            var cell = new VBoxContainer { CustomMinimumSize = new Vector2(128, 0) };
            cell.AddThemeConstantOverride("separation", 2);
            var thumb = new TextureButton
            {
                TextureNormal = PhotoStore.Texture(id) ?? PhotoVisuals.Blank,
                IgnoreTextureSize = true,
                StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(128, 128f * PhotoStore.CardH / PhotoStore.CardW),
                FocusMode = Control.FocusModeEnum.All,
            };
            string photo = id;
            thumb.Pressed += () => Inspect(photo);
            cell.AddChild(thumb);
            var caption = new Label
            {
                Text = (pack.Contains(id) ? "● " : "") + PhotoStore.ShortCaption(id),
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipText = true, CustomMinimumSize = new Vector2(128, 0),
            };
            caption.AddThemeFontSizeOverride("font_size", 11);
            caption.AddThemeColorOverride("font_color", pack.Contains(id) ? new Color(0.98f, 0.84f, 0.38f) : new Color(0.7f, 0.72f, 0.76f));
            cell.AddChild(caption);
            _grid.AddChild(cell);
        }
        if (ids.Count == 0)
            _grid.AddChild(new Label { Text = "No photos yet. Take one with the camera." });
    }

    // ---- input / frame ---------------------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        if (_viewer.Visible)
        {
            bool press = e is InputEventKey { Pressed: true, Echo: false } or InputEventJoypadButton { Pressed: true }
                || (e is InputEventMouseButton { Pressed: true } mb
                    && !(_copyButton.Visible && _copyButton.GetGlobalRect().HasPoint(mb.Position))
                    && mb.ButtonIndex is MouseButton.Left or MouseButton.Right or MouseButton.Middle);
            if (!press) return;
            CloseViewer();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_album.Visible && (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Inventory)
                               || e.IsActionPressed(PlayerInput.Menu)))
        {
            CloseAlbum();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // taken away with the items (mounting, a mode change)
        if (!_items.Ui.ItemsActive && Blocking)
        {
            _viewer.Visible = false;
            CloseAlbum();
        }

        // the developing card rises from under the bottom edge, right of the hotbar, and sinks back when done
        _developSlide = Mathf.MoveToward(_developSlide, _developOn ? 1f : 0f, dt * (_developOn ? 2.5f : 1.5f));
        _developing.Visible = _developSlide > 0.001f;
        if (_developing.Visible)
        {
            var view = _root.Size;
            var size = _developing.Size;
            float ease = 1f - (1f - _developSlide) * (1f - _developSlide);
            _developing.Position = new Vector2(view.X * 0.5f + 210f, view.Y - ease * (size.Y + 24f));
            _developing.Rotation = -0.06f * ease;
        }
    }
}
