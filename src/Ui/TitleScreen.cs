using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The first screen: the name, the running occasion, and the ways in — Play solo, Multiplayer, Map,
/// Settings, Controls, Quit, and "Update to vX.Y.Z" once the update check finds a newer release
/// (<see cref="UpdatePrompt"/>). The 3D valley (<see cref="TitleDiorama"/>) turns behind it; a dark
/// gradient on the left keeps the text readable over it. Esc does nothing here (there is nothing
/// behind it), but it is still consumed.
/// </summary>
public partial class TitleScreen : Screen
{
    private AudioStreamPlayer _jingle = null!;
    private static readonly HashSet<string> Jingled = new();
    private Button _first = null!;
    private UpdatePrompt? _update;
    private Button _updateEntry = null!;
    private Container? _entries;

    public static TitleScreen Create() => new() { Name = "Title" };

    public override void _Ready()
    {
        AddChild(SideShade(0.92f, 0.62f));

        var skin = Skins.MenuSkin.Current;
        bool slanted = skin.Shape.Skew > 0;
        var column = UiKit.VBox(6);
        column.SetAnchorsPreset(LayoutPreset.LeftWide);
        // centred above the footer, so a shown "Update to" entry does not push Quit onto it
        column.OffsetLeft = 72; column.OffsetRight = 72 + 380; column.OffsetTop = 0; column.OffsetBottom = -50;
        column.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(column);

        // the logo: the skin's display face (#1135), a condensed italic in capitals for Low-poly Sport
        var name = UiKit.Text("UnitSport", slanted ? 78 : 58);
        UiKit.Display(name);
        name.AddThemeConstantOverride("line_spacing", -12);
        column.AddChild(name);
        var countryRow = UiKit.HBox(14);
        var country = UiKit.Text("SWITZERLAND", slanted ? 18 : 16, UiTheme.Amber);
        country.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.Bold, SpacingGlyph = slanted ? 8 : 7 });
        country.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        countryRow.AddChild(country);
        // the skin's second accent as a slanted dash beside the name
        if (slanted) countryRow.AddChild(Dash(Skins.MenuSkin.Role("accent2"), skin.Shape.Skew));
        column.AddChild(countryRow);
        column.AddChild(UiKit.Spacer(slanted ? 24 : 34));

        // hidden until the update check finds a newer release (#845); one press updates, no questions
        _updateEntry = Entry(column, "", PressUpdate);
        _updateEntry.AddThemeColorOverride("font_color", UiTheme.Amber);
        _updateEntry.Visible = false;
        _first = Entry(column, "Play solo", () => Shell.Push(SoloScreen.Create()));
        Entry(column, "Multiplayer", () => Shell.Push(MultiplayerScreen.Create()));
        // The map of Switzerland: what terrain is downloaded, and how to get more (#515)
        Entry(column, "Map", () => Shell.Push(MapScreen.Create()));
        Entry(column, "Settings", () => Shell.Push(SettingsScreen.Create()));
        // VR (#186): a restart either way, after a confirmation
        if (Platform.CanSpawnProcesses) // a restart with OpenXR (#63)
            Entry(column, XR.XrSession.Active ? "Leave VR" : "Play in VR",
                () => SettingsScreen.AskVr(this, Shell, !XR.XrSession.Active));
        Entry(column, "Controls", () => Shell.ShowControls());
        column.AddChild(UiKit.Spacer(8));
        Entry(column, "Quit", () => Shell.Quit(), dim: true);
        _entries = column;

        // the occasion of the day (Halloween, Christmas...): a card at the bottom right
        if (OccasionLine() is { } occasion) AddChild(OccasionCard(occasion, slanted));

        // footer: the keys on the left, the version on the right
        var foot = UiKit.HBox(18);
        foot.SetAnchorsPreset(LayoutPreset.BottomWide);
        foot.OffsetLeft = 72; foot.OffsetRight = -64; foot.OffsetBottom = -26; foot.OffsetTop = -50;
        if (!Platform.IsMobile) // keyboard hints, and the room Quit needs on a phone's shorter canvas (#63)
            foot.AddChild(UiKit.Text(InputHints.Format("{help} all controls  ·  F11 fullscreen"), UiTheme.FontSmall, UiTheme.TextDim));
        string version = (string)ProjectSettings.GetSetting("application/config/version", "");
        foot.AddChild(UiKit.Text(version.Length > 0 ? $"v{version}" : "development build", UiTheme.FontSmall, UiTheme.TextFaint));
        AddChild(foot);

        _jingle = new AudioStreamPlayer { Bus = Audio.SfxBus.Name, VolumeDb = -6 };
        AddChild(_jingle);
        PlayJingle();
        _update = UpdatePrompt.Attach(this);
        if (_update != null) _update.Stopped += () => _updateEntry.Disabled = false;
        if (UpdatePrompt.Available is { } tag) ShowUpdate(tag);
    }

    public override void _EnterTree() => UpdatePrompt.Found += ShowUpdate;
    public override void _ExitTree() => UpdatePrompt.Found -= ShowUpdate;

    private void ShowUpdate(string tag)
    {
        if (_update == null) return; // a build that never updates (headless, development, phone)
        _updateEntry.Text = $"Update to {tag}";
        _updateEntry.Visible = true;
    }

    private void PressUpdate()
    {
        if (_update == null) return;
        _updateEntry.Disabled = _update.UpdateNow();
    }

    private static Button Entry(Container into, string text, Action pressed, bool dim = false)
    {
        bool slanted = Skins.MenuSkin.Current.Shape.Skew > 0;
        var b = UiKit.MenuButton(text, dim ? (slanted ? 20 : 17) : (slanted ? 25 : 22));
        if (slanted) b.CustomMinimumSize = new Vector2(0, dim ? 40 : 48);
        if (dim) UiKit.DimFont(b);
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    public override void OnShown()
    {
        _first.CallDeferred(Control.MethodName.GrabFocus);
        Callable.From(SlideIn).CallDeferred();
    }

    /// <summary>The entries slide in one after another (#1135), once laid out; none of it with Reduce motion.</summary>
    private void SlideIn()
    {
        if (_entries == null || GameSettings.Current.ReduceMotion || Skins.MenuSkin.Current.Shape.Skew <= 0) return;
        int i = 0;
        foreach (var child in _entries.GetChildren())
        {
            if (child is not Button { Visible: true } b) continue;
            var at = b.Position;
            b.Position = at - new Vector2(16, 0);
            b.Modulate = new Color(1, 1, 1, 0);
            var tween = b.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(b, "position", at, 0.32).SetDelay(i * 0.05);
            tween.TweenProperty(b, "modulate:a", 1f, 0.32).SetDelay(i * 0.05);
            i++;
        }
    }

    /// <summary>A short slanted bar in <paramref name="color"/>: the skin's dash beside the logo.</summary>
    private static Control Dash(Color color, float skew)
    {
        var box = new StyleBoxFlat { BgColor = color, Skew = new Vector2(skew * 2.5f, 0), AntiAliasing = true };
        var p = new Panel { CustomMinimumSize = new Vector2(70, 6), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", box);
        return p;
    }

    /// <summary>The occasion running now, as a card at the bottom right ("Now on").</summary>
    private static Control OccasionCard(string occasion, bool slanted)
    {
        var body = UiKit.VBox(2);
        var head = UiKit.Text("NOW ON", UiTheme.FontSmall);
        UiKit.Display(head);
        head.AddThemeColorOverride("font_color", Skins.MenuSkin.Role("accent2"));
        body.AddChild(head);
        // "NOW ON" says it is running: the card names it only
        body.AddChild(UiKit.Text(occasion.Replace(" is on", ""), UiTheme.FontBody, bold: true, wrap: true));
        var card = UiKit.Card(body, margin: 16);
        if (slanted)
            card.Ready += () =>
            {
                if (card.GetThemeStylebox("panel").Duplicate() is StyleBoxFlat box)
                {
                    box.Skew = new Vector2(Skins.MenuSkin.Current.Shape.Skew * 0.5f, 0);
                    box.ContentMarginLeft += 10; box.ContentMarginRight += 10;
                    card.AddThemeStyleboxOverride("panel", box);
                }
            };
        card.CustomMinimumSize = new Vector2(300, 0);
        card.SetAnchorsPreset(LayoutPreset.BottomRight);
        card.GrowHorizontal = GrowDirection.Begin;
        card.GrowVertical = GrowDirection.Begin;
        card.OffsetRight = -64; card.OffsetBottom = -96;
        return card;
    }

    /// <summary>Nothing behind the title: Esc stays here.</summary>
    public override bool OnBack() => false;

    /// <summary>A left-to-right shade so text stays readable over the 3D backdrop.</summary>
    internal static TextureRect SideShade(float left, float width)
    {
        var Ground = Skins.MenuSkin.Role("ground");
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient
                {
                    // the skin's ground (#1135): cream under Alpine's ink, indigo under Night Arcade's
                    Colors = new[] { new Color(Ground, left), new Color(Ground, left * 0.6f), new Color(Ground, 0) },
                    Offsets = new[] { 0f, 0.45f, 1f },
                },
                FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 0),
                Width = 256, Height = 4,
            },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        shade.SetAnchorsPreset(LayoutPreset.LeftWide);
        shade.AnchorRight = width;
        return shade;
    }

    /// <summary>The occasions the calendar (and the player's own Always / Off) has on today.</summary>
    private static List<Occasions.OccasionEntry> Running()
    {
        var entries = Occasions.OccasionConfig.Load();
        var prefs = GameSettings.Current.OccasionPreferences;
        var on = Occasions.OccasionSchedule.Evaluate(entries, DateOnly.FromDateTime(DateTime.Today)).Select(r => r.Entry).ToList();
        foreach (var e in entries)
            if (prefs.TryGetValue(e.Id, out var p) && p == Occasions.OccasionPreference.Always && !on.Contains(e)) on.Add(e);
        on.RemoveAll(e => prefs.TryGetValue(e.Id, out var p) && p == Occasions.OccasionPreference.Off);
        return on;
    }

    private static string? OccasionLine()
    {
        try
        {
            var on = Running();
            return on.Count == 0 ? null : string.Join("  ·  ", on.Select(e => $"{Occasions.OccasionRegistry.Get(e.Id).Title} is on"));
        }
        catch (Exception) { return null; }
    }

    /// <summary>The running occasion's chip-tune jingle, once per session (the Sfx bus, so the slider governs it).</summary>
    private void PlayJingle()
    {
        try
        {
            var top = Running().Where(e => (e.Facets.ToFlags() & Occasions.OccasionFacets.Audio) != 0)
                .OrderByDescending(e => e.Priority).FirstOrDefault();
            if (top == null || !Jingled.Add(top.Id)) return;
            if (Occasions.OccasionRegistry.Get(top.Id).Jingle() is not { } samples) return;
            _jingle.Stream = Audio.Dsp.Encode(Audio.Dsp.Normalise(samples, 0.8f));
            _jingle.Play();
        }
        catch (Exception e) { GD.PushWarning($"[title] no jingle: {e.Message}"); }
    }
}
