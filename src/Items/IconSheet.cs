using Godot;

namespace UnitSport.Items;

/// <summary>
/// <c>--iconsheet</c>: renders every item's icon (authored on the left, the generated fallback on
/// the right) into <c>test_output/iconsheet.png</c> and quits. Exit code 1 if a grid is malformed
/// or an item has no authored icon.
/// </summary>
public static class IconSheet
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--iconsheet") >= 0;

    public static int Run()
    {
        const int scale = 5, cols = 6, pad = 6, cell = ItemIcons.Size * scale;
        var ids = ItemDefs.All.Select(d => d.Id).ToList();
        int rows = (ids.Count + cols - 1) / cols;
        int cw = cell * 2 + pad * 3, ch = cell + pad * 2;
        var sheet = Image.CreateEmpty(cw * cols, ch * rows, false, Image.Format.Rgba8);
        sheet.Fill(new Color(0.16f, 0.17f, 0.20f));

        for (int i = 0; i < ids.Count; i++)
        {
            int ox = i % cols * cw + pad, oy = i / cols * ch + pad;
            var def = ItemDefs.Get(ids[i])!;
            Blit(sheet, ItemIcons.GetImage(ids[i])!, ox, oy, scale);
            Blit(sheet, ItemIcons.GenericPixels(def.Tint, def.Glyph, ItemIcons.ShapeFor(def)), ox + cell + pad, oy, scale);
        }

        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://test_output"));
        string path = ProjectSettings.GlobalizePath("res://test_output/iconsheet.png");
        var err = sheet.SavePng(path);
        var problems = ItemIcons.Validate();
        foreach (var p in problems) GD.Print("ICON PROBLEM: " + p);
        GD.Print($"RESULT iconsheet: {ids.Count} items, {problems.Count} problems, save={err}, {path}");
        return problems.Count == 0 && err == Error.Ok ? 0 : 1;
    }

    private static void Blit(Image dst, Image src, int ox, int oy, int scale)
    {
        for (int y = 0; y < src.GetHeight(); y++)
            for (int x = 0; x < src.GetWidth(); x++)
            {
                var c = src.GetPixel(x, y);
                if (c.A < 0.5f) continue;
                for (int dy = 0; dy < scale; dy++)
                    for (int dx = 0; dx < scale; dx++)
                        dst.SetPixel(ox + x * scale + dx, oy + y * scale + dy, c);
            }
    }
}
