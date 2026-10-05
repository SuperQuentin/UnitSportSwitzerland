using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The first screen: the name, the running occasion, and the ways in — Play solo, Multiplayer, Map,
/// Settings, Controls, Quit. The 3D valley (<see cref="TitleDiorama"/>) turns behind it; a dark
/// gradient on the left keeps the text readable over it. Esc does nothing here (there is nothing
/// behind it), but it is still consumed.
/// </summary>
public partial class TitleScreen : Screen
{
    private AudioStreamPlayer _jingle = null!;
    private static readonly HashSet<string> Jingled = new();
    private Button _first = null!;
    private UpdatePrompt? _update;

    public static TitleScreen Create() => new() { Name = "Title" };

    public override void _Ready()
    {
        AddChild(SideShade(0.92f, 0.62f));

        var column = UiKit.VBox(6);
        column.SetAnchorsPreset(LayoutPreset.LeftWide);
        column.OffsetLeft = 72; column.OffsetRight = 72 + 380; column.OffsetTop = 0; column.OffsetBottom = 0;
        column.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(column);

        var name = UiKit.Text("UnitSport", 58, Colors.White, bold: true);
        name.AddThemeConstantOverride("line_spacing", -8);
        column.AddChild(name);
        var country = UiKit.Text("SWITZERLAND", 16, UiTheme.Amber);
        country.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.Bold, SpacingGlyph = 7 });
        column.AddChild(country);

        var occasion = OccasionLine();
        column.AddChild(UiKit.Spacer(occasion != null ? 10 : 34));
        if (occasion != null)
        {
            var o = UiKit.Text(occasion, UiTheme.FontSmall, new Color(0.98f, 0.58f, 0.22f));
            column.AddChild(o);
            column.AddChild(UiKit.Spacer(16));
        }

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

        // footer: version and the way back to here
        var foot = UiKit.HBox(18);
        foot.SetAnchorsPreset(LayoutPreset.BottomWide);
        foot.OffsetLeft = 72; foot.OffsetBottom = -26; foot.OffsetTop = -50;
        string version = (string)ProjectSettings.GetSetting("application/config/version", "");
        foot.AddChild(UiKit.Text(version.Length > 0 ? $"v{version}" : "development build", UiTheme.FontTiny, UiTheme.TextFaint));
        foot.AddChild(UiKit.Text(InputHints.Format("{help} all controls  ·  F11 fullscreen"), UiTheme.FontTiny, UiTheme.TextFaint));
        AddChild(foot);

        _jingle = new AudioStreamPlayer { Bus = Audio.SfxBus.Name, VolumeDb = -6 };
        AddChild(_jingle);
        PlayJingle();
        _update = UpdatePrompt.Attach(this);
    }

    private static Button Entry(Container into, string text, Action pressed, bool dim = false)
    {
        var b = UiKit.MenuButton(text, dim ? 17 : 22);
        if (dim) b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    public override void OnShown()
    {
        _first.CallDeferred(Control.MethodName.GrabFocus);
        _update?.OnTitleShown();
    }

    /// <summary>Nothing behind the title: Esc stays here.</summary>
    public override bool OnBack() => false;

    /// <summary>A left-to-right shade so text stays readable over the 3D backdrop.</summary>
    internal static TextureRect SideShade(float left, float width)
    {
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Gradient = new Gradient
                {
                    Colors = new[] { new Color(0.02f, 0.025f, 0.04f, left), new Color(0.02f, 0.025f, 0.04f, left * 0.6f), new Color(0.02f, 0.025f, 0.04f, 0) },
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
