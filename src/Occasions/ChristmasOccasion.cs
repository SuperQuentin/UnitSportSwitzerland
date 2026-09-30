using Godot;
using UnitSport.Avatar;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;
using static UnitSport.Occasions.PropColors;

namespace UnitSport.Occasions;

/// <summary>
/// Christmas: a short, low winter day, the whole country under snow and more falling, lights along
/// the eaves, a decorated fir in every town with gifts under it, sleigh bells at dusk and a carol
/// from the church before the hour.
/// </summary>
public sealed class ChristmasOccasion : Occasion
{
	public override string Id => OccasionIds.Christmas;
	public override string Title => "Christmas";

	// ---- atmosphere ----------------------------------------------------------------------------

	/// <summary>Late December: up after eight, down before five, the sun barely 20° at noon.</summary>
	public override OccasionAtmosphere Atmosphere { get; } = new()
	{
		Sunrise = 8.17f,
		Sunset = 16.67f,
		NoonElevation = 20f,
		Snow = 1f,
		Lights = 1f,
		Snowfall = 0.6f,
		MistColor = new Color(0.80f, 0.84f, 0.92f),
		MistDay = 0.00008f,
		MistNight = 0.00025f,
		MistHeight = 35f,
	};

	public override (Color Tint, Color Sky) Grade(float el, Color tint, Color sky)
	{
		float day = Mathf.SmoothStep(8f, 18f, el);
		float golden = Mathf.SmoothStep(-2f, 2f, el) * (1f - Mathf.SmoothStep(6f, 14f, el));
		float dusk = Mathf.SmoothStep(-11f, -5f, el) * (1f - Mathf.SmoothStep(-2f, 1f, el));
		float night = 1f - Mathf.SmoothStep(-12f, -7f, el);

		// a cold, clear winter day: bluer light, a bright pale sky
		tint *= new Color(1f - 0.07f * day, 1f - 0.03f * day, 1f);
		sky = sky.Lerp(new Color(0.78f, 0.85f, 0.93f), 0.4f * day);

		// the low sun is pink rather than gold
		tint = tint.Lerp(new Color(1f, 0.80f, 0.72f), 0.3f * golden);
		sky = sky.Lerp(new Color(0.95f, 0.62f, 0.55f), 0.45f * golden);

		// a deep blue dusk, and a night the snow keeps from going black
		tint = tint.Lerp(new Color(0.42f, 0.48f, 0.78f), 0.6f * dusk);
		sky = sky.Lerp(new Color(0.12f, 0.18f, 0.40f), 0.7f * dusk);
		tint = tint.Lerp(new Color(0.38f, 0.44f, 0.60f), 0.6f * night);
		sky = sky.Lerp(new Color(0.02f, 0.03f, 0.08f), 0.8f * night);
		return (tint, sky);
	}

	// ---- loot, hunt, hat -----------------------------------------------------------------------

	private static readonly Items.ItemId[] Baking = [Items.ItemId.Biberli, Items.ItemId.Mandarin, Items.ItemId.Grittibaenz];

	/// <summary>Biberli, mandarins and a Grittibänz in the kitchens; Glühwein in the fridge.</summary>
	public override (float Chance, Items.ItemId[] Items)? Treats(FurnitureType type) => type switch
	{
		FurnitureType.Counter or FurnitureType.Nightstand or FurnitureType.ShopCounter => (0.4f, Baking),
		FurnitureType.Fridge or FurnitureType.Stove => (0.3f, [Items.ItemId.Gluehwein, Items.ItemId.Mandarin]),
		_ => null,
	};

	public override (Items.ItemId Id, int Count) HuntReward(Random rng)
	{
		double r = rng.NextDouble();
		if (r < 0.03) return (Items.ItemId.ReindeerAntlers, 1);
		if (r < 0.28) return (Items.ItemId.Gluehwein, 1);
		return (Baking[rng.Next(Baking.Length)], rng.Next(2, 4));
	}

	public override Headwear Hat => Headwear.SantaHat;

	// ---- sound ---------------------------------------------------------------------------------

	private double _sleighNext;
	private int _carolFor = -1;

