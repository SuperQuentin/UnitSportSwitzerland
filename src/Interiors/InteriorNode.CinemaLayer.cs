using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The playing hall's screen as a compositor layer in a headset (#1319, the owner's: "the movie screen at full resolution
/// in VR, in another layer, like the menus"). The mesh screen is drawn into each eye's picture and resampled again by
/// the lens correction; a layer is sampled by the headset's compositor straight from the frame, at the headset's own
/// resolution. A SubViewport the decoded frame's size draws it through the hall's display
/// (<c>shaders/cinema_layer.gdshader</c>, the same mapping as the screen's); an <see cref="OpenXRCompositionLayerQuad"/>,
/// a child of the tracking origin as OpenXR wants, holds it where the screen is, behind the 3D picture as
/// <c>XrUi</c>'s panel: the screen's mesh turns into a hole (<c>shaders/cinema_hole.gdshader</c>), so heads, seats, the
/// curtains and hands in front still cover it. 2D films only (a quad layer shows both eyes one picture); the mesh
/// screen without a headset, in <c>--xrsim</c>, with Settings › VR sharp cinema screen off, or where the runtime has
/// no layers.
/// </summary>
public partial class InteriorNode
{
    private OpenXRCompositionLayerQuad? _layer;
    private SubViewport? _layerView;
    private ColorRect? _layerRect;
    private ShaderMaterial? _layerPicture, _layerHole;
    private CinemaScreen? _layerOn;
    /// <summary>The runtime said it has no quad layers: never asked again this run.</summary>
    private static bool _layerUnsupported;

    /// <summary>Behind the menus' panel (<c>XrUi</c>, -2) and the watch's wheel (-1).</summary>
    private const int LayerSort = -3;

    /// <summary>Whether a hall's video goes on a layer now (a 2D film, in a real headset, the setting on).</summary>
    private static bool LayerWanted(bool stereo) =>
        !stereo && !_layerUnsupported && XR.XrSession.Active && !XR.XrSession.Simulated
        && Core.GameSettings.Current.VrCinemaLayer && XR.XrSession.Rig?.Origin != null;

    /// <summary>
    /// Per new frame (<see cref="ShowVideo"/>): the layer made or taken down as wanted, its viewport the frame's size, its
    /// display set on change. True while the screen is on the layer.
    /// </summary>
    private bool FeedLayer(CinemaScreen s, bool stereo, Vector2I size, bool displayChanged, int display, float cw, float ch)
    {
        if (!LayerWanted(stereo)) { DropLayer(); return false; }
        if (_layer == null)
        {
            if (!MakeLayer(s, size)) return false;
            displayChanged = true;
        }
        if (_layerView!.Size != size) { _layerView.Size = size; _layerRect!.Size = size; }
        if (displayChanged) SetDisplay(_layerPicture!, display, cw, ch);
        return true;
    }

    private bool MakeLayer(CinemaScreen s, Vector2I size)
    {
        _layerPicture ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cinema_layer.gdshader") };
        _layerHole ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cinema_hole.gdshader") };
        _layerPicture.SetShaderParameter(GPicture, _videoTexture);
        _layerView = new SubViewport
        {
            Name = "CinemaLayerView",
            Size = size,
            Disable3D = true,
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _layerRect = new ColorRect { Material = _layerPicture, Size = size, Color = Colors.White };
        _layerView.AddChild(_layerRect);
        AddChild(_layerView);
        _layer = new OpenXRCompositionLayerQuad
        {
            Name = "CinemaLayer",
            LayerViewport = _layerView,
            QuadSize = ((QuadMesh)s.Picture.Mesh).Size,
            SortOrder = LayerSort,
            AlphaBlend = false,
        };
        XR.XrSession.Rig!.Origin.AddChild(_layer);
        if (!_layer.IsNativelySupported())
        {
            GD.Print("[cinema] this headset's runtime has no quad layers: the screen stays a mesh");
            _layerUnsupported = true;
            DropLayer();
            return false;
        }
        _layerOn = s;
        s.Picture.MaterialOverride = _layerHole;
        HoldLayer();
        GD.Print($"[cinema] hall {s.No + 1}'s screen on a compositor layer ({size.X}x{size.Y})");
        return true;
    }

    /// <summary>
    /// Per frame (<see cref="CinemaBooths"/>): the layer where the screen is. It hangs from the tracking origin, which moves
    /// with the player, so it is placed again every frame; nothing else changes.
    /// </summary>
    public void HoldLayer()
    {
        if (_layer == null || _layerOn == null) return;
        _layer.GlobalTransform = _layerOn.Picture.GlobalTransform;
        _layer.Visible = _layerOn.Picture.Visible;
    }

    /// <summary>The layer and its viewport gone, the screen's own material back.</summary>
    private void DropLayer()
    {
        if (_layerOn != null && _layerOn.Picture.MaterialOverride == _layerHole) _layerOn.Picture.MaterialOverride = _videoScreen;
        _layerOn = null;
        if (_layer != null && IsInstanceValid(_layer)) _layer.QueueFree();
        _layer = null;
        if (_layerView != null && IsInstanceValid(_layerView)) _layerView.QueueFree();
        _layerView = null;
        _layerRect = null;
    }
}
