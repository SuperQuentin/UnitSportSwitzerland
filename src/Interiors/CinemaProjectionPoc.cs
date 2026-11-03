using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A proof of concept (#1250), in the model viewer only: the picture thrown by a light instead of pasted
/// on the screen. A spot light at the porthole carries the slide as its projector texture (letterboxed to
/// a square, the cone's frame, so 2.39:1 stays 2.39:1) and casts shadows; the screen, the room and a
/// figure walking across the beam are shaded by it alone (<c>shaders/projection_screen.gdshader</c>). The
/// figure wears the picture and leaves its shadow on the screen, as Source games do. Cost: one shadowed
/// spot light per screen showing (a shadow map pass over what it sees), against today's one unshaded quad;
/// the game's cinemas keep the quad until that is weighed.
/// </summary>
public static class CinemaProjectionPoc
{
    private const float ScreenW = 9f, Throw = 16f, FigureLane = 2.2f;

    [Core.Showcase("Interiors", "Cinema projection (POC)")]
    private static Node3D Build()
    {
        var root = new Node3D { Name = "ProjectionPoc" };
        float screenH = ScreenW / 2.39f, mid = 0.9f + screenH / 2;
        var shader = GD.Load<Shader>("res://shaders/projection_screen.gdshader");
        ShaderMaterial Mat(Color c) { var m = new ShaderMaterial { Shader = shader }; m.SetShaderParameter("albedo", c); return m; }

        // the screen, the floor, the side walls: a box of a room, open toward the viewer
        root.AddChild(new MeshInstance3D { Name = "Screen", Mesh = new QuadMesh { Size = new Vector2(ScreenW, screenH) }, MaterialOverride = Mat(new Color(0.92f, 0.92f, 0.90f)), Position = new Vector3(0, mid, 0) });
        root.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(14, Throw + 2) }, MaterialOverride = Mat(new Color(0.35f, 0.08f, 0.10f)), Position = new Vector3(0, 0, Throw / 2) });
        root.AddChild(new MeshInstance3D { Name = "Wall", Mesh = new BoxMesh { Size = new Vector3(14, 6, 0.2f) }, MaterialOverride = Mat(new Color(0.20f, 0.08f, 0.10f)), Position = new Vector3(0, 3, -0.15f) });

        // the projector: the picture as the light's texture, its cone just wider than the screen
        var picture = Letterboxed(InteriorNode.Slide(0));
        var at = new Vector3(0, mid + 0.3f, Throw);
        var spot = new SpotLight3D
        {
            Name = "Projector",
            LightProjector = picture,
            LightEnergy = 9f,
            SpotRange = Throw + 4f,
            SpotAttenuation = 0f,
            // the square texture spans the cone's diameter: half the screen's width at the throw
            SpotAngle = Mathf.RadToDeg(Mathf.Atan(ScreenW / 2 / at.DistanceTo(new Vector3(0, mid, 0)))),
            ShadowEnabled = true,
            ShadowBias = 0.03f,
        };
        root.AddChild(spot);
        spot.LookAtFromPosition(at, new Vector3(0, mid, 0), Vector3.Up);

        root.AddChild(new ProjectionWalker(Mat(new Color(0.85f, 0.85f, 0.85f))) { Name = "Walker", Position = new Vector3(0, 0, FigureLane) });
        return root;
    }

    /// <summary>The slide in a square of black, so the spot's square projection keeps its 2.39:1.</summary>
    private static ImageTexture Letterboxed(Texture2D tex)
    {
        var img = tex.GetImage();
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgb8);
        int w = img.GetWidth();
        var square = Image.CreateEmpty(w, w, false, Image.Format.Rgb8);
        square.Fill(Colors.Black);
        square.BlitRect(img, new Rect2I(0, 0, w, img.GetHeight()), new Vector2I(0, (w - img.GetHeight()) / 2));
        return ImageTexture.CreateFromImage(square);
    }
}
