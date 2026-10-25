using System.IO;
using System.Linq;
using UnitSport.Ui.Skins;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Menu skins as data (src/Ui/Skins/SkinData.cs, linked in, #1135): the shipped files, the checks, the pick.</summary>
public class SkinDataTests
{
    private static string SkinsDir()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "assets", "skins"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "assets", "skins");
    }

    [Fact]
    public void Every_shipped_skin_reads_and_names_every_role()
    {
        var files = Directory.GetFiles(SkinsDir(), "*.json");
        Assert.Contains(files, f => Path.GetFileName(f) == "sport.json");
        Assert.Contains(files, f => Path.GetFileName(f) == "classic.json");
        foreach (string f in files)
        {
            var skin = SkinData.Parse(File.ReadAllText(f), out string? error);
            Assert.True(skin != null, $"{Path.GetFileName(f)}: {error}");
            Assert.Equal(Path.GetFileNameWithoutExtension(f), skin!.Id);
        }
    }

    [Fact]
    public void The_sport_skin_leans_and_fills()
    {
        var sport = SkinData.Parse(File.ReadAllText(Path.Combine(SkinsDir(), "sport.json")), out _)!;
        Assert.Equal("saira", sport.Font);
        Assert.True(sport.Shape.Skew > 0);
        Assert.True(sport.Shape.MenuFill);
    }

    private const string Minimal = """
        { "id": "x", "name": "X", "colors": { ROLES }, "shape": { } }
        """;

    private static string With(string roles) => Minimal.Replace("ROLES", roles);

    private static string AllRoles(string except = "") =>
        string.Join(", ", SkinData.Roles.Where(r => r != except).Select(r => $"\"{r}\": \"#112233\""));

    [Fact]
    public void A_missing_role_or_a_bad_colour_is_refused_with_its_name()
    {
        Assert.NotNull(SkinData.Parse(With(AllRoles()), out _));
        Assert.Null(SkinData.Parse(With(AllRoles(except: "accent")), out string? missing));
        Assert.Contains("accent", missing);
        Assert.Null(SkinData.Parse(With(AllRoles() + ", \"controlEdge\": \"blue\""), out string? bad));
        Assert.Contains("controlEdge", bad);
    }

    [Fact]
    public void Shapes_and_fonts_out_of_range_are_refused()
    {
        string skin = With(AllRoles());
        Assert.Null(SkinData.Parse(skin.Replace("\"shape\": { }", "\"shape\": { \"skew\": 0.9 }"), out string? skew));
        Assert.Contains("skew", skew);
        Assert.Null(SkinData.Parse(skin.Replace("\"name\": \"X\"", "\"name\": \"X\", \"font\": \"comic\""), out string? font));
        Assert.Contains("font", font);
    }

    [Fact]
    public void An_optional_role_falls_back_to_its_partner()
    {
        var skin = SkinData.Parse(With(AllRoles()), out _)!;
        Assert.Equal(skin.Color("cardEdge"), skin.Color("controlEdge"));
    }

    [Theory]
    [InlineData("#FF0000", 1f, 0f, 0f, 1f)]
    [InlineData("#00FF0080", 0f, 1f, 0f, 128 / 255f)]
    public void Hex_colours_read_as_channels(string hex, float r, float g, float b, float a) =>
        Assert.Equal((r, g, b, a), SkinData.Rgba(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("FF0000")]
    [InlineData("#F00")]
    [InlineData("#GG0000")]
    public void Anything_else_is_no_colour(string? hex) => Assert.Null(SkinData.Rgba(hex));

    [Fact]
    public void The_pick_follows_the_setting_auto_and_falls_back()
    {
        var known = new[] { "sport", "classic", "night" };
        Assert.Equal("sport", SkinRules.Pick("sport", 23, "classic", "night", known, "classic"));
        Assert.Equal("classic", SkinRules.Pick("auto", 12, "classic", "night", known, "classic"));
        Assert.Equal("night", SkinRules.Pick("auto", 22, "classic", "night", known, "classic"));
        // a skin whose file is gone: the fallback
        Assert.Equal("classic", SkinRules.Pick("candy", 12, "classic", "night", known, "classic"));
    }
}
