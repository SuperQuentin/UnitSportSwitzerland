using Godot;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// A faint "generated terrain" line in the corner while the ground under the camera is generated
/// rather than surveyed (<see cref="FallbackChunkSource"/>). Only a note, not a tint on the world:
/// the blend exists so the border cannot be seen, and marking the ground would draw it back in.
/// Hidden during a video export.
/// </summary>
public partial class GeneratedTerrainNote : CanvasLayer
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private Label _label = null!;
    private double _sinceCheck = double.MaxValue;

    public GeneratedTerrainNote(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        Name = "GeneratedTerrainNote";
        Layer = 9;   // under the HUD's 10
        _label = new Label
        {
            Text = "generated terrain",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0.55f),
            Visible = false,
        };
        _label.AddThemeFontSizeOverride("font_size", 12);
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
        _label.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        _label.Position = new Vector2(10, GameSettings.BaseHeight - 26);
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        _sinceCheck += delta;
        if (_sinceCheck < 0.5) return;
        _sinceCheck = 0;

        var camera = GetViewport().GetCamera3D();
        _label.Visible = camera != null && !_chunks.OfflineMode && _chunks.Visible
            && _chunks.IsGenerated(_origin.TileAt(camera.GlobalPosition));
    }
}
