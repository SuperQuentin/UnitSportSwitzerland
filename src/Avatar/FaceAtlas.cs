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
    public const int FinishId = 11;

    private const int Cell = 16, Cells = 4;

    /// <summary>
    /// Faces as the left half of a 16×16 cell, mirrored onto the right. Keys: <c>.</c> clear,
    /// <c>k</c> line, <c>w</c> eye white, <c>i</c>/<c>d</c> iris and its dark, <c>h</c> highlight,
    /// <c>m</c> mouth, <c>b</c> blush, <c>n</c> nose, <c>f</c> freckle.
    /// </summary>
    private static readonly (string Name, Color Iris, string[] Rows)[] Faces =
    {
        ("anime", new Color(0.56f, 0.30f, 0.86f), new[]
        {
            "........", "........", "........", "..kkk...", "........",
            "kkkkkk..", "..whid..", "..wiid..", "..widd..", "...kk...",
            ".bb.....", "........", ".......m", "........", "........", "........",
        }),
        ("calm", new Color(0.42f, 0.55f, 0.28f), new[]
        {
            "........", "........", "........", "........", "..kkk...",
            "........", ".kkkkk..", "..wddk..", "...kk...", "........",
            "........", "........", ".......k", "........", "........", "........",
        }),
        ("sharp", new Color(0.10f, 0.10f, 0.12f), new[]
        {
            "........", "........", "........", ".kk.....", "..kkk...",
            ".kkkkk..", ".kwwwk..", ".kwkwk..", "..kkk...", "..k.k...",
            "........", "........", "......kk", "........", "........", "........",
        }),
        ("cute", new Color(0.25f, 0.55f, 0.95f), new[]
        {
            "........", "........", "........", "........", "........",
            "..kkk...", ".kddhk..", ".kiiik..", "..kkk...", "........",
            ".bbb....", "........", "......k.", ".......k", "........", "........",
        }),
        ("freckles", new Color(0.45f, 0.30f, 0.18f), new[]
        {
            "........", "........", "........", "..kk....", "........",
            ".kkkk...", ".kwdk...", ".kwdk...", "........", ".f.f....",
            "..f.....", ".......n", "........", "......mm", "........", "........",
        }),
    };

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
            var (_, iris, rows) = Faces[f];
            int ox = f % Cells * Cell, oy = f / Cells * Cell;
            for (int y = 0; y < Cell; y++)
                for (int x = 0; x < Cell / 2; x++)
                {
                    Color? c = rows[y][x] switch
                    {
                        'k' => line,
                        'w' => new Color(0.97f, 0.96f, 0.94f),
                        'i' => iris,
                        'd' => iris.Darkened(0.5f),
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
