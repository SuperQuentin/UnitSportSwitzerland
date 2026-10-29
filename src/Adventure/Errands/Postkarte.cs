using System;
using System.Collections.Generic;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Errands;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/errands.md).

/// <summary>The card templates of the plan (4.2). The hiker and the postcard come with the world events and the photo spots.</summary>
public enum CardTemplate : byte { AlpCheese = 1, MailRun = 2, LostHiker = 3, FerryRun = 4, Specialty = 5, HutSupplies = 6, FarmerRequest = 7, PostcardHome = 8 }

/// <summary>One card of a day and a cell: its quest, who gives it, what it hands out when taken and what it pays.</summary>
public sealed record Offer(string Id, int Day, int CellE, int CellN, int Slot, CardTemplate Template, Giver Giver, QuestDef Quest,
    ItemId? Cargo, int CargoCount, int Chf, int Hash, string DestCanton)
{
    /// <summary>The lost hiker's card (#1339): where the hiker sits and where the hut is. Null for every other template.</summary>
    public HikerSpot? Hiker { get; init; }
}

/// <summary>Where a card's hiker waits (<see cref="Near"/> names the peak, pass or cross they were last seen by) and where they are brought to.</summary>
public sealed record HikerSpot(double E, double N, string Near, double GoalE, double GoalN);

/// <summary>
/// The daily Postkarten (#1267 part 4): <see cref="Offers"/> picks the day's cards of a 5 km cell from its givers, the same on
/// the server and every client from the catalogues alone. A card's id says everything: <c>pk:20261027:517_102:3</c> is the
/// third card of that day and cell, rebuilt by <see cref="Offers"/>.
/// </summary>
public static class Postkarte
{
    public const int CellKm = 5, CellM = 5000, PerCell = 8, PerGiver = 2;
    public const string Prefix = "pk:";
    public const int RepHome = 3, RepAway = 1;

    public static (int E, int N) CellOf(double e, double n) => ((int)Math.Floor(e / CellM), (int)Math.Floor(n / CellM));

    public static string IdOf(int day, int cellE, int cellN, int slot) => $"{Prefix}{day}:{cellE}_{cellN}:{slot}";

