using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>"What's new" (#1413): release bodies as BBCode, and which releases it lists.</summary>
public class ReleaseNotesTests
{
    [Fact]
    public void Converts_headings_bullets_and_bold_and_drops_images_and_the_diff_line()
    {
        string md = "### Highlights\n![gif](https://e/a.gif)\n\n### Features\n- **Sit** on chairs (#1385)\n  - nested [x]\n\n**Full diff:** https://e/compare\n";
        Assert.Equal(
            "[font_size=16][b]Features[/b][/font_size]\n• [b]Sit[/b] on chairs (#1385)\n  • nested [lb]x[rb]",
            ReleaseNotes.ToBbcode(md));
    }

    [Fact]
    public void A_heading_with_only_dropped_lines_is_dropped()
    {
        string md = "### Highlights\n![clip](https://e/a.gif)\n![clip](https://e/b.gif)\n\n### Features and changes\n- one\n\n**Full diff:** https://e/compare\n";
        string o = ReleaseNotes.ToBbcode(md);
        Assert.DoesNotContain("Highlights", o);
        Assert.Equal("[font_size=16][b]Features and changes[/b][/font_size]\n• one", o);
        Assert.Equal("", ReleaseNotes.ToBbcode("### Highlights\n![clip](https://e/a.gif)\n"));
    }

    [Fact]
    public void Links_keep_their_label_and_empty_bodies_are_empty()
    {
        Assert.Equal("see the docs", ReleaseNotes.ToBbcode("see [the docs](https://e/x)"));
        Assert.Equal("", ReleaseNotes.ToBbcode(null));
        Assert.Equal("", ReleaseNotes.ToBbcode("\r\n![a](b)\r\n"));
    }

    private static UpdateInfo.Release R(string tag, bool pre = false, string body = "") =>
        new(tag, "", Array.Empty<UpdateInfo.Asset>(), pre, body);

    [Fact]
    public void Notes_between_are_newest_first_stable_and_capped()
    {
        var all = new[] { R("v0.3.0"), R("v0.1.0"), R("v0.4.0-rc", pre: true), R("v0.2.0"), R("v0.4.0"), R("nightly") };
        Assert.Equal(new[] { "v0.3.0", "v0.2.0" }, UpdateInfo.NotesBetween(all, "0.1.0", "0.3.0").Select(r => r.Tag));
        Assert.Equal(new[] { "v0.4.0", "v0.3.0", "v0.2.0", "v0.1.0" }, UpdateInfo.NotesBetween(all, null, "0.4.0").Select(r => r.Tag));
        Assert.Equal(new[] { "v0.4.0", "v0.3.0" }, UpdateInfo.NotesBetween(all, "", "0.4.0", max: 2).Select(r => r.Tag));
        Assert.Empty(UpdateInfo.NotesBetween(all, "0.4.0", "0.4.0"));
        Assert.Empty(UpdateInfo.NotesBetween(all, "0.1.0", ""));
    }

    [Fact]
    public void Format_puts_each_tag_over_its_notes_and_the_body_is_parsed()
    {
        var list = UpdateInfo.ParseList("""[{"tag_name":"v0.2.0","body":"- a"},{"tag_name":"v0.1.0"}]""");
        Assert.Equal("- a", list[0].Body);
        Assert.Equal("", list[1].Body);
        Assert.Equal("[font_size=20][b]v0.2.0[/b][/font_size]\n• a\n\n[font_size=20][b]v0.1.0[/b][/font_size]\n", ReleaseNotes.Format(list));
    }
}
