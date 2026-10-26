using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitSport.Adventure;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/progress-store.md).

/// <summary>The serializer options of every adventure file and section: one static instance (core/json-store).</summary>
public static class AdventureJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        WriteIndented = false,
    };

    /// <summary>Where warnings go (a bad section, a downgrade): <c>GD.PushWarning</c> in the game, a list in the tests.</summary>
    public static Action<string>? Warn { get; set; }

    internal static void Warning(string what) => Warn?.Invoke(what);
}

/// <summary>Untyped view of a <see cref="SectionKey{T}"/>, for the progress store.</summary>
public interface ISectionKey
{
    string Id { get; }
    int Version { get; }
    Type Shape { get; }
    object Decode(JsonNode? data, int version);
    JsonNode? Encode(object value);
}

/// <summary>
/// A system's handle on its slice of a player's progress (#1223). Declared in the owning system's own
/// file, never in a central list:
/// <code>public static readonly SectionKey&lt;QuestsProgress&gt; Section = new("quests", Version: 1);</code>
/// <see cref="Id"/> is the stable lower-case JSON key, <see cref="Version"/> the shape's version and
/// <see cref="Migrate"/> turns the data of an older version into the current <typeparamref name="T"/>
/// (null: deserialized as it is). <typeparamref name="T"/> is a plain DTO, serialized with
/// <see cref="AdventureJson.Options"/>.
/// </summary>
public sealed record SectionKey<T>(string Id, int Version, Func<JsonNode, int, T?>? Migrate = null) : ISectionKey
    where T : class, new()
{
    public Type Shape => typeof(T);

    /// <summary>The section's data as <typeparamref name="T"/>: migrated when <paramref name="version"/> is older, a new one when absent.</summary>
    public T DecodeTyped(JsonNode? data, int version)
    {
        if (data == null) return new T();
        if (version < Version && Migrate != null) return Migrate(data, version) ?? new T();
        return data.Deserialize<T>(AdventureJson.Options) ?? new T();
    }

    public object Decode(JsonNode? data, int version) => DecodeTyped(data, version);

    public JsonNode? Encode(object value) => JsonSerializer.SerializeToNode((T)value, AdventureJson.Options);
}
