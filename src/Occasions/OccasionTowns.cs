using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions;

/// <summary>
/// Where the towns are, for anything an occasion puts in a town rather than at a door: a
/// Christmas tree, bats around the church, pumpkin patches on the village edge.
///
/// <para>
/// The towns of <c>places.json</c> when there is one (shipped, or streamed into the cache), plus the
/// villages of the generated fill that stand on generated ground — real and generated terrain sit
/// side by side, and the generated villages have no <c>places.json</c>. The same filter as the
/// church bells in <see cref="Audio.Ambience"/>: towns of at least 20 buildings. Main thread.
/// </para>
/// </summary>
public static class OccasionTowns
{
	public readonly record struct Town(double E, double N, int Buildings, string Name);

	public const int MinBuildings = 20;

	public static IReadOnlyList<Town> All { get; private set; } = Array.Empty<Town>();

	/// <summary>Raised on the main thread when <see cref="All"/> is replaced.</summary>
	public static event Action? Changed;

	private static ProceduralWorld? _generated;
	private static Func<double, double, bool>? _generatedAt;
	private static Task<List<Town>?>? _loading;

	/// <summary>
	/// The generator, and where its ground is actually drawn (<paramref name="generatedAt"/>, read on
	/// a worker thread: it must be lock-free). Null for no generated villages.
	/// </summary>
	public static void UseGenerated(ProceduralWorld? world, Func<double, double, bool>? generatedAt = null)
	{
		_generated = world;
		_generatedAt = generatedAt;
		Reload();
	}

	/// <summary>Re-reads the index — at boot, when places.json streams in, and when the world is replaced.</summary>
	public static void Reload()
	{
		var generated = _generated;
		var generatedAt = _generatedAt;
		_loading = Task.Run(() =>
		{
			var towns = new List<Town>();
			foreach (string dir in new[] { TerrainPaths.FindChunkDir(), TerrainPaths.FindCacheDir() })
			{
				string path = System.IO.Path.Combine(dir, PlaceIndex.FileName);
				if (!System.IO.File.Exists(path)) continue;
				try
				{
					towns = PlaceIndex.FromJson(System.IO.File.ReadAllText(path)).Places
						.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= MinBuildings)
						.Select(p => new Town(p.E, p.N, p.Buildings, p.Name))
						.ToList();
					if (towns.Count > 0) break;
				}
				catch (Exception) { /* a half-written streamed file: try the next */ }
			}
			if (generated != null)
				towns.AddRange(generated.VillageCentres()
					.Where(v => v.Buildings >= MinBuildings && (generatedAt == null || generatedAt(v.E, v.N)))
					.Select(v => new Town(v.E, v.N, v.Buildings, v.Name)));
			return towns;
		});
	}

	/// <summary>Main thread, once a frame by whoever owns occasion content: publishes a finished load.</summary>
	public static void Poll()
	{
		if (_loading is not { IsCompleted: true } t) return;
		_loading = null;
		All = t.IsCompletedSuccessfully && t.Result is { } towns ? towns : Array.Empty<Town>();
		GD.Print($"[occasions] {All.Count} towns for occasion props");
		Changed?.Invoke();
	}

	/// <summary>The towns within <paramref name="radius"/> metres of a point, nearest first.</summary>
	public static IEnumerable<Town> Near(double e, double n, double radius)
	{
		double r2 = radius * radius;
		return All.Where(t => (t.E - e) * (t.E - e) + (t.N - n) * (t.N - n) <= r2)
			.OrderBy(t => (t.E - e) * (t.E - e) + (t.N - n) * (t.N - n));
	}
}
