using Godot;
using UnitSport.Core;
using UnitSport.Playtest.Recording;
using UnitSport.Terrain;

namespace UnitSport.Playtest.TopView;

/// <summary>
/// A top view as a picture (#844, #851): the ground's raster with the shapes' SVG rasterised by
/// ThorVG (headless too) and blended over it. And where a session's ground comes from offline.
/// </summary>
public static class TopViewImage
{
    public static Image? Render(TopViewShapes shapes, TopGround? ground, int px)
    {
        var overlay = new Image();
        if (overlay.LoadSvgFromString(TopViewSvg.Render(shapes, px, pixelText: true, ground), 1f) != Error.Ok) return null;
        if (ground == null || ground.Px != px) return overlay;
        var image = Image.CreateFromData(px, px, false, Image.Format.Rgba8, ground.Rgba);
        overlay.Convert(Image.Format.Rgba8);
        image.BlendRect(overlay, new Rect2I(0, 0, px, px), Vector2I.Zero);
        return image;
    }

    public static Error Save(TopViewShapes shapes, TopGround? ground, int px, string path) =>
        Render(shapes, ground, px) is { } image ? image.SavePng(path) : Error.Failed;

    /// <summary>The ground under <paramref name="shapes"/>' square, or null without a source or with no tile there.</summary>
    public static TopGround? Ground(IChunkSource? source, TopViewShapes shapes, int px)
    {
        if (source == null) return null;
        try
        {
            // on the pool: blocking the main thread on a read that resumes on the main thread would never end
            var ground = Task.Run(() => TopViewGround.LoadAsync(source, shapes.Min, shapes.Side, px)).GetAwaiter().GetResult();
            return ground.Tiles > 0 ? ground : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[topview] no ground: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The ground a recorded session ran on, from its first <c>session start</c> event (#851): the
    /// fixture course and where it was laid, or the tile folder. <paramref name="chunks"/> overrides
    /// it as <c>--chunks</c> does the game's (<c>fixture:&lt;course&gt;</c> or a folder).
    /// </summary>
    public static IChunkSource? SourceOf(string sessionDir, string? chunks)
    {
        var start = SessionReport.ReadLines(Path.Combine(sessionDir, SessionReport.EventsFile))
            .FirstOrDefault(e => e.Type == "session" && e.Str("what") == "start");
        string? spec = chunks ?? (start?.Str("ground") is { Length: > 0 } g ? g : null);
        if (spec == null) return null;
        if (spec.StartsWith(Systems.FixturePrefix, StringComparison.Ordinal))
        {
            double e = start?.Num("startE") ?? SpawnPoint.DefaultLv95E, n = start?.Num("startN") ?? SpawnPoint.DefaultLv95N;
            return Terrain.Fixture.FixtureChunkSource.Create(spec[Systems.FixturePrefix.Length..], e, n);
        }
        return Directory.Exists(spec) ? new LocalChunkSource(spec) : null;
    }

    /// <summary>What a recorder writes into its start event so the ground can be found again offline.</summary>
    public static (string Key, object? Value)[] Describe(string spec, (double E, double N) start) =>
    [
        ("ground", spec),
        ("startE", Math.Round(start.E, 2)),
        ("startN", Math.Round(start.N, 2)),
    ];
}

/// <summary>
/// Where a session's ground comes from (#851): the live source (null on a server, which draws no
/// pictures), and how to find it again offline: <c>fixture:&lt;course&gt;</c> and where it was laid, or the tile folder.
/// </summary>
public sealed record GroundSpec(IChunkSource? Source, string Spec, double StartE, double StartN);
