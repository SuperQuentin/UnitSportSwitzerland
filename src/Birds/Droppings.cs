using Godot;

namespace UnitSport.Birds;

/// <summary>
/// Pigeon droppings on a client (#143), cosmetic only. The authority (the server online) decides when
/// one falls and on whom (<see cref="BirdLife.Dropping"/>); this node draws it: a white speck falling
/// under gravity, and where it lands a splat that shrinks away after <see cref="SplatLife"/> s. With
/// a victim it homes onto that player's head and sticks there (everyone sees it on them); the victim
/// gets a splat across the screen as well. Without one it lands on whatever the physics ray meets: the
/// ground, a roof, a car (the splat then rides on the car).
/// </summary>
public partial class Droppings : Node3D
{
    private const float SplatLife = 40f, OnPlayerLife = 25f, FallLimit = 8f;

    private sealed class Falling
    {
        public required MeshInstance3D Node;
        public Vector3 Velocity;
        public Node3D? Victim;
        public Action? OnHit;
        public float Age;
    }

    private readonly List<Falling> _falling = new();
    private static Texture2D? _texture;
    private static StandardMaterial3D? _material;
    private static readonly SphereMesh Speck = new() { Radius = 0.03f, Height = 0.06f, RadialSegments = 6, Rings = 3 };
    private static readonly QuadMesh Flat = new() { Size = new Vector2(0.24f, 0.24f) };
    private static readonly SphereMesh Blob = new() { Radius = 0.06f, Height = 0.05f, RadialSegments = 6, Rings = 3 };

    /// <summary>A white splash with a few runs, drawn once: 32 px, the PS1 look.</summary>
    public static Texture2D Texture
    {
        get
        {
            if (_texture != null) return _texture;
            var img = Image.CreateEmpty(32, 32, false, Image.Format.Rgba8);
            var rng = new Random(143);
            var c = new Vector2(16, 16);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    var d = new Vector2(x, y) - c;
                    float a = Mathf.Atan2(d.Y, d.X);
                    float edge = 7f + 2.5f * Mathf.Sin(a * 5f + 1f) + 1.5f * Mathf.Sin(a * 3f);
                    if (d.Length() < edge)
                        img.SetPixel(x, y, d.Length() < 3f ? new Color(0.35f, 0.32f, 0.28f) : new Color(0.93f, 0.93f, 0.9f));
                }
            for (int k = 0; k < 6; k++)
            {
                var dir = Vector2.Right.Rotated(k * Mathf.Tau / 6f + (float)rng.NextDouble());
                var at = c + dir * (10f + 4f * (float)rng.NextDouble());
                img.SetPixel(Mathf.Clamp((int)at.X, 0, 31), Mathf.Clamp((int)at.Y, 0, 31), new Color(0.93f, 0.93f, 0.9f));
            }
            return _texture = ImageTexture.CreateFromImage(img);
        }
    }

    private static StandardMaterial3D Material => _material ??= new StandardMaterial3D
    {
        AlbedoTexture = Texture,
        Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static readonly StandardMaterial3D White = new() { AlbedoColor = new Color(0.93f, 0.93f, 0.9f) };

    /// <summary>Starts one falling. <paramref name="onHit"/> runs when it lands on <paramref name="victim"/>.</summary>
    public void Drop(Vector3 from, Vector3 velocity, Node3D? victim, Action? onHit)
    {
        var node = new MeshInstance3D { Mesh = Speck, MaterialOverride = White };
        AddChild(node);
        node.GlobalPosition = from;
        _falling.Add(new Falling { Node = node, Velocity = velocity, Victim = victim, OnHit = onHit });
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        for (int i = _falling.Count - 1; i >= 0; i--)
        {
            var d = _falling[i];
            d.Age += dt;
            var p = d.Node.GlobalPosition;
            d.Velocity += Vector3.Down * 9.81f * dt;
            d.Velocity *= 1f - 0.3f * dt;
            var next = p + d.Velocity * dt;
            bool done = false;
            if (d.Victim != null && IsInstanceValid(d.Victim))
            {
                // the authority saw it fall on this player: it lands on them, wherever they stepped since
                var head = d.Victim.GlobalPosition + Vector3.Up * 1.65f;
                next = next with { X = Mathf.Lerp(next.X, head.X, 1f - Mathf.Exp(-6f * dt)), Z = Mathf.Lerp(next.Z, head.Z, 1f - Mathf.Exp(-6f * dt)) };
                if (next.Y <= head.Y)
                {
                    OnPlayer(d.Victim);
                    d.OnHit?.Invoke();
                    done = true;
                }
            }
            else
            {
                var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(p, next));
                // not on a player nobody was told about: the authority picked who gets hit
                if (hit.Count > 0 && hit["collider"].AsGodotObject() is not CharacterBody3D)
                {
                    Splat(hit["position"].AsVector3(), hit["normal"].AsVector3(), hit["collider"].AsGodotObject() as Node3D);
                    done = true;
                }
            }
            if (!done && d.Age < FallLimit) { d.Node.GlobalPosition = next; continue; }
            d.Node.QueueFree();
            _falling.RemoveAt(i);
        }
    }

    /// <summary>A splat flat on the surface, 1 cm off it; on a moving body (a car) it rides along.</summary>
    private void Splat(Vector3 at, Vector3 normal, Node3D? on)
    {
        var splat = new MeshInstance3D { Mesh = Flat, MaterialOverride = Material };
        Node parent = on is StaticBody3D or null ? this : on;
        parent.AddChild(splat);
        var up = Mathf.Abs(normal.Dot(Vector3.Up)) > 0.9f ? Vector3.Forward : Vector3.Up;
        splat.GlobalTransform = new Transform3D(Basis.LookingAt(-normal, up).Rotated(normal, (float)GD.RandRange(0, Mathf.Tau)), at + normal * 0.01f);
        Fade(splat, SplatLife);
    }

    private void OnPlayer(Node3D player)
    {
        var blob = new MeshInstance3D { Mesh = Blob, MaterialOverride = White, Position = new Vector3((float)GD.RandRange(-0.12, 0.12), 1.62f, (float)GD.RandRange(-0.08, 0.08)) };
        player.AddChild(blob);
        Fade(blob, OnPlayerLife);
    }

    /// <summary>Shrinks to nothing over the last five seconds, then goes.</summary>
    private void Fade(Node3D node, float life)
    {
        var tween = node.CreateTween();
        tween.TweenInterval(life - 5f);
        tween.TweenProperty(node, "scale", Vector3.One * 0.01f, 5f);
        tween.TweenCallback(Callable.From(node.QueueFree));
    }

    /// <summary>The victim's own view: a splat across the screen that fades in a few seconds.</summary>
    public void OnScreen()
    {
        var layer = new CanvasLayer { Layer = 5 };
        var size = GetViewport().GetVisibleRect().Size;
        float side = size.Y * 0.45f;
        var rect = new TextureRect
        {
            Texture = Texture, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
            Size = new Vector2(side, side),
            Position = new Vector2((float)GD.RandRange(0.15, 0.6) * size.X, (float)GD.RandRange(0.0, 0.25) * size.Y),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layer.AddChild(rect);
        AddChild(layer);
        var tween = rect.CreateTween();
        tween.TweenInterval(2.5f);
        tween.TweenProperty(rect, "modulate:a", 0f, 2.5f);
        tween.TweenCallback(Callable.From(layer.QueueFree));
    }
}
