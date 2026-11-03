using UnitSport.Adventure.Errands;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The givers stage (#1339): which OSM tags make which giver, the alp dairy against the village one, the thinning, the file.</summary>
public class GiverPickTests
{
    private static Dictionary<string, string> Tags(params (string K, string V)[] kv) => kv.ToDictionary(p => p.K, p => p.V);

    [Fact]
    public void TagsMakeTheRightGivers()
    {
        Assert.Equal(GiverPick.Postmaster, GiverPick.KindOf(Tags(("amenity", "post_office"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("craft", "cheese_maker"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("shop", "dairy"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("shop", "cheese"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("name", "Sennerei Hinterberg"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("name", "Fromagerie d'alpage"))));
        Assert.Equal(GiverPick.Dairy, GiverPick.KindOf(Tags(("name", "Caseificio del Gottardo"))));
        Assert.Equal(GiverPick.Host, GiverPick.KindOf(Tags(("amenity", "restaurant"), ("cuisine", "swiss"))));
        Assert.Equal(GiverPick.Host, GiverPick.KindOf(Tags(("amenity", "restaurant"), ("cuisine", "pizza;regional"))));
        Assert.Equal(0, GiverPick.KindOf(Tags(("amenity", "restaurant"), ("cuisine", "pizza"))));
        Assert.Equal(0, GiverPick.KindOf(Tags(("amenity", "restaurant"))));
        Assert.Equal(0, GiverPick.KindOf(Tags(("amenity", "restaurant"), ("name", "Käserei-Beizli"), ("cuisine", "pizza"))));   // a restaurant called Käserei is no dairy
        Assert.Equal(GiverPick.Farmer, GiverPick.KindOf(Tags(("place", "farm"))));
        Assert.Equal(GiverPick.Farmer, GiverPick.KindOf(Tags(("landuse", "farmyard"))));
        Assert.Equal(0, GiverPick.KindOf(Tags(("amenity", "post_office"), ("disused", "yes"))));
        Assert.Equal(0, GiverPick.KindOf(Tags(("amenity", "bench"))));
    }

    [Fact]
    public void ADairyOn_TheAlpIsASennAndOneInTheValleyIsNot()
    {
        Assert.Equal(GiverPick.Senn, GiverPick.Resolve(GiverPick.Dairy, 1200));
        Assert.Equal(GiverPick.Senn, GiverPick.Resolve(GiverPick.Dairy, 1950));
        Assert.Equal(GiverPick.Dairy, GiverPick.Resolve(GiverPick.Dairy, 1199));
        Assert.Equal(GiverPick.Postmaster, GiverPick.Resolve(GiverPick.Postmaster, 2500));
    }

    private static List<GiverPick.Candidate> Sample() => new()
    {
        new("osm:n1", GiverPick.Postmaster, 2600100, 1200100, "Post Zermatt"),
        new("osm:n2", GiverPick.Postmaster, 2600110, 1200110, "Post Zermatt (agency)"),   // 14 m from the first: one place
        new("osm:n3", GiverPick.Dairy, 2601500, 1201500, "Käserei im Tal"),
        new("osm:n4", GiverPick.Dairy, 2603500, 1203500, "Alp Sennerei"),
        new("osm:n5", GiverPick.Farmer, 2600300, 1200300, "Hof A"),
        new("osm:n6", GiverPick.Farmer, 2600900, 1200900, "Hof B"),   // the same 2 km cell as Hof A
        new("osm:n7", GiverPick.Farmer, 2602300, 1200300, "Hof C"),   // the next cell
        new("osm:n8", GiverPick.Host, 2600500, 1200500, "Beiz 1"),
        new("osm:n9", GiverPick.Host, 2601500, 1201500, "Beiz 2"),
    };

    private static double Height(double e, double n) => e > 2603000 ? 1800 : 700;
    private static string Canton(double e, double n) => "VS";

    [Fact]
    public void ThePickThinsFarmsAndBeizenToOneAKilometreCellPairAndMergesNeighbours()
    {
        var rows = GiverPick.Pick(Sample(), Height, Canton);
        Assert.Equal(1, rows.Count(r => r.Kind == GiverPick.Postmaster));
        Assert.Equal(2, rows.Count(r => r.Kind == GiverPick.Farmer));   // A or B, and C
        Assert.Equal(1, rows.Count(r => r.Kind == GiverPick.Host));     // both Beizen lie in one 2 km cell
        Assert.Equal(1, rows.Count(r => r.Kind == GiverPick.Dairy));
        Assert.Equal(1, rows.Count(r => r.Kind == GiverPick.Senn));
        Assert.All(rows, r => Assert.Equal("VS", r.Canton));
        Assert.StartsWith("giver:senn:", rows.First(r => r.Kind == GiverPick.Senn).Id);
        Assert.Equal(rows.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal), rows.Select(r => r.Id));
    }

    [Fact]
    public void TheSamePickComesOutWhateverTheOrder()
    {
        var a = GiverPick.Pick(Sample(), Height, Canton);
        var reversed = Sample();
        reversed.Reverse();
        var b = GiverPick.Pick(reversed, Height, Canton);
        Assert.Equal(a, b);
        Assert.Equal(a, GiverPick.Pick(Sample(), Height, Canton));
    }

    [Fact]
    public void AnIdKeepsItsSourceNotItsPosition()
    {
        Assert.Equal(GiverPick.IdOf(GiverPick.Farmer, "gwr:123"), GiverPick.IdOf(GiverPick.Farmer, "gwr:123"));
        Assert.NotEqual(GiverPick.IdOf(GiverPick.Farmer, "gwr:123"), GiverPick.IdOf(GiverPick.Farmer, "gwr:124"));
        Assert.Matches(@"^giver:farm:[0-9a-f]{10}$", GiverPick.IdOf(GiverPick.Farmer, "gwr:123"));
    }

    [Fact]
    public void AFarmIsABigBarnWithAHomeNear()
    {
        var solids = new List<GiverPick.Solid>
        {
            new(100, 100, 300, Barn: true, Dwelling: false, "gwr:1"),
            new(120, 110, 90, Barn: false, Dwelling: true, "gwr:2"),     // a house 22 m from the barn
            new(500, 500, 400, Barn: true, Dwelling: false, "gwr:3"),    // a barn with no house
            new(900, 900, 80, Barn: true, Dwelling: false, "gwr:4"),     // too small
            new(905, 905, 90, Barn: false, Dwelling: true, "gwr:5"),
            new(300, 300, 200, Barn: true, Dwelling: false, "gwr:6"),
            new(380, 300, 90, Barn: false, Dwelling: true, "gwr:7"),     // 80 m away
        };
        var farms = GiverPick.Farms(solids).ToList();
        Assert.Single(farms);
        Assert.Equal("gwr:1", farms[0].Source);
    }

    [Fact]
    public void TheFileRoundTripsAndRerunsGiveTheSameBytes()
    {
        var rows = GiverPick.Pick(Sample(), Height, Canton);
        byte[] Write()
        {
            using var ms = new MemoryStream();
            GiverFormat.Write(ms, "osm=test tiles=0", rows);
            return ms.ToArray();
        }
        var bytes = Write();
        Assert.Equal(bytes, Write());
        var back = GiverFormat.Read(new MemoryStream(bytes));
        Assert.Equal(rows.Count, back.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            Assert.Equal(rows[i].Id, back[i].Id);
            Assert.Equal(rows[i].Kind, back[i].Kind);
            Assert.Equal(rows[i].E, back[i].E, 1);
            Assert.Equal(rows[i].Name, back[i].Name);
            Assert.Equal(rows[i].Canton, back[i].Canton);
        }
        Assert.Throws<InvalidDataException>(() => GiverFormat.Read(new MemoryStream(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public void TheCatalogueTakesTheFileAndSkipsKindsItDoesNotKnow()
    {
        var rows = GiverPick.Pick(Sample(), Height, Canton).ToList();
        rows.Add(new GiverRecord("giver:x:1", 42, 2600000, 1200000, 0f, "VS", 600, "Unknown", "test"));
        var catalogue = new GiverCatalogue(GiverCatalogue.FromRecords(rows));
        Assert.Equal(rows.Count - 1, catalogue.Count);
        Assert.Null(catalogue.Resolve("giver:x:1"));
        var senn = catalogue.All.First(g => g.Kind == GiverKind.Senn);
        Assert.Equal(1800, senn.Elevation);
        var near = new List<Giver>();
        catalogue.Near(senn.E, senn.N, 50, near);
        Assert.Contains(near, g => g.Id == senn.Id);
        // the Postkarte of an alp dairy now exists with real data: the cheese goes down to a dairy 300-1 400 m lower
        Assert.Contains(catalogue.All, g => g.Kind == GiverKind.Dairy && senn.Elevation - g.Elevation is >= 300 and <= 1400);
    }
}
