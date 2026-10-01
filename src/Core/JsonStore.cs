using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// The one way to persist a JSON file: written to a unique <c>.part</c> next to it, then moved over it,
/// so a crash or a full disk mid-write leaves the previous file whole instead of a truncated one.
/// Callers keep their own try/catch: this throws like <see cref="File.WriteAllText(string, string?)"/>.
/// </summary>
public static class JsonStore
{
    /// <summary>Indented, default naming: shared so a save does not build new options each time.</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Serializes <paramref name="value"/> (UTF-8, no BOM) to <paramref name="path"/>, an OS path or
    /// a <c>user://</c> one, creating its folder. <paramref name="options"/> null = compact, default naming.</summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        path = ProjectSettings.GlobalizePath(path);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // unique, so two saves of the same file at once (a worker thread and the main one) cannot collide
        string tmp = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, options));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
