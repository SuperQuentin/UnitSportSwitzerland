using Godot;

namespace UnitSport.Gpx;

/// <summary>
/// One simulated lens: how it bends the frame, and how wide it sees.
///
/// <para>
/// The two halves have to move together. Distortion alone cannot widen a picture — it only
/// curves the one already drawn — so a "fisheye" made of curvature with no extra field of view
/// looks like a warped photograph rather than a wide lens. <see cref="FovBias"/> is what
/// actually opens the angle; the shader is what makes the straight lines bend.
/// </para>
/// </summary>
public readonly record struct LensProfile(
    string Name,
    float K1,
    float K2,
    float Chromatic,
    float Vignette,
    Vector2 WarpAxis,
    float FovBias)
{
    public bool IsIdentity => K1 == 0 && K2 == 0 && Chromatic == 0 && Vignette == 0;

    public static readonly LensProfile[] All =
    {
        new("None", 0f, 0f, 0f, 0f, Vector2.One, 1f),

        // The action-cam look: strong barrel, a wide field, and enough falloff to sell the
        // small front element.
        new("Action cam", 0.28f, 0.06f, 0.004f, 0.35f, Vector2.One, 1.25f),

        new("Wide", 0.15f, 0.02f, 0.002f, 0.20f, Vector2.One, 1.15f),

        // Anamorphic squeezes horizontally only, which is why the warp axis is nearly flat in
        // y — the vertical geometry of the frame is left alone.
        new("Anamorphic", -0.06f, 0f, 0.006f, 0.40f, new Vector2(1f, 0.15f), 1f),

        new("Vintage", 0.10f, 0.03f, 0.010f, 0.55f, Vector2.One, 0.95f),
    };

    /// <summary>
    /// The lens in force, read by <c>ShotContext.Place</c> and by the non-cinema camera modes.
    ///
    /// <para>
    /// Static because it is a property of the viewer, like the render resolution — every shot
    /// writes <c>Camera.Fov</c> itself, so threading it through eleven shot classes would only
    /// mean each new one had to remember.
    /// </para>
    /// </summary>
    public static LensProfile Current { get; set; } = All[0];
}

/// <summary>
/// Applies <see cref="LensProfile.Current"/> to the finished frame.
///
/// <para>
/// It sits on its own <see cref="CanvasLayer"/> BELOW the HUD's, so the controls and the
/// leaderboard are never bent along with the world — a distorted scrubber is unusable. Because
/// it draws into the root viewport rather than a side one, <see cref="VideoExporter"/>'s frame
/// grab picks it up with no extra work.
/// </para>
/// </summary>
public partial class LensLayer : CanvasLayer
{
    private ColorRect _rect = null!;
    private ShaderMaterial _material = null!;
    private int _index;

    public static LensLayer Create() => new() { Name = "LensLayer", Layer = 5 };

    public LensProfile Profile => LensProfile.All[_index];

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/lens.gdshader") };

        _rect = new ColorRect
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            Material = _material,
            // it covers the screen; without this it would swallow every click meant for the HUD
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_rect);

        Apply();
    }

    /// <summary>Steps to the next lens and returns it, for the button that asked.</summary>
    public LensProfile Cycle()
    {
        _index = (_index + 1) % LensProfile.All.Length;
        Apply();
        return Profile;
    }

    /// <summary>Picks a lens by index, wrapping. Used by <c>--lens</c>.</summary>
    public LensProfile Select(int index)
    {
        _index = ((index % LensProfile.All.Length) + LensProfile.All.Length) % LensProfile.All.Length;
        Apply();
        return Profile;
    }

    public void Reset()
    {
        _index = 0;
        Apply();
    }

    /// <summary>
    /// Pushes the profile to the shader, and hides the whole layer when there is nothing to do.
    /// A full-screen pass that samples the frame and writes it back unchanged is not free.
    /// </summary>
    private void Apply()
    {
        var lens = Profile;
        LensProfile.Current = lens;

        _rect.Visible = !lens.IsIdentity;
        if (!_rect.Visible) return;

        _material.SetShaderParameter("k1", lens.K1);
        _material.SetShaderParameter("k2", lens.K2);
        _material.SetShaderParameter("chromatic", lens.Chromatic);
        _material.SetShaderParameter("vignette", lens.Vignette);
        _material.SetShaderParameter("warp_axis", lens.WarpAxis);
        RefreshAspect();
    }

    public override void _Process(double _)
    {
        if (_rect.Visible) RefreshAspect();
    }

    /// <summary>
    /// The distortion is circular, so it needs the frame's real shape. The game renders at a
    /// fixed internal size and Godot stretches that to the window ("viewport" stretch mode), but
    /// aspect = "expand" means a non-16:9 window genuinely gets a wider or taller view — so this
    /// is read from the viewport rather than assumed.
    /// </summary>
    private void RefreshAspect()
    {
        var size = GetViewport().GetVisibleRect().Size;
        if (size.Y > 0) _material.SetShaderParameter("aspect", size.X / size.Y);
    }
}