	/// <summary>
	/// A sleigh jingling past near the village from dusk on, and half a minute before every hour
	/// the nearest church rings the chorus of "Jingle Bells" before its ordinary strokes.
	/// </summary>
	public override void Ambience(OccasionAudio a)
	{
		if (a.Night > 0.35f && a.Now >= _sleighNext)
		{
			_sleighNext = a.Now + a.Rand(40f, 90f);
			if (a.Bank("sleigh", OccasionSounds.SleighBells) is { } sleigh && a.NearestTown(900f) is { } town
				&& a.Spot(60f, 200f) is { } s)
				a.Speak(sleigh, s + Vector3.Up * 1.5f, a.Rand(0.94f, 1.06f), -4f, 25f, 500f);
		}

		var clock = a.Clock;
		int hourKey = clock.DayOfYear * 24 + clock.Hour;
		if (clock is { Minute: 59, Second: >= 25 } && _carolFor != hourKey
			&& a.Bank("carol", OccasionSounds.Carol, 1) is { } carol)
		{
			_carolFor = hourKey;
			if (a.NearestTown(2400f) is { } church)
				a.Speak(carol, church + Vector3.Up * 22f, 1f, 2f, 90f, 2400f);
		}
	}

	public override float[] Jingle() => OccasionSounds.ChristmasJingle();

	// ---- props ---------------------------------------------------------------------------------

	private static readonly Color Needles = Matte(0.10f, 0.26f, 0.14f);
	private static readonly Color Bark = Matte(0.30f, 0.20f, 0.12f);

	/// <summary>
	/// A 12 m town fir: four stacked tiers on a trunk, strung with baubles in four colours and a
	/// star on top. The baubles and the star are lamps (alpha 1): lit at night.
	/// </summary>
	public static readonly PropKind TownTree = new("ChristmasTree", () =>
	{
		var s = new MeshScratch();
		s.Tube(new Vector3(0, 0, 0), new Vector3(0, 2.0f, 0), 0.35f, 0.3f, Bark, 6);
		(float Base, float Top, float Radius)[] tiers = [(1.2f, 5.0f, 3.2f), (3.6f, 7.4f, 2.6f), (5.8f, 9.6f, 1.9f), (8.0f, 11.6f, 1.2f)];
		foreach (var (b, t, r) in tiers) s.Tube(new Vector3(0, b, 0), new Vector3(0, t, 0), r, 0.05f, Needles, 8);

		Color[] baubles = [Lamp(0.95f, 0.10f, 0.10f), Lamp(1.0f, 0.78f, 0.20f), Lamp(0.25f, 0.45f, 1.0f), Lamp(0.95f, 0.95f, 0.95f)];
		var rng = new Random(2512);
		int i = 0;
		foreach (var (b, t, r) in tiers)
			for (int k = 0; k < 9; k++, i++)
			{
				float h = b + (t - b) * (0.15f + 0.5f * (float)rng.NextDouble());
				float radius = r * (1f - (h - b) / (t - b)) + 0.08f;
				float a = k * Mathf.Tau / 9f + (float)rng.NextDouble() * 0.4f;
				s.Box(new Vector3(Mathf.Cos(a) * radius, h, Mathf.Sin(a) * radius), new Vector3(0.22f, 0.22f, 0.22f),
					baubles[i % baubles.Length], new Basis(Vector3.Up, a));
			}
		var gold = Lamp(1.0f, 0.85f, 0.3f);
		s.Box(new Vector3(0, 12.0f, 0), new Vector3(0.7f, 0.7f, 0.12f), gold, new Basis(Vector3.Back, Mathf.Pi / 4));
		s.Box(new Vector3(0, 12.0f, 0), new Vector3(0.12f, 0.9f, 0.12f), gold);
		return s.Build();
	}, candle: false);

	private static PropKind Gift(string name, Color paper, Color ribbon, float w, float h, float d) => new(name, () =>
	{
		var s = new MeshScratch();
		s.Box(new Vector3(0, h / 2, 0), new Vector3(w, h, d), paper);
		s.Box(new Vector3(0, h / 2, 0), new Vector3(w + 0.01f, h + 0.01f, 0.06f), ribbon);
		s.Box(new Vector3(0, h / 2, 0), new Vector3(0.06f, h + 0.01f, d + 0.01f), ribbon);
		s.Box(new Vector3(0, h + 0.04f, 0), new Vector3(0.16f, 0.07f, 0.08f), ribbon, new Basis(Vector3.Up, 0.7f));
		return s.Build();
	}, candle: false);

	public static readonly PropKind[] Gifts =
	[
		Gift("GiftRed", Matte(0.80f, 0.10f, 0.12f), Matte(0.95f, 0.80f, 0.25f), 0.45f, 0.32f, 0.40f),
		Gift("GiftGreen", Matte(0.12f, 0.50f, 0.22f), Matte(0.85f, 0.12f, 0.12f), 0.38f, 0.38f, 0.38f),
		Gift("GiftBlue", Matte(0.20f, 0.35f, 0.80f), Matte(0.95f, 0.95f, 0.95f), 0.55f, 0.22f, 0.35f),
	];

