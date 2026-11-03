using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A mirror on the wall above a washbasin (#439): a small viewport rendering the room from the
/// viewer's reflection in the glass, shown on a quad, the way the cab mirrors work (#69,
/// <see cref="Avatar.CabMirrors"/>). In VR it is where you see your own figure and real hands.
///
/// <para>
/// Cost: only the nearest mirror within <see cref="Range"/> that the viewer stands in front of
/// renders, every other frame, at <see cref="Pixels"/> pixels high in the PS1 grain; the others show
/// their last picture. Its viewport is made the first time it is the one.
/// </para>
/// <para>
/// A row of mirrors on one wall at one height (a counter of basins, #1285) is a <see cref="Bank"/>: one
/// viewport and one camera frame the whole row, and each glass shows its own slice of that one picture
/// (its UVs), so the whole row is live at the cost of one camera. A mirror alone is a bank of one.
/// </para>
/// </summary>
public partial class WallMirror : Node3D
{
    public static readonly Vector2 Size = new(0.5f, 0.6f);
    private const int Pixels = 96;
    private const float Range = 5f, Far = 40f;
    /// <summary>What it sees: the world and figures (your own first-person body included), not the held-item viewmodel nor door portals' quads.</summary>
    public const uint Cull = 0xFFFFFu & ~Items.HeldItemVisual.ViewmodelLayer & ~DoorPortals.AllQuadLayers
                              | XR.XrSession.SpectatorOnlyLayer;

    /// <summary>Mirrors side by side further apart than this, or a row longer, are banks of their own, m.</summary>
    private const float BankGap = 0.6f, BankSpan = 4.5f;

    private static readonly List<WallMirror> All = new();
    private static WallMirror? _active;
    private static ulong _pickedFrame;

    /// <summary>
    /// Mirrors in one plane, at one height, side by side: the first (the leader) renders for all. <see cref="X0"/>
    /// and <see cref="X1"/> are the row's glass from end to end along the leader's local X.
    /// </summary>
    private sealed class Bank
    {
        public readonly List<WallMirror> Members = new();
        public WallMirror Leader => Members[0];
        public float X0 = -Size.X / 2, X1 = Size.X / 2;
        public SubViewport? Port;
        public Camera3D? Camera;
    }

    private Bank? _bank;
    /// <summary>Where this glass sits along its bank's leader's X, m.</summary>
    private float _along;

    private MeshInstance3D _face = null!;
    private StandardMaterial3D _mat = null!;

    public override void _Ready()
    {
        _mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.55f, 0.62f, 0.68f),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            // a mirror shows the world left for right
            Uv1Scale = new Vector3(-1f, 1f, 1f),
            Uv1Offset = new Vector3(1f, 0f, 0f),
        };
        _face = new MeshInstance3D
        {
            Name = "Glass",
            Mesh = new QuadMesh { Size = Size },
            MaterialOverride = _mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_face);
        if (_bank == null)
        {
            _bank = new Bank();
            _bank.Members.Add(this);
        }
        // its slice of the bank's picture, left for right: the camera's right is the glass's -X
        float span = _bank.X1 - _bank.X0;
        _mat.Uv1Scale = new Vector3(-Size.X / span, 1f, 1f);
        _mat.Uv1Offset = new Vector3((_bank.X1 - _along + Size.X / 2) / span, 0f, 0f);
        if (_bank.Port != null) Show(_bank.Port);
    }

    private void Show(SubViewport port)
    {
        _mat.AlbedoTexture = port.GetTexture();
        _mat.AlbedoColor = new Color(0.86f, 0.88f, 0.92f);
    }

    /// <summary>Every wall mirror in the tree is in this group.</summary>
    public const string Group = "wall_mirrors";

    public override void _EnterTree()
    {
        All.Add(this);
        AddToGroup(Group);
    }

    public override void _ExitTree()
    {
        All.Remove(this);
        if (_active == this) _active = null;
    }

    public override void _Process(double delta)
    {
        if (DisplayServer.GetName() == "headless" || !IsVisibleInTree()) return;
        var eye = GetViewport().GetCamera3D();
        if (eye == null) return;
        ulong frame = Engine.GetProcessFrames();
        if (_pickedFrame != frame)
        {
            _pickedFrame = frame;
            _active = Pick(eye.GlobalPosition)?._bank?.Leader;
        }
        if (_active != this || frame % 2 != 0) return;
        Render(eye.GlobalPosition);
    }

    /// <summary>The nearest mirror in range that the eye is in front of.</summary>
    private static WallMirror? Pick(Vector3 eye)
    {
        WallMirror? best = null;
        float bestD = Range;
        foreach (var m in All)
        {
            if (!m.IsVisibleInTree()) continue;
            var t = m.GlobalTransform;
            float depth = (eye - t.Origin).Dot(t.Basis.Z.Normalized());
            float d = eye.DistanceTo(t.Origin);
            if (depth > 0.05f && d < bestD)
            {
                bestD = d;
                best = m;
            }
        }
        return best;
    }

    /// <summary>The leader renders its whole bank: one picture as wide as the row, every glass its slice of it.</summary>
    private void Render(Vector3 eye)
    {
        var bank = _bank!;
        float span = bank.X1 - bank.X0;
        if (bank.Port == null)
        {
            bank.Port = new SubViewport
            {
                Name = "View",
                Size = new Vector2I(Mathf.RoundToInt(Pixels * span / Size.Y), Pixels),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
            };
            AddChild(bank.Port);
            bank.Camera = new Camera3D { Name = "Camera", CullMask = Cull, Far = Far };
            bank.Port.AddChild(bank.Camera);
            bank.Camera.MakeCurrent();
            foreach (var m in bank.Members)
                if (m.IsNodeReady()) m.Show(bank.Port);
        }
        // the eye's reflection, looking square through the glass with a frustum framing exactly the row,
        // so its near plane is the glass (CabMirrors.Aim)
        var face = _face.GlobalTransform;
        var centre = face.Origin + face.Basis.X.Normalized() * ((bank.X0 + bank.X1) / 2 - _along);
        var normal = face.Basis.Z.Normalized();
        float depth = (eye - centre).Dot(normal);
        if (depth < 0.05f) return;
        var image = eye - 2f * depth * normal;
        var z = -normal;
        var y = (face.Basis.Y - z * face.Basis.Y.Dot(z)).Normalized();
        var basis = new Basis(y.Cross(z), y, z);
        bank.Camera!.GlobalTransform = new Transform3D(basis, image);
        var local = basis.Transposed() * (centre - image);
        bank.Camera.SetFrustum(Size.Y, new Vector2(local.X, local.Y), depth + 0.002f, Far);
        bank.Port.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    /// <summary>A mirror above each washbasin of <paramref name="layout"/>, in the interior's own frame.</summary>
    public static void AddTo(Node3D interior, InteriorLayout layout)
    {
        var made = new List<WallMirror>();
        foreach (var p in layout.Furniture)
        {
            if (p.Type is not (FurnitureType.Sink or FurnitureType.Mirror)) continue;
            // the basin is authored with its back to −Z: the glass on that wall, facing the room;
            // a cabin's mirror (#822) is its own frame, the glass on its face
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            bool framed = p.Type == FurnitureType.Mirror;
            float y = framed ? p.Lift + p.H / 2 : 1.45f, z = framed ? p.D / 2 + 0.012f : -p.D / 2 + 0.012f;
            var at = new Vector3(p.X, layout.FloorY(p.Floor) + y, p.Z) + basis * new Vector3(0f, 0f, z);
            var mirror = new WallMirror { Name = $"Mirror{interior.GetChildCount()}", Transform = new Transform3D(basis, at) };
            made.Add(mirror);
            interior.AddChild(mirror);
        }
        Banks(made);
    }

    /// <summary>
    /// Puts mirrors facing one way, in one plane, at one height, side by side with gaps under <see cref="BankGap"/>,
    /// into banks no longer than <see cref="BankSpan"/>. All are children of one interior, so their transforms compare.
    /// </summary>
    private static void Banks(List<WallMirror> mirrors)
    {
        var rows = mirrors.GroupBy(m =>
        {
            var t = m.Transform;
            var n = t.Basis.Z.Normalized();
            return (Mathf.RoundToInt(n.X * 4), Mathf.RoundToInt(n.Z * 4), Mathf.RoundToInt(t.Origin.Dot(n) * 100), Mathf.RoundToInt(t.Origin.Y * 100));
        });
        foreach (var row in rows)
        {
            var x = row.First().Transform.Basis.X.Normalized();
            Bank? bank = null;
            float first = 0;
            foreach (var m in row.OrderBy(m => m.Transform.Origin.Dot(x)))
            {
                float s = m.Transform.Origin.Dot(x);
                // one row shares one turn, so its leader's X is x: along is measured on it, in order
                float along = s - first;
                if (bank == null || along - Size.X / 2 - bank.X1 > BankGap || along + Size.X / 2 - bank.X0 > BankSpan)
                {
                    bank = new Bank();
                    first = s;
                    along = 0;
                }
                bank.Members.Add(m);
                bank.X0 = Math.Min(bank.X0, along - Size.X / 2);
                bank.X1 = Math.Max(bank.X1, along + Size.X / 2);
                m._bank = bank;
                m._along = along;
            }
        }
    }
}
