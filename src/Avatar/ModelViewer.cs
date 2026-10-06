using System.Reflection;
using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>
/// Interactive viewer for the procedurally built models, one at a time on a plain backdrop
/// (docs/notes/avatar/model-viewer.md).
///
/// <para>
/// <c>godot --path . -- --models</c>: Left/Right (A/D, D-pad) step through a category,
/// Up/Down (W/S, D-pad) switch category, drag orbits, wheel zooms.
/// <c>--models,&lt;dir&gt;</c> writes one PNG per model to that directory, prints the builder
/// classes nothing shows, and quits with a RESULT line.
/// </para>
/// <para>
/// The list is not written here: every <see cref="ShowcaseAttribute"/> method in the assembly is
/// one entry (or a set), and untagged parameterless static mesh builders land in "Unlisted".
/// </para>
/// </summary>
public partial class ModelViewer : Node3D
{
    private sealed record Entry(string Category, string Name, Func<Node3D> Make);

    private const string Unlisted = "Unlisted";

    private List<(string Name, List<Entry> Entries)> _categories = new();
    private int _category, _index;
    private Node3D? _model;
    private Camera3D _camera = null!;
    private Label _label = null!;
    private Vector3 _target;
    private float _yaw = 0.6f, _pitch = -0.25f, _distance = 5f;
    private string? _shots;

    public static bool Requested() => CmdArgs.FlagWithShot("--models").Requested;

