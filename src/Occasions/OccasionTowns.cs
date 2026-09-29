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
/// <c>places.json</c> when there is one (shipped, or streamed into the cache), else the villages
/// of the generated stand-in world, which has no <c>places.json</c>. The same filter as the
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
	private static Task<List<Town>?>? _loading;

	/// <summary>The generated world while it stands in for real terrain; null once it is retired.</summary>
	public static void UseGenerated(ProceduralWorld? world)
	{
		_generated = world;
		Reload();
	}

	/// <summary>Re-reads the index — at boot, when places.json streams in, and when the world is replaced.</summary>
	public static void Reload()
	{
		var generated = _generated;
		_loading = Task.Run(() =>
		{
			foreach (string dir in new[] { TerrainPaths.FindChunkDir(), TerrainPaths.FindCacheDir() })
			{
				string path = System.IO.Path.Combine(dir, PlaceIndex.FileName);
				if (!System.IO.File.Exists(path)) continue;
				try
				{
					var towns = PlaceIndex.FromJson(System.IO.File.ReadAllText(path)).Places
						.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= MinBuildings)
						.Select(p => new Town(p.E, p.N, p.Buildings, p.Name))
						.ToList();
					if (towns.Count > 0) return towns;
				}
				catch (Exception) { /* a half-written streamed file: try the next, or the generated world */ }
			}
			return generated?.VillageCentres()
				.Where(v => v.Buildings >= MinBuildings)
				.Select(v => new Town(v.E, v.N, v.Buildings, v.Name))
				.ToList();
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