	private const uint Salt = 0xC4215;

	/// <summary>
	/// Where a town's tree stands: the town point nudged along a spiral to the nearest spot that is
	/// dry, fairly flat, clear of the carriageway and not against a house. Deterministic, so the
	/// tree (Decorations) and its gifts (Hunt) agree without talking to each other.
	/// </summary>
	private static Vector3? TreeSpot(TileContext t, OccasionTowns.Town town)
	{
		if (TileId.FromLv95(town.E, town.N) != t.Id) return null;   // each tree belongs to one tile
		var centre = t.Local(town.E, town.N);
		for (int ring = 0; ring <= 15; ring++)
			for (int k = 0; k < Math.Max(1, ring * 6); k++)
			{
				float a = k * Mathf.Tau / Math.Max(1, ring * 6), r = ring * 4f;
				var p = centre + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
				if (p.X < 4 || p.X > 996 || p.Z < 4 || p.Z > 996) continue;
				if (t.HeightAt(p) is not { } h || t.CoverAt(p) is CoverClass.Water or null) continue;
				float lo = h, hi = h;
				foreach (var o in new[] { new Vector3(3, 0, 0), new Vector3(-3, 0, 0), new Vector3(0, 0, 3), new Vector3(0, 0, -3) })
					if (t.HeightAt(p + o) is { } g) { lo = Mathf.Min(lo, g); hi = Mathf.Max(hi, g); }
				if (hi - lo > 1.2f || t.DistanceToRoad(p) < 5f) continue;
				if (t.Doors.Any(d => new Vector2(d.Position.X - p.X, d.Position.Z - p.Z).Length() < 11f)) continue;
				return p with { Y = h };
			}
		return null;
	}

	private static int TownKey(OccasionTowns.Town town) => unchecked((int)town.E * 31 + (int)town.N);

	private static bool GiftDoor(TileContext t, DoorSpot d)
	{
		var (a, b, c) = t.DoorKey(d);
		return d.Width > 0 && OccasionHash.Unit(a, b, c, Salt) < 0.05f;
	}

	public override void Decorate(TileContext t, DecorBuilder into)
	{
		foreach (var town in t.Towns)
			if (TreeSpot(t, town) is { } p)
				into.Add(TownTree, new Transform3D(new Basis(Vector3.Up, OccasionHash.Unit(TownKey(town), 0, 0, Salt) * Mathf.Tau), p));
	}

	public override void PlaceHunt(TileContext t, DecorBuilder into)
	{
		// five gifts under every town tree
		foreach (var town in t.Towns)
		{
			if (TreeSpot(t, town) is not { } p) continue;
			int key = TownKey(town);
			for (int k = 0; k < 5; k++)
			{
				float a = k * Mathf.Tau / 5f + OccasionHash.Unit(key, k, 0, Salt + 1) * 0.5f;
				float r = 3.4f + OccasionHash.Unit(key, k, 1, Salt + 1) * 0.6f;
				var g = p + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
				if (t.HeightAt(g) is { } h) g.Y = h;
				var kind = Gifts[(int)(OccasionHash.Unit(key, k, 2, Salt + 1) * Gifts.Length) % Gifts.Length];
				into.AddHuntSpot(new HuntSpot($"{Id}:tree{key}_{k}", Id, g, "a gift"),
					kind, new Transform3D(new Basis(Vector3.Up, a * 1.7f), g));
			}
		}

		// and one on about one doorstep in twenty
		foreach (var d in t.Doors)
		{
			if (!GiftDoor(t, d)) continue;
			var (a, b, c) = t.DoorKey(d);
			var outward = new Vector3(d.Outward.X, 0, d.Outward.Z).Normalized();
			var along = new Vector3(-outward.Z, 0, outward.X);
			var p = d.Position + outward * 0.6f + along * (d.Width * 0.5f + 0.35f);
			if (t.HeightAt(p) is { } h && h > d.Position.Y - 0.6f) p.Y = h;
			var kind = Gifts[(int)(OccasionHash.Unit(a, b, c, Salt + 2) * Gifts.Length) % Gifts.Length];
			into.AddHuntSpot(new HuntSpot($"{Id}:{a}_{b}_{c}", Id, p, "a gift"),
				kind, new Transform3D(TileContext.Facing(d.Outward), p));
		}
	}
}
