using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnitSport.Adventure;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Adventure progress store (src/Adventure/PlayerProgress.cs, SectionKey.cs, linked in, #1223).</summary>
public class AdventureBookTests
{
    private sealed class Places
    {
        public List<string> Found { get; set; } = new();
        public int Viewpoints { get; set; }
    }

    // v1 kept the found POIs as one comma-separated string
    private static readonly SectionKey<Places> PlacesV2 = new("places", Version: 2, Migrate: (old, from) => from == 1
        ? new Places { Found = new List<string>((old["found"]?.GetValue<string>() ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries)) }
        : old.Deserialize<Places>(AdventureJson.Options));

    private static readonly SectionKey<Places> PlacesV1Reader = new("places", Version: 1);

    private sealed class Pins { public List<string> Ids { get; set; } = new(); }
    private static readonly SectionKey<Pins> PinsKey = new("pins", Version: 1);

    [Fact]
    public void Sections_round_trip_through_the_file()
    {
        var p = new PlayerProgress("9f2c") { LastName = "Lea", Created = 1793000000, Seen = 1793004000 };
        var q = p.Get(QuestsProgress.Section);
        q.Done["a"] = 5;
        q.Titles.Add("Pass hunter");
        p.Touch(QuestsProgress.Section);
        p.Get(PinsKey).Ids.Add("x");
        p.Touch(PinsKey);

        var back = AdventureBook.FromJson(AdventureBook.ToJson(p));
        Assert.Equal("9f2c", back.Key);
        Assert.Equal("Lea", back.LastName);
        Assert.Equal(1793000000, back.Created);
        Assert.Equal(1793004000, back.Seen);
        Assert.Equal(5, back.Get(QuestsProgress.Section).Done["a"]);
        Assert.Equal(new[] { "Pass hunter" }, back.Get(QuestsProgress.Section).Titles);
        Assert.Equal(new[] { "x" }, back.Get(PinsKey).Ids);
    }

    [Fact]
    public void The_file_has_the_envelope_and_versioned_sections()
    {
        var p = new PlayerProgress("k");
        p.Get(QuestsProgress.Section).Tracked = "q";
        p.Touch(QuestsProgress.Section);
        var root = AdventureBook.ToNode(p);
        Assert.Equal(1, root["v"]!.GetValue<int>());
        Assert.Equal("k", root["key"]!.GetValue<string>());
        Assert.Equal(1, root["sections"]!["quests"]!["v"]!.GetValue<int>());
        Assert.Equal("q", root["sections"]!["quests"]!["data"]!["tracked"]!.GetValue<string>());
    }

    [Fact]
    public void A_section_no_code_asked_for_is_written_back_unchanged()
    {
        const string json = """
            {"v":1,"key":"k","name":"","created":0,"seen":0,"sections":{
              "fog":{"v":3,"data":{"mask":"AAEC","cells":[1,2,3]}},
              "quests":{"v":1,"data":{"active":{},"done":{"x":7},"tracked":null,"titles":[],"title":null}}}}
            """;
        var p = AdventureBook.FromJson(json);
        p.Get(QuestsProgress.Section).Done["y"] = 8;
        p.Touch(QuestsProgress.Section);
        var back = AdventureBook.ToNode(p);
        Assert.Equal(3, back["sections"]!["fog"]!["v"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"mask":"AAEC","cells":[1,2,3]}"""), back["sections"]!["fog"]!["data"]));
        Assert.Equal(8, back["sections"]!["quests"]!["data"]!["done"]!["y"]!.GetValue<long>());
    }

    [Fact]
    public void A_newer_section_is_kept_and_read_as_a_read_only_empty_value()
    {
        var warnings = new List<string>();
        AdventureJson.Warn = warnings.Add;
        try
        {
            var p = new PlayerProgress("k");
            p.SetRaw("places", 2, JsonNode.Parse("""{"found":["a","b"],"viewpoints":4}"""));
            var places = p.Get(PlacesV1Reader);   // this build only knows version 1
            Assert.Empty(places.Found);
            Assert.Single(warnings);
            places.Found.Add("c");
            p.Touch(PlacesV1Reader);   // refused: a downgrade never overwrites newer data
            Assert.Equal(2, warnings.Count);
            var back = AdventureBook.ToNode(p);
            Assert.Equal(2, back["sections"]!["places"]!["v"]!.GetValue<int>());
            Assert.Equal(4, back["sections"]!["places"]!["data"]!["viewpoints"]!.GetValue<int>());
            Assert.False(p.NeedsSave);
        }
        finally { AdventureJson.Warn = null; }
    }

    [Fact]
    public void An_older_section_migrates_and_is_saved_in_the_new_shape()
    {
        var p = new PlayerProgress("k");
        p.SetRaw("places", 1, JsonNode.Parse("""{"found":"a,b,c"}"""));
        var places = p.Get(PlacesV2);
        Assert.Equal(new[] { "a", "b", "c" }, places.Found);
        Assert.True(p.NeedsSave);   // migrated: written back at the next save
        Assert.Equal(new[] { "places" }, p.TakeSync());
        var back = AdventureBook.ToNode(p);
        Assert.Equal(2, back["sections"]!["places"]!["v"]!.GetValue<int>());
        Assert.Equal(3, back["sections"]!["places"]!["data"]!["found"]!.AsArray().Count);
    }

    [Fact]
    public void Decoding_is_lazy_and_returns_the_same_instance()
    {
        var p = new PlayerProgress("k");
        p.SetRaw("pins", 1, JsonNode.Parse("""{"ids":["a"]}"""));
        var a = p.Get(PinsKey);
        var b = p.Get(PinsKey);
        Assert.Same(a, b);
        Assert.Equal(new[] { "a" }, a.Ids);
        // a section absent from the file: a new one
        Assert.Empty(p.Get(QuestsProgress.Section).Active);
    }

    [Fact]
    public void Touch_marks_a_section_for_the_save_and_the_sync_once()
    {
        var p = new PlayerProgress("k");
        p.Get(PinsKey).Ids.Add("a");
        Assert.False(p.NeedsSave);
        p.Touch(PinsKey);
        Assert.True(p.NeedsSave);
        Assert.Equal(new[] { "pins" }, p.TakeSync());
        Assert.Empty(p.TakeSync());
        p.Saved();
        Assert.False(p.NeedsSave);
    }

    [Fact]
    public void A_change_without_Touch_is_found()
    {
        var p = new PlayerProgress("k");
        p.Get(PinsKey).Ids.Add("forgot to touch");
        Assert.Equal(new[] { "pins" }, p.Untouched());
        p.Touch(PinsKey);
        Assert.Empty(p.Untouched());
        p.Saved();
        Assert.Empty(p.Untouched());
    }

    [Fact]
    public void The_client_mirror_takes_a_synced_section_and_decodes_it_again()
    {
        var server = new PlayerProgress("k");
        server.Get(QuestsProgress.Section).Tracked = "q1";
        server.Touch(QuestsProgress.Section);
        var mirror = new PlayerProgress("mine");
        Assert.Null(mirror.Get(QuestsProgress.Section).Tracked);
        foreach (string id in server.TakeSync())
            mirror.SetRaw(id, server.VersionOf(id), JsonNode.Parse(server.DataOf(id)!.ToJsonString()));
        Assert.Equal("q1", mirror.Get(QuestsProgress.Section).Tracked);
    }

    [Fact]
    public void The_book_loads_unloads_and_names_files_safely()
    {
        var book = new AdventureBook();
        var p = book.For("abc");
        Assert.Same(p, book.For("abc"));
        Assert.True(book.TryGet("abc", out _));
        book.Forget("abc");
        Assert.False(book.TryGet("abc", out _));
        Assert.Equal("acct_1234-ab.json", AdventureBook.FileName("acct:1234-ab"));
        Assert.Equal("___etc_passwd.json", AdventureBook.FileName("../etc/passwd"));
    }

    [Fact]
    public void An_unreadable_section_is_kept_raw()
    {
        var warnings = new List<string>();
        AdventureJson.Warn = warnings.Add;
        try
        {
            var p = new PlayerProgress("k");
            p.SetRaw("pins", 1, JsonNode.Parse("""{"ids":"not a list"}"""));
            Assert.Empty(p.Get(PinsKey).Ids);
            Assert.Single(warnings);
            Assert.Equal("not a list", AdventureBook.ToNode(p)["sections"]!["pins"]!["data"]!["ids"]!.GetValue<string>());
        }
        finally { AdventureJson.Warn = null; }
    }
}
