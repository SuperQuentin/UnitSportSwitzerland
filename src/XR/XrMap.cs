using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// The hand-held map (#439): a relief of the real terrain round the player, held in the left hand,
/// built from the heights the client already has. The right hand points at a spot on it and the
/// trigger travels there, through the place search's own teleport (<see cref="Core.Teleporter"/>);
/// in a Battle Royale match it only shows, as M does there.
/// </summary>
internal sealed partial class XrMap : Node3D
{
    /// <summary>The ground it covers, m, its size in the hand, m, and the samples a side.</summary>
    private const float Span = 3000f, Size = 0.4f;
    private const int Grid = 32;
    /// <summary>Height on the model per metre of real height: the Alps exaggerated twice.</summary>
    private const float Relief = Size / Span * 2f;

    private readonly XRController3D _pointer;
    private MeshInstance3D _relief = null!, _dot = null!;
    private Vector2? _aim;
    private bool _triggerWas = true;

    public XrMap(XRController3D pointer)
    {
        _pointer = pointer;
        Name = "HandMap";
        Visible = false;
    }

    /// <summary>The right hand points at the map: its trigger is the map's, not a use.</summary>
    public bool Pointing => Visible && _aim != null;

    public override void _Ready()
    {
        // above the open left palm, level
        Transform = new Transform3D(Basis.Identity, new Vector3(0f, 0.08f, -0.12f));
        _relief = new MeshInstance3D { Layers = XrSession.HeadsetOnlyLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_relief);
        AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.008f, Height = 0.03f, RadialSegments = 8 },
            Position = new Vector3(0f, 0.05f, 0f),
            Rotation = new Vector3(Mathf.Pi, 0f, 0f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.15f, 0.12f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
        });
        _dot = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.006f, Height = 0.012f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.8f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            Layers = XrSession.HeadsetOnlyLayer,
            Visible = false,
        };
        AddChild(_dot);
    }

    /// <summary>Opens the map round <paramref name="player"/> (built now, from the heights the client has), or shuts it.</summary>
    public void Toggle(FootPlayer? player)
    {
        if (Visible || player?.Terrain == null)
        {
            Visible = false;
            return;
        }
        _relief.Mesh = Build(player);
        _triggerWas = true;
        Visible = true;
    }

    private static ArrayMesh Build(FootPlayer player)
    {
        var centre = player.GlobalPosition;
        var heights = new float[Grid + 1, Grid + 1];
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i <= Grid; i++)
            for (int j = 0; j <= Grid; j++)
            {
                var at = centre + new Vector3((i / (float)Grid - 0.5f) * Span, 0f, (j / (float)Grid - 0.5f) * Span);
                float h = player.Terrain!.TryGetHeight(at, out float g) ? g : float.NaN;
                heights[i, j] = h;
                if (!float.IsNaN(h)) { low = Mathf.Min(low, h); high = Mathf.Max(high, h); }
            }
        if (low > high) low = high = centre.Y;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        Vector3 P(int i, int j)
        {
            float h = float.IsNaN(heights[i, j]) ? low : heights[i, j];
            return new Vector3((i / (float)Grid - 0.5f) * Size, (h - low) * Relief, (j / (float)Grid - 0.5f) * Size);
        }
        Color C(int i, int j)
        {
            if (float.IsNaN(heights[i, j])) return new Color(0.3f, 0.3f, 0.32f);
            float t = (heights[i, j] - low) / Mathf.Max(1f, high - low);
            // valley green, rock brown, snow white above 2600 m
            return heights[i, j] > 2600f ? new Color(0.95f, 0.95f, 0.97f)
                : new Color(0.32f, 0.55f, 0.25f).Lerp(new Color(0.55f, 0.45f, 0.35f), t);
        }
        for (int i = 0; i < Grid; i++)
            for (int j = 0; j < Grid; j++)
                foreach (var (a, b) in new[] { ((i, j), (i + 1, j)), ((i + 1, j), (i + 1, j + 1)) })
                {
                    // two triangles a cell, flat coloured by their first corner: the PS1 look
                    var (p0, p1, p2) = a == (i, j)
                        ? (P(i, j), P(i + 1, j + 1), P(i + 1, j))
                        : (P(i, j), P(i, j + 1), P(i + 1, j + 1));
                    var col = C(i, j);
                    foreach (var p in new[] { p0, p1, p2 })
                    {
                        st.SetColor(col);
                        st.AddVertex(p);
                    }
                }
        st.GenerateNormals();
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled });
        return mesh;
    }

    /// <summary>Once a frame while open: the right hand's ray on the map, and its trigger to travel.</summary>
    public void Update(FootPlayer? player)
    {
        if (!Visible) return;
        if (player is not { Ride: RideKind.OnFoot, RidingWith: 0 } || Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Visible = false;
            return;
        }
        // the ray against the map's plane, in the map's frame
        var inv = GlobalTransform.AffineInverse();
        var from = inv * _pointer.GlobalPosition;
        var dir = (inv.Basis * -_pointer.GlobalBasis.Z).Normalized();
        _aim = null;
        if (Mathf.Abs(dir.Y) > 1e-3f)
        {
            float t = -from.Y / dir.Y;
            var hit = from + dir * t;
            if (t > 0f && Mathf.Abs(hit.X) <= Size / 2 && Mathf.Abs(hit.Z) <= Size / 2)
                _aim = new Vector2(hit.X, hit.Z);
        }
        _dot.Visible = _aim != null;
        if (_aim is { } a) _dot.Position = new Vector3(a.X, 0.02f, a.Y);

        bool trigger = _pointer.GetFloat("trigger") > 0.6f;
        if (trigger && !_triggerWas && _aim is { } go && BattleRoyale.BrManager.Instance?.InMatch != true
            && Core.Teleporter.Instance is { } teleporter)
        {
            // map metres to the ground: east is the map's +X, north its −Z, as the world's
            var g = player.Global;
            double e = g.E + go.X / Size * Span, n = g.N - go.Y / Size * Span;
            teleporter.TeleportTo(e, n);
            Visible = false;
        }
        _triggerWas = trigger;
    }
}