    public static bool IsCard(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Parses a card id; false for anything malformed.</summary>
    public static bool TryParse(string id, out int day, out int cellE, out int cellN, out int slot)
    {
        day = cellE = cellN = slot = 0;
        if (!IsCard(id)) return false;
        var parts = id.Split(':');
        if (parts.Length != 4) return false;
        var cell = parts[2].Split('_');
        return int.TryParse(parts[1], out day) && day > 20000101 && cell.Length == 2 && int.TryParse(cell[0], out cellE) && int.TryParse(cell[1], out cellN)
            && int.TryParse(parts[3], out slot) && slot is >= 0 and < PerCell;
    }

    public static int? DayOf(string id) => TryParse(id, out int d, out _, out _, out _) ? d : null;

    // ---- the day's cards ------------------------------------------------------------------------------

    /// <summary>
    /// The cards of <paramref name="day"/> in a cell: its givers in a hash order of (day, cell), each trying the templates its
    /// kind can give until one finds a valid route, two cards a giver at most and eight a cell. A cell with no givers has none.
    /// </summary>
    public static List<Offer> Offers(int day, int cellE, int cellN, GiverCatalogue givers, PoiCatalogue pois, TravelRules rules)
    {
        var offers = new List<Offer>(PerCell);
        var inCell = new List<Giver>();
        givers.InCell(cellE, cellN, inCell);
        if (inCell.Count == 0) return offers;
        long d = day, ce = cellE, cn = cellN;
        var order = new Dictionary<string, ulong>(inCell.Count);
        foreach (var g in inCell) order[g.Id] = ErrandHash.Of(d, ce, cn, (long)ErrandHash.Of(g.Id));
        inCell.Sort((a, b) => order[a.Id].CompareTo(order[b.Id]));
        var temp = new List<Giver>();
        foreach (var giver in inCell)
        {
            if (offers.Count >= PerCell) break;
            var templates = TemplatesOf(giver);
            long gh = (long)ErrandHash.Of(giver.Id);
            Array.Sort(templates, (a, b) => ErrandHash.Of(d, ce, cn, gh, (long)a).CompareTo(ErrandHash.Of(d, ce, cn, gh, (long)b)));
            int made = 0;
            foreach (var t in templates)
            {
                if (made >= PerGiver || offers.Count >= PerCell) break;
                if (Build(t, day, cellE, cellN, offers.Count, giver, givers, pois, rules, temp) is { } offer)
                {
                    offers.Add(offer);
                    made++;
                }
            }
        }
        return offers;
    }

    /// <summary>The templates a kind of giver can give (before their routes are tried).</summary>
    public static CardTemplate[] TemplatesOf(Giver g) => g.Kind switch
    {
        GiverKind.HutWarden => new[] { CardTemplate.Specialty, CardTemplate.LostHiker, CardTemplate.PostcardHome },
        GiverKind.StationMaster => g.Elevation <= 1200 ? new[] { CardTemplate.Specialty, CardTemplate.HutSupplies, CardTemplate.PostcardHome } : new[] { CardTemplate.Specialty, CardTemplate.PostcardHome },
        GiverKind.ShipMaster => new[] { CardTemplate.FerryRun, CardTemplate.PostcardHome },
        GiverKind.Farmer => new[] { CardTemplate.FarmerRequest },
        GiverKind.Senn => g.Elevation >= 1200 ? new[] { CardTemplate.AlpCheese, CardTemplate.Specialty } : new[] { CardTemplate.Specialty },
        GiverKind.Dairy => new[] { CardTemplate.Specialty },
        GiverKind.Postmaster => g.Elevation <= 1200 ? new[] { CardTemplate.MailRun, CardTemplate.HutSupplies, CardTemplate.PostcardHome } : new[] { CardTemplate.MailRun, CardTemplate.PostcardHome },
        _ => new[] { CardTemplate.Specialty, CardTemplate.PostcardHome },
    };

    private static Offer? Build(CardTemplate t, int day, int cellE, int cellN, int slot, Giver from, GiverCatalogue givers, PoiCatalogue pois, TravelRules rules, List<Giver> temp)
    {
        long seed = (long)ErrandHash.Of(day, cellE, cellN, (long)ErrandHash.Of(from.Id), (long)t);
        string id = IdOf(day, cellE, cellN, slot);
        string fromName = from.Name;
        Step[] steps;
        ItemId? cargo = null;
        int cargoCount = 0;
        double km = 0, climb = 0;
        string dest, destCanton, title, blurb;
        ItemId item;
        switch (t)
        {
            case CardTemplate.AlpCheese:
            {
                // cheese down to a dairy or a Beiz 300-1 400 m lower, within 12 km
                givers.Near(from.E, from.N, 12000, temp);
                var to = Pick(temp, seed, g => g.Kind is GiverKind.Dairy or GiverKind.Host or GiverKind.Postmaster && from.Elevation - g.Elevation is >= 300 and <= 1400);
                if (to == null) return null;
                item = Specialties.For(from.Canton);
                cargo = item; cargoCount = 2;
                steps = new Step[] { new DeliverStep(item, At(to)) { Count = 2 } };
                km = Km(from, to); dest = to.Name; destCanton = to.Canton;
                title = $"Cheese down to {dest}";
                blurb = $"Carry 2 {Pretty(item)} from {fromName} down to {dest}.";
                break;
            }
            case CardTemplate.MailRun:
            {
                var stops = Stops(from, pois, seed);
                if (stops == null) return null;
                item = ItemId.MailSack;
                cargo = item; cargoCount = 3;
                var list = new Step[3];
                double e = from.E, n = from.N;
                for (int i = 0; i < 3; i++)
                {
                    list[i] = new DeliverStep(item, new AreaTarget(stops[i].Name, stops[i].E, stops[i].N, 30f));
                    km += Math.Sqrt((stops[i].E - e) * (stops[i].E - e) + (stops[i].N - n) * (stops[i].N - n)) / 1000;
                    e = stops[i].E; n = stops[i].N;
                }
                steps = list;
                dest = stops[2].Name; destCanton = stops[2].Canton.Length > 0 ? stops[2].Canton : from.Canton;
                title = $"Mail run: {stops[0].Name}, {stops[1].Name}, {stops[2].Name}";
                blurb = $"Take 3 mail sacks from {fromName} to {stops[0].Name}, {stops[1].Name} and {stops[2].Name}, in that order.";
                break;
            }
            case CardTemplate.FerryRun:
            {
                var to = Landing(from, pois, seed);
                if (to == null) return null;
                item = ItemId.FerryCrate;
                cargo = item; cargoCount = 1;
                steps = new Step[] { new DeliverStep(item, new AreaTarget(to.Name, to.E, to.N, 30f)) };
                km = Math.Sqrt((to.E - from.E) * (to.E - from.E) + (to.N - from.N) * (to.N - from.N)) / 1000;
                dest = to.Name; destCanton = to.Canton.Length > 0 ? to.Canton : from.Canton;
                title = $"Ferry crate to {dest}";
                blurb = $"Take a crate from {fromName} across the lake to {dest}.";
                break;
            }
            case CardTemplate.Specialty:
            {
                // a specialty of this canton to a trade giver 10-90 km away, another canton preferred
                var to = Far(givers.Trade, from, seed);
                if (to == null) return null;
                item = Specialties.For(from.Canton);
                cargo = item; cargoCount = 3;
                steps = new Step[] { new DeliverStep(item, At(to)) { Count = 3 } };
                km = Km(from, to); dest = to.Name; destCanton = to.Canton;
                title = $"{Pretty(item)} for {dest}";
                blurb = $"Take 3 {Pretty(item)} from {fromName} to {dest}, {km:0} km away.";
                break;
            }
            case CardTemplate.HutSupplies:
            {
                var to = Pick(givers.Huts, seed, g => g.Elevation - from.Elevation >= 500 && Km(from, g) <= 10 && g.Id != from.Id);
                if (to == null) return null;
                item = ItemId.SupplyPack;
                cargo = item; cargoCount = 1;
                steps = new Step[] { new DeliverStep(item, At(to)) };
                km = Km(from, to); climb = to.Elevation - from.Elevation; dest = to.Name; destCanton = to.Canton;
                title = $"Supplies up to {dest}";
                blurb = $"Carry a supply pack from {fromName} up to {dest}, {climb:0} m higher.";
                break;
            }
            case CardTemplate.FarmerRequest:
            {
                var wants = new[] { ItemId.Potato, ItemId.Flour, ItemId.Carrot, ItemId.HayBale, ItemId.WheatSeed };
                item = wants[(int)(ErrandHash.Mix((ulong)seed) % (ulong)wants.Length)];
                int count = 2 + (int)(ErrandHash.Mix((ulong)seed + 7) % 5);
                steps = new Step[] { new DeliverStep(item, new AreaTarget(from.Name, from.E, from.N, 6f)) { Count = count } };
                dest = from.Name; destCanton = from.Canton;
                title = $"{count} {Pretty(item)} for {fromName}";
                blurb = $"{fromName} needs {count} {Pretty(item)}. Grow them, buy them, and bring them here.";
                return Make(t, id, day, cellE, cellN, slot, from, seed, title, blurb, steps, null, 0, ErrandRewards.For(t, 0, 0, item, count, rules), destCanton);
            }
            case CardTemplate.LostHiker:
            {
                // a hiker sitting 20-80 m off a peak, pass or cross 400-1 500 m from the hut: found, then brought down to it
                var by = HikerPlace(from, pois, seed);
                if (by == null) return null;
                var (poi, he, hn) = by.Value;
                string entity = WorldEventRules.HikerEntity(id);
                var hut = new AreaTarget(from.Name, from.E, from.N, HikerSim.GoalM);
                steps = new Step[]
                {
                    new InteractStep(entity) { Text = $"Find the lost hiker, last seen near {poi.Name}", Reveal = true },
                    new EscortStep(entity, hut),
                };
                km = Km(from, poi.E, poi.N); climb = Math.Max(0, poi.Elevation - from.Elevation);
                dest = from.Name; destCanton = from.Canton;
                title = $"A hiker is lost near {poi.Name}";
                blurb = $"A hiker did not come back. Find them near {poi.Name} and bring them to {fromName}.";
                var offer = Make(t, id, day, cellE, cellN, slot, from, seed, title, blurb, steps, null, 0, ErrandRewards.For(t, km, climb, null, 0, rules), destCanton);
                return offer with { Hiker = new HikerSpot(he, hn, poi.Name, from.E, from.N) };
            }
            case CardTemplate.PostcardHome:
            {
                // a photo of a famous view within 10 km, then the postcard to a post office
                var spot = PostcardSpot(from, seed);
                if (spot == null) return null;
                var post = Pick(givers.Trade, seed, g => g.Kind == GiverKind.Postmaster && Km(g, spot.StandE, spot.StandN) <= 15);
                if (post == null) return null;
                item = ItemId.Postcard;
                cargo = item; cargoCount = 1;
                steps = new Step[] { new PostcardStep(spot.Id, spot.Target), new DeliverStep(item, At(post)) };
                km = Km(from, post); dest = post.Name; destCanton = post.Canton;
                title = $"A postcard of {spot.Target}";
                blurb = $"Take a good photo: {spot.Name}. Then post the card at {dest}.";
                break;
            }
            default:
                return null;
        }
        return Make(t, id, day, cellE, cellN, slot, from, seed, title, blurb, steps, cargo, cargoCount, ErrandRewards.For(t, km, climb, item, cargoCount, rules), destCanton);
    }

    private static Offer Make(CardTemplate t, string id, int day, int cellE, int cellN, int slot, Giver from, long seed, string title, string blurb,
        Step[] steps, ItemId? cargo, int cargoCount, int chf, string destCanton)
    {
        var rewards = new List<Reward> { new Cash(chf), new Unlock("rep", $"{from.Canton}:+{RepHome}") };
        if (destCanton.Length == 2 && destCanton != from.Canton) rewards.Add(new Unlock("rep", $"{destCanton}:+{RepAway}"));
        var quest = new QuestDef(id, title, blurb, steps, rewards.ToArray())
        {
            Canton = from.Canton, Start = QuestStart.Given, Source = "postkarte", Repeatable = false,
        };
        return new Offer(id, day, cellE, cellN, slot, t, from, quest, cargo, cargoCount, chf, (int)(ErrandHash.Mix((ulong)seed) & 0x7FFFFFFF), destCanton);
    }

    private static AreaTarget At(Giver g) => new(g.Name, g.E, g.N, 6f);

    private static double Km(Giver a, double e, double n) => Math.Sqrt((e - a.E) * (e - a.E) + (n - a.N) * (n - a.N)) / 1000;

    private static readonly PoiKind[] HikerKinds = { PoiKind.Peak, PoiKind.Pass, PoiKind.SummitCross };

    /// <summary>The peak, pass or cross 400-1 500 m from a hut that the hash picks, and the hiker's seat 20-80 m from it.</summary>
    private static (Poi Poi, double E, double N)? HikerPlace(Giver hut, PoiCatalogue pois, long seed)
    {
        Poi? best = null;
        ulong bestScore = ulong.MaxValue;
        var near = new List<Poi>();
        foreach (var kind in HikerKinds)
        {
            pois.Near(hut.E, hut.N, 1500, near, kind);
            foreach (var p in near)
            {
                double d = Math.Sqrt((p.E - hut.E) * (p.E - hut.E) + (p.N - hut.N) * (p.N - hut.N));
                if (d < 400 || p.Name.Length == 0) continue;
                ulong score = ErrandHash.Of(seed, (long)p.Hash);
                if (score < bestScore) { bestScore = score; best = p; }
            }
        }
        if (best == null) return null;
        var (e, n) = WorldEventRules.Offset(best.E, best.N, ErrandHash.Unit(ErrandHash.Of(seed, 0x4B)), 20 + 60 * ErrandHash.Unit(ErrandHash.Of(seed, 0x4C)));
        return (best, e, n);
    }

    /// <summary>The famous view whose standing place is within 10 km of the giver that the hash picks.</summary>
    private static Rewards.PhotoSpot? PostcardSpot(Giver from, long seed)
    {
        Rewards.PhotoSpot? best = null;
        ulong bestScore = ulong.MaxValue;
        void Try(Rewards.PhotoSpot s)
        {
            if (Km(from, s.StandE, s.StandN) > 10) return;
            ulong score = ErrandHash.Of(seed, (long)ErrandHash.Of(s.Id));
            if (score < bestScore) { bestScore = score; best = s; }
        }
        foreach (var s in Rewards.PhotoSpots.Curated) Try(s);
        foreach (var s in Rewards.PhotoSpots.Extra) Try(s);
        return best;
    }

    private static double Km(Giver a, Giver b) => Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N)) / 1000;

    /// <summary>The candidate with the smallest hash score among those passing <paramref name="ok"/>.</summary>
    private static Giver? Pick(IReadOnlyList<Giver> list, long seed, Func<Giver, bool> ok)
    {
        Giver? best = null;
        ulong bestScore = ulong.MaxValue;
        foreach (var g in list)
        {
            if (!ok(g)) continue;
            ulong score = ErrandHash.Of(seed, (long)ErrandHash.Of(g.Id));
            if (score < bestScore) { bestScore = score; best = g; }
        }
        return best;
    }

    private static Giver? Pick(ReadOnlySpan<Giver> list, long seed, Func<Giver, bool> ok)
    {
        Giver? best = null;
        ulong bestScore = ulong.MaxValue;
        foreach (var g in list)
        {
            if (!ok(g)) continue;
            ulong score = ErrandHash.Of(seed, (long)ErrandHash.Of(g.Id));
            if (score < bestScore) { bestScore = score; best = g; }
        }
        return best;
    }

    /// <summary>A trade giver 10-90 km away, another canton first.</summary>
    private static Giver? Far(ReadOnlySpan<Giver> trade, Giver from, long seed)
    {
        Giver? best = null;
        double bestScore = double.MaxValue;
        foreach (var g in trade)
        {
            if (g.Id == from.Id) continue;
            double km = Km(from, g);
            if (km is < 10 or > 90) continue;
            double score = ErrandHash.Unit(ErrandHash.Of(seed, (long)ErrandHash.Of(g.Id))) + (g.Canton == from.Canton ? 1 : 0);
            if (score < bestScore) { bestScore = score; best = g; }
        }
        return best;
    }

    private static readonly PoiKind[] StopKinds = { PoiKind.BusStop, PoiKind.RailStation, PoiKind.ShipLanding };

    /// <summary>Three distinct stops within 15 km, village stops first, ordered by nearest neighbour from the giver.</summary>
    private static Poi[]? Stops(Giver from, PoiCatalogue pois, long seed)
    {
        var near = new List<Poi>();
        var cand = new List<(Poi Poi, ulong Score)>();
        foreach (var kind in StopKinds)
        {
            pois.Near(from.E, from.N, 15000, near, kind);
            foreach (var p in near)
            {
                if (p.Name.Length == 0 || p.Name == from.Name) continue;
                int rank = p.Kind == PoiKind.BusStop && p.Is(PoiFlags.VillageStop) ? 0 : p.Kind == PoiKind.BusStop ? 1 : p.Kind == PoiKind.RailStation ? 2 : 3;
                cand.Add((p, (ulong)rank << 60 | ErrandHash.Of(seed, (long)p.Hash) >> 4));
            }
        }
        cand.Sort((a, b) => a.Score.CompareTo(b.Score));
        var picked = new List<Poi>(3);
        foreach (var (p, _) in cand)
        {
            if (picked.Exists(q => q.Name == p.Name)) continue;
            picked.Add(p);
            if (picked.Count == 3) break;
        }
        if (picked.Count < 3) return null;
        var ordered = new Poi[3];
        double e = from.E, n = from.N;
        for (int i = 0; i < 3; i++)
        {
            int bi = 0;
            double bd = double.MaxValue;
            for (int j = 0; j < picked.Count; j++)
            {
                double dist = (picked[j].E - e) * (picked[j].E - e) + (picked[j].N - n) * (picked[j].N - n);
                if (dist < bd) { bd = dist; bi = j; }
            }
            ordered[i] = picked[bi];
            e = picked[bi].E; n = picked[bi].N;
            picked.RemoveAt(bi);
        }
        return ordered;
    }

    /// <summary>Another landing on the giver's lake, at least 4 km away.</summary>
    private static Poi? Landing(Giver from, PoiCatalogue pois, long seed)
    {
        Poi? lake = null;
        foreach (var l in pois.OfKind(PoiKind.Lake))
            if (PoiCatalogue.InLake(l, from.E, from.N, 300)) { lake = l; break; }
        if (lake == null) return null;
        Poi? best = null;
        ulong bestScore = ulong.MaxValue;
        foreach (var p in pois.OfKind(PoiKind.ShipLanding))
        {
            double de = p.E - from.E, dn = p.N - from.N;
            if (de * de + dn * dn < 4000.0 * 4000 || p.Name == from.Name || !PoiCatalogue.InLake(lake, p.E, p.N, 300)) continue;
            ulong score = ErrandHash.Of(seed, (long)p.Hash);
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>"RacletteCheese" -> "Raclette cheese".</summary>
    public static string Pretty(ItemId id)
    {
        string s = id.ToString();
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i])) sb.Append(' ').Append(char.ToLowerInvariant(s[i]));
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
