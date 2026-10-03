using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The figures' pixel faces (#394): a 64×64 texture of 16×16 cells, drawn here as text so a face
/// is edited like the mask faces are (<c>HumanMeshBuilder.MaskFaces</c>), with no imported asset.
/// The head's face band (<see cref="MeshScratch.FaceBand"/>) samples one cell; transparent pixels
/// are discarded so the head's skin shows through, which is why the atlas carries no skin tone.
/// <c>shaders/body/avatar.gdshaderinc</c> draws it for finish id <see cref="FinishId"/>.
/// </summary>
public static class FaceAtlas
{
    /// <summary>The vertex-alpha finish id that marks a face band (after the clothes' 1..10).</summary>
    public const int FinishId = (int)Finish.Face;

    private const int Cell = 16, Cells = 4;

    /// <summary>
    /// Faces as the left half of a 16×16 cell, mirrored onto the right. Keys: <c>.</c> clear,
    /// <c>k</c> line, <c>w</c> eye white, <c>i</c>/<c>d</c> iris and its dark (keyed magenta: the
    /// eye colour is the face band's vertex colour), <c>h</c> highlight, <c>m</c> mouth, <c>b</c>
    /// blush, <c>n</c> nose, <c>f</c> freckle, <c>s</c> stubble.
    /// </summary>
    private static readonly (string Name, string[] Rows)[] Faces =
    {
        ("anime", new[]
        {
            "........", "........", "........", "..kkk...", "........",
            "kkkkkk..", "..whid..", "..wiid..", "..widd..", "...kk...",
            ".bb.....", "........", ".......m", "........", "........", "........",
        }),
        ("calm", new[]
        {
            "........", "........", "........", "........", "..kkk...",
            "........", ".kkkkk..", "..wddk..", "...kk...", "........",
            "........", "........", ".......k", "........", "........", "........",
        }),
        ("sharp", new[]
        {
            "........", "........", "........", ".kk.....", "..kkk...",
            ".kkkkk..", ".kwwwk..", ".kwkwk..", "..kkk...", "..k.k...",
            "........", "........", "......kk", "........", "........", "........",
        }),
        ("cute", new[]
        {
            "........", "........", "........", "........", "........",
            "..kkk...", ".kddhk..", ".kiiik..", "..kkk...", "........",
            ".bbb....", "........", "......k.", ".......k", "........", "........",
        }),
        ("freckles", new[]
        {
            "........", "........", "........", "..kk....", "........",
            ".kkkk...", ".kwdk...", ".kwdk...", "........", ".f.f....",
            "..f.....", ".......n", "........", "......mm", "........", "........",
        }),
        // #394 feedback: masculine faces, heavier brows and smaller eyes
        ("stern", new[]
        {
            "........", "........", "........", ".kkkk...", "..kkkk..",
            "........", ".kkkk...", "..wdk...", "........", "........",
            ".......n", "........", "......kk", "........", "........", "........",
        }),
        ("grin", new[]
        {
            "........", "........", "........", "........", "..kkk...",
            "........", ".kkkk...", ".kwik...", "..kk....", "........",
            ".......n", "........", ".....k..", "......kw", "........", "........",
        }),
        ("stubble", new[]
        {
            "........", "........", "........", "........", ".kkk....",
            "........", ".kkkk...", "..dk....", "........", "........",
            ".......n", ".s.....s", "......kk", "s.s.s.s.", ".s.s.s.s", "..s.s.s.",
        }),
    };

    /// <summary>The faces' names, in atlas order.</summary>
    public static string Name(int index) => Faces[Mathf.PosMod(index, Faces.Length)].Name;

    public static int Count => Faces.Length;

    /// <summary>Where face <paramref name="index"/> sits in the atlas, half a texel in from each edge.</summary>
    public static Rect2 Uv(int index)
    {
        index = Mathf.PosMod(index, Faces.Length);
        const float size = 1f / Cells, inset = 0.5f / (Cell * Cells);
        return new Rect2(new Vector2(index % Cells, index / Cells) * size + new Vector2(inset, inset),
            new Vector2(size - 2 * inset, size - 2 * inset));
    }

    private static ImageTexture? _texture;

    public static Texture2D Texture => _texture ??= Draw();

    private static ImageTexture Draw()
    {
        var image = Image.CreateEmpty(Cell * Cells, Cell * Cells, false, Image.Format.Rgba8);
        image.Fill(new Color(0, 0, 0, 0));
        var line = new Color(0.10f, 0.07f, 0.10f);
        for (int f = 0; f < Faces.Length; f++)
        {
            var (_, rows) = Faces[f];
            int ox = f % Cells * Cell, oy = f / Cells * Cell;
            for (int y = 0; y < Cell; y++)
                for (int x = 0; x < Cell / 2; x++)
                {
                    Color? c = rows[y][x] switch
                    {
                        'k' => line,
                        'w' => new Color(0.97f, 0.96f, 0.94f),
                        // the iris is keyed, not coloured: the shader draws it in the figure's eye colour
                        'i' => new Color(1f, 0f, 1f),
                        'd' => new Color(0.55f, 0f, 0.55f),
                        's' => new Color(0.36f, 0.30f, 0.28f),
                        'h' => Colors.White,
                        'm' => new Color(0.50f, 0.14f, 0.18f),
                        'b' => new Color(0.96f, 0.56f, 0.62f),
                        'n' => new Color(0.72f, 0.46f, 0.40f),
                        'f' => new Color(0.66f, 0.42f, 0.30f),
                        _ => null,
                    };
                    if (c is not { } colour) continue;
                    image.SetPixel(ox + x, oy + y, colour);
                    image.SetPixel(ox + Cell - 1 - x, oy + y, colour);
                }
        }
        return ImageTexture.CreateFromImage(image);
    }
}