    public override void _Ready()
    {
        _shots = CmdArgs.FlagWithShot("--models").Shot;
        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(Mathf.DegToRad(-42), Mathf.DegToRad(-35), 0),
            LightEnergy = 1.1f,
        });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.44f, 0.50f, 0.56f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.62f),
                AmbientLightEnergy = 0.85f,
            },
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(400, 400) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.38f, 0.31f) },
        });

        _camera = new Camera3D { Fov = 40, Current = true, Far = 2000 };
        AddChild(_camera);

        var ui = new CanvasLayer();
        _label = new Label { Position = new Vector2(12, 8) };
        ui.AddChild(_label);
        AddChild(ui);

        _categories = Discover()
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key == Unlisted ? 1 : 0).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()))
            .ToList();

        if (_shots != null) _ = ShootAll();
        else Display(0, 0);
    }

    // ---- discovery -------------------------------------------------------------------------

    private static IEnumerable<Entry> Discover()
    {
        var tagged = new HashSet<MethodInfo>();
        foreach (var method in StaticMethods())
            foreach (var tag in method.GetCustomAttributes<ShowcaseAttribute>())
            {
                tagged.Add(method);
                if (method.GetParameters().Length != 0)
                {
                    GD.PushError($"[models] [Showcase] on {Where(method)}: it must take no parameters");
                    continue;
                }
                foreach (var entry in FromMethod(method, tag))
                    yield return entry;
            }

        // untagged parameterless mesh builders: a new model shows before anyone tags it
        foreach (var method in StaticMethods())
            if (!tagged.Contains(method) && IsBareMeshBuilder(method))
                yield return new Entry(Unlisted, Where(method), () => Shaded((Mesh)method.Invoke(null, null)!));
    }

    private static bool IsBareMeshBuilder(MethodInfo m) =>
        m.GetParameters().Length == 0 && !m.IsSpecialName && typeof(Mesh).IsAssignableFrom(m.ReturnType);

    private static IEnumerable<Entry> FromMethod(MethodInfo method, ShowcaseAttribute tag)
    {
        string name = tag.Name ?? method.Name;
        var type = method.ReturnType;
        if (typeof(Mesh).IsAssignableFrom(type))
            yield return new Entry(tag.Category, name, () => Shaded((Mesh)method.Invoke(null, null)!, tag.Figure));
        else if (typeof(Node3D).IsAssignableFrom(type))
            yield return new Entry(tag.Category, name, () => (Node3D)method.Invoke(null, null)!);
        else if (typeof(IEnumerable<(string, Func<Node3D>)>).IsAssignableFrom(type))
        {
            var set = (IEnumerable<(string Name, Func<Node3D> Make)>)method.Invoke(null, null)!;
            foreach (var (variant, make) in set)
                yield return new Entry(tag.Category, tag.Name == null ? variant : $"{tag.Name} - {variant}", make);
        }
        else
            GD.PushError($"[models] [Showcase] on {Where(method)}: returns {type.Name}, not a Mesh, Node3D or variant set");
    }

    private static IEnumerable<MethodInfo> StaticMethods() =>
        typeof(ModelViewer).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("UnitSport", StringComparison.Ordinal) == true)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.ContainsGenericParameters)
                .OrderBy(m => m.MetadataToken));

    private static string Where(MethodInfo m) => $"{m.DeclaringType!.Name}.{m.Name}";

    private static Type Outermost(Type t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t;
    }

    /// <summary>
    /// Builder classes (a public static method returning a mesh, or named *MeshBuilder / *Meshes)
    /// with no entry in the viewer at all: where the next model to tag probably is.
    /// </summary>
    private static List<string> Uncovered()
    {
        var methods = StaticMethods().ToList();
        var covered = methods
            .Where(m => m.GetCustomAttributes<ShowcaseAttribute>().Any() || IsBareMeshBuilder(m))
            .Select(m => Outermost(m.DeclaringType!)).ToHashSet();
        return methods
            .Where(m => m.IsPublic && (typeof(Mesh).IsAssignableFrom(m.ReturnType)
                || m.DeclaringType!.Name.EndsWith("MeshBuilder", StringComparison.Ordinal)
                || m.DeclaringType!.Name.EndsWith("Meshes", StringComparison.Ordinal)))
            .Select(m => Outermost(m.DeclaringType!))
            .Where(t => !covered.Contains(t))
            .Select(t => t.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    // ---- display ---------------------------------------------------------------------------

    private void Display(int category, int index)
    {
        if (_categories.Count == 0)
        {
            _label.Text = "No [Showcase] models found";
            return;
        }
        _category = Wrap(category, _categories.Count);
        var entries = _categories[_category].Entries;
        _index = Wrap(index, entries.Count);
        var entry = entries[_index];

        _model?.QueueFree();
        _model = null;
        string error = "";
        try
        {
            _model = entry.Make();
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
            error = $"\nFAILED: {inner.GetType().Name}: {inner.Message}";
            GD.PushError($"[models] {entry.Category} / {entry.Name}: {inner}");
        }

        var box = new Aabb(Vector3.Zero, Vector3.One);
        if (_model != null)
        {
            AddChild(_model);   // some models build their meshes in _Ready, so frame after adding
            box = Bounds(_model) ?? box;
        }
        _target = box.GetCenter();
        _distance = Mathf.Max(box.Size.Length() * 1.4f, 0.3f);

        _label.Text = $"{_categories[_category].Name}  ({_category + 1}/{_categories.Count})   ›   " +
                      $"{entry.Name}  ({_index + 1}/{entries.Count})   " +
                      $"{box.Size.X:0.00} x {box.Size.Y:0.00} x {box.Size.Z:0.00} m{error}\n" +
                      "Left/Right: model   Up/Down: category   drag: orbit   wheel: zoom";
    }

    private static Aabb? Bounds(Node3D root)
    {
        Aabb? box = null;
        var inverse = root.GlobalTransform.AffineInverse();
        foreach (var node in root.FindChildren("*", nameof(VisualInstance3D), true, false).Prepend(root))
            if (node is VisualInstance3D vi and not Light3D)
            {
                var b = inverse * vi.GlobalTransform * vi.GetAabb();
                box = box is { } a ? a.Merge(b) : b;
            }
        return box;
    }

    private static int Wrap(int i, int n) => (i % n + n) % n;

    private async Task ShootAll()
    {
        DirAccess.MakeDirRecursiveAbsolute(_shots!);
        int count = 0, failed = 0;
        for (int c = 0; c < _categories.Count; c++)
            for (int i = 0; i < _categories[c].Entries.Count; i++)
            {
                Display(c, i);
                if (_model == null) failed++;
                // two frames: one for _Ready-built meshes, one to render them
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var entry = _categories[c].Entries[i];
                string file = $"{_shots}/{c:00}-{i:000}-{Slug(entry.Category)}-{Slug(entry.Name)}.png";
                GetViewport().GetTexture().GetImage().SavePng(file);
                count++;
            }
        foreach (var name in Uncovered())
            GD.Print($"[models] no viewer entry: {name}");
        GD.Print($"[models] wrote {count} models in {_categories.Count} categories to {_shots}");
        GD.Print(failed == 0 ? "[models] RESULT: ok" : $"[models] RESULT: FAILED ({failed} models threw)");
        GetTree().Quit(failed == 0 ? 0 : 1);
    }

    private static string Slug(string s) =>
        new string(s.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_').ToArray());

    // ---- input and camera --------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (_shots != null) return;
        switch (e)
        {
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Right or Key.D:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadRight }:
                Display(_category, _index + 1); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Left or Key.A:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadLeft }:
                Display(_category, _index - 1); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Down or Key.S:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadDown }:
                Display(_category + 1, 0); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Up or Key.W:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadUp }:
                Display(_category - 1, 0); break;
            case InputEventMouseMotion m when (m.ButtonMask & MouseButtonMask.Left) != 0:
                _yaw -= m.Relative.X * 0.01f;
                _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.01f, -1.5f, 1.5f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _distance *= 0.9f; break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _distance *= 1.1f; break;
        }
    }

    public override void _Process(double delta)
    {
        var offset = new Vector3(0, 0, _distance).Rotated(Vector3.Right, _pitch).Rotated(Vector3.Up, _yaw);
        _camera.Position = _target + offset;
        _camera.Near = Mathf.Max(_distance * 0.002f, 0.01f);
        // built by hand rather than LookAt: right = forward x up, see the handedness gotcha
        var forward = -offset.Normalized();
        var right = forward.Cross(Vector3.Up).Normalized();
        _camera.Basis = new Basis(right, right.Cross(forward), -forward);
    }

    // ---- helpers for [Showcase] factories ----------------------------------------------------

    /// <summary>
    /// A mesh as a node: surfaces that carry their own material keep it; otherwise the shared
    /// vertex-colour material, or the figure material (faces, clothes' finishes).
    /// </summary>
    public static MeshInstance3D Shaded(Mesh mesh, bool figure = false)
    {
        var node = new MeshInstance3D { Mesh = mesh };
        bool ownMaterials = mesh.GetSurfaceCount() > 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            ownMaterials &= mesh.SurfaceGetMaterial(s) != null;
        if (!ownMaterials)
            node.MaterialOverride = figure ? HumanMeshBuilder.FigureMaterial() : HumanMeshBuilder.Material();
        return node;
    }
}
