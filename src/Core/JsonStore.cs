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
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null) =>
        SaveQueue.WriteAtomic(ProjectSettings.GlobalizePath(path), JsonSerializer.Serialize(value, options));

    /// <summary>
    /// Like <see cref="Save"/>, but only the serializing happens now (the snapshot: later changes to
    /// <paramref name="value"/> are not in it); the file is written by <see cref="SaveQueue"/>'s
    /// background writer, in order, last save per file wins, flushed on quit. For saves made while
    /// playing (a plant, a claim, a deposit). Serializing errors throw here; write errors go to
    /// <paramref name="onError"/>, on the writer thread.
    /// </summary>
    public static void SaveAsync<T>(string path, T value, JsonSerializerOptions? options = null, Action<Exception>? onError = null) =>
        SaveQueue.Enqueue(ProjectSettings.GlobalizePath(path), JsonSerializer.Serialize(value, options), onError);
}
