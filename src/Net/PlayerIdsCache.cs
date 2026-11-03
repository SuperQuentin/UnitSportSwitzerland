using System.Text.Json;
using Godot;

namespace UnitSport.Net;

/// <summary>
/// Server: <see cref="PlayerIds"/>'s cache of names, <c>user://players/names.json</c> (id to the last name seen, #1359).
/// Only shown for players who are away; nothing is keyed by it.
/// </summary>
public static class PlayerIdsCache
{
    private const string File = "user://players/names.json";

    /// <summary>The server's ids with the saved cache, writing it back on each change; <paramref name="persist"/> false keeps it in memory (a check's scratch run).</summary>
    public static PlayerIds Create(bool persist = true) => persist ? new PlayerIds(Load(), Save) : new PlayerIds();

    private static Dictionary<string, string>? Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(File);
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[ids] {File} unreadable: {e.Message}");
            return null;
        }
    }

    // a copy: the write runs off the main thread while the cache may change again
    private static void Save(IReadOnlyDictionary<string, string> names) =>
        Core.JsonStore.SaveAsync(File, new Dictionary<string, string>(names), Core.JsonStore.Indented);
}
