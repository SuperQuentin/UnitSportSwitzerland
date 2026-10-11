using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using FaceGenome = UnitSport.Avatar.Face.FaceGenome;

namespace UnitSport.Ui;

/// <summary>
/// Settings → Character (#806, was the inventory's Body row of #394): who your figure is, one
/// option per part (build, face, eyes, skin, hair, its colour) beside a small turning preview.
/// Each change packs <see cref="GameSettings.AppearanceBits"/>, tells the world
/// (<see cref="GameSettings.Changed"/>: <c>ClientWorld.PublishAppearance</c> puts it on your
/// player, which replicates it) and saves that one key, never this run's command-line overrides.
/// </summary>
public static class CharacterPage
{
    private static readonly string[] Parts = { "Build", "Face", "Eyes", "Skin", "Hair", "Colour" };

    public static void Fill(VBoxContainer rows)
    {
        var split = UiKit.HBox(18);
        rows.AddChild(split);
        var left = UiKit.VBox(0);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        split.AddChild(left);
        var preview = new FigurePreview();
        split.AddChild(preview);

        var a = Current(rows);
        var options = new OptionButton[Parts.Length];
        for (int i = 0; i < Parts.Length; i++)
        {
            int part = i;
            options[i] = UiKit.OptionRow(left, Parts[i], Names(part), Index(a, part), chosen =>
            {
                var next = With(Current(rows), part, chosen);
                GameSettings.Current.AppearanceBits = next.Pack();
                preview.ShowFigure(next);
            }, i == 0 ? "Everyone online sees the figure you choose" : null, Commit);
        }
        Tint(options, a);
        preview.Shown = () => Current(rows);
        // the network id (and with it the figure of someone who never chose) changes on joining a server
        rows.VisibilityChanged += () =>
        {
            if (!rows.IsVisibleInTree()) return;
            var now = Current(rows);
            for (int i = 0; i < options.Length; i++) options[i].Selected = Index(now, i);
            Tint(options, now);
        };
        foreach (var o in options) o.ItemSelected += _ => Tint(options, Current(rows));
    }

    private static void Commit()
    {
        GameSettings.NotifyChanged();
        GameSettings.SaveOnly(nameof(GameSettings.AppearanceBits), GameSettings.Current.AppearanceBits);
    }

    /// <summary>What the player looks like now: their choice, or the figure their network id gives them.</summary>
    private static Appearance Current(Node any) =>
        Appearance.Unpack(GameSettings.Current.AppearanceBits)
        ?? Appearance.For(any.IsInsideTree() && any.Multiplayer.HasMultiplayerPeer() ? any.Multiplayer.GetUniqueId() : 1);

    private static string[] Names(int part) => part switch
    {
        0 => Enum.GetNames<BodyBuild>(),
        1 => Enumerable.Range(0, FaceGenome.PresetCount).Select(i => Capital(FaceGenome.PresetName(i))).ToArray(),
        2 => Appearance.EyeNames,
        3 => Enumerable.Range(1, Appearance.SkinTones.Length).Select(i => $"Tone {i}").ToArray(),
        4 => Enum.GetNames<HairStyle>(),
        _ => Appearance.HairColourNames,
    };

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static int Index(Appearance a, int part) => part switch
    {
        0 => (int)a.Build,
        1 => Mathf.PosMod(a.Face, FaceGenome.PresetCount),
        2 => Mathf.PosMod(a.Eyes, Appearance.EyeColours.Length),
        3 => Mathf.PosMod(a.Skin, Appearance.SkinTones.Length),
        4 => (int)a.Hair,
        _ => Mathf.PosMod(a.HairColour, Appearance.HairColours.Length),
    };

    private static Appearance With(Appearance a, int part, int i) => part switch
    {
        0 => a with { Build = (BodyBuild)i },
        1 => a with { Face = i },
        2 => a with { Eyes = i },
        3 => a with { Skin = i },
        4 => a with { Hair = (HairStyle)i },
        _ => a with { HairColour = i },
    };

    /// <summary>The colour rows show their colour.</summary>
    private static void Tint(OptionButton[] options, Appearance a)
    {
        options[2].AddThemeColorOverride("font_color", a.EyeColour.Lerp(Colors.White, 0.35f));   // black eyes stay readable
        options[3].AddThemeColorOverride("font_color", a.SkinColour.Lerp(Colors.White, 0.2f));
        options[5].AddThemeColorOverride("font_color", a.HairTint.Lerp(Colors.White, 0.25f));
    }
}

/// <summary>
/// The figure on a turntable in its own small world: a SubViewport built while the Character tab
/// is on screen and freed as soon as it is not, so the menu costs nothing elsewhere. It draws with
/// the shared figure material as it is (no face_idle change, unlike <see cref="AvatarPreview"/>).
/// </summary>
public partial class FigurePreview : SubViewportContainer
{
    private const float TurnSpeed = 0.6f;   // rad/s
    private static readonly Vector2I Pixels = new(250, 340);

    /// <summary>The figure to show when the preview (re)appears.</summary>
    public Func<Appearance>? Shown;

    private SubViewport? _viewport;
    private MeshInstance3D? _figure;

    public FigurePreview()
    {
        Stretch = true;
        CustomMinimumSize = Pixels;
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        MouseFilter = MouseFilterEnum.Ignore;
        FocusMode = FocusModeEnum.None;
        VisibilityChanged += Sync;
        TreeEntered += Sync;
        TreeExiting += Free3D;
    }

    private void Sync()
    {
        if (IsInsideTree() && IsVisibleInTree()) { if (_viewport == null) Build(); }
        else Free3D();
    }

    private void Build()
    {
        _viewport = new SubViewport
        {
            Size = Pixels, OwnWorld3D = true, TransparentBg = true,
            Msaa3D = Viewport.Msaa.Disabled, RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
        };
        _viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.62f, 0.66f),
                AmbientLightEnergy = 0.9f,
            },
        });
        _viewport.AddChild(new DirectionalLight3D { Rotation = new Vector3(Mathf.DegToRad(-35), Mathf.DegToRad(-30), 0), LightEnergy = 1.1f });
        var camera = new Camera3D { Position = new Vector3(0, 1.05f, 3.4f), Fov = 36, Current = true };
        _viewport.AddChild(camera);
        camera.RotationDegrees = new Vector3(-4, 0, 0);
        _figure = new MeshInstance3D { MaterialOverride = HumanMeshBuilder.FigureMaterial(), Rotation = new Vector3(0, Mathf.Pi, 0) };
        _viewport.AddChild(_figure);
        AddChild(_viewport);
        if (Shown?.Invoke() is { } a) ShowFigure(a);
        SetProcess(true);
    }

    private void Free3D()
    {
        SetProcess(false);
        if (_viewport == null) return;
        _viewport.QueueFree();
        _viewport = null;
        _figure = null;
    }

    /// <summary>Redraws the figure (on a change only: a mesh build is not per frame).</summary>
    public void ShowFigure(Appearance a)
    {
        if (_figure == null) return;
        _figure.Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(1).With(a));
    }

    public override void _Ready() => SetProcess(_viewport != null);

    public override void _Process(double delta)
    {
        if (_figure != null) _figure.RotateY((float)delta * TurnSpeed);
    }
}
