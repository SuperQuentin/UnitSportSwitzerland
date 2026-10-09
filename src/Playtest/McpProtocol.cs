using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitSport.Playtest;

/// <summary>What a tool call answers: MCP content blocks (text, PNG images), and whether it failed.</summary>
public sealed record McpToolResult(JsonArray Content, bool IsError = false)
{
    public static McpToolResult Text(string text) => new(new JsonArray(Block(text)));

    public static McpToolResult Error(string text) => new(new JsonArray(Block(text)), IsError: true);

    public static McpToolResult Image(byte[] png, string caption) => new(new JsonArray(
        new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = "image/png" },
        Block(caption)));

    private static JsonObject Block(string text) => new() { ["type"] = "text", ["text"] = text };
}

/// <summary>
/// One tool the server offers: its name, what it does (Claude reads this to choose it), a JSON schema
/// for its arguments, and the call. The call runs on a socket thread; anything that touches the scene
/// tree hops to the main thread itself (<see cref="PlaytestDirector"/>).
/// </summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema,
    Func<JsonElement, CancellationToken, Task<McpToolResult>> Run)
{
    /// <summary>A schema with these properties (name, JSON type, description), the ones in <paramref name="required"/> required.</summary>
    public static JsonObject Schema(params (string Name, string Type, string Description)[] props) => Schema(props, []);

    public static JsonObject Schema((string Name, string Type, string Description)[] props, string[] required)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description) in props)
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }
}

/// <summary>
/// The Model Context Protocol's JSON-RPC side (#751, docs/notes/general/playtest.md), with no transport
/// and no engine: <see cref="Handle"/> takes a request body and returns the reply body (null for a
/// notification). Just the methods a tool server needs: <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c>, <c>tools/call</c>. Pure, so tier 0 tests it (McpProtocolTests).
/// </summary>
public sealed class McpProtocol(string name, string version, string instructions, IReadOnlyList<McpTool> tools)
{
    /// <summary>Protocol revisions this server speaks, newest first; a client asking for another gets the newest.</summary>
    public static readonly string[] Revisions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly Dictionary<string, McpTool> _tools = tools.ToDictionary(t => t.Name);

    /// <summary>A client finished <c>initialize</c> (the playtest panel shows "Claude connected").</summary>
    public event Action<string>? Initialized;

    /// <summary>A tool was called: a client that connected before a game restart never initializes again, it just calls.</summary>
    public event Action<string>? ToolCalled;

    /// <summary>Answers one HTTP body: a request, a notification, or a batch of them.</summary>
    public async Task<string?> Handle(string body, CancellationToken token)
    {
        JsonNode? message;
        try { message = JsonNode.Parse(body); }
        catch (JsonException e) { return Reply(null, error: (-32700, $"parse error: {e.Message}")).ToJsonString(); }

        if (message is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (await One(item, token) is { } reply) replies.Add(reply);
            return replies.Count == 0 ? null : replies.ToJsonString();
        }
        return (await One(message, token))?.ToJsonString();
    }

    private async Task<JsonObject?> One(JsonNode? message, CancellationToken token)
    {
        if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method)
            return Reply(null, error: (-32600, "invalid request"));
        // no id: a notification, never answered (notifications/initialized, notifications/cancelled)
        if (!request.TryGetPropertyValue("id", out var idNode)) return null;
        var id = idNode?.DeepClone();
        var parameters = request["params"] as JsonObject;
        try
        {
            switch (method)
            {
                case "initialize":
                    string asked = parameters?["protocolVersion"]?.GetValue<string>() ?? Revisions[0];
                    string client = parameters?["clientInfo"]?["name"]?.GetValue<string>() ?? "client";
                    Initialized?.Invoke(client);
                    return Reply(id, new JsonObject
                    {
                        ["protocolVersion"] = Revisions.Contains(asked) ? asked : Revisions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version },
                        ["instructions"] = instructions,
                    });
                case "ping":
                    return Reply(id, new JsonObject());
                case "tools/list":
                    return Reply(id, new JsonObject
                    {
                        ["tools"] = new JsonArray(_tools.Values.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = t.InputSchema.DeepClone(),
                        }).ToArray()),
                    });
                case "tools/call":
                    string? tool = parameters?["name"]?.GetValue<string>();
                    if (tool == null || !_tools.TryGetValue(tool, out var t))
                        return Reply(id, error: (-32602, $"unknown tool: {tool}"));
                    ToolCalled?.Invoke(tool);
                    var args = parameters?["arguments"] is JsonObject a
                        ? JsonSerializer.Deserialize<JsonElement>(a.ToJsonString())
                        : JsonSerializer.Deserialize<JsonElement>("{}");
                    McpToolResult result;
                    // a tool that throws is the tool's failure, reported to Claude, not a protocol error
                    try { result = await t.Run(args, token); }
                    catch (Exception e) { result = McpToolResult.Error($"{tool} failed: {e.GetType().Name}: {e.Message}"); }
                    return Reply(id, new JsonObject { ["content"] = result.Content.DeepClone(), ["isError"] = result.IsError });
                default:
                    return Reply(id, error: (-32601, $"method not found: {method}"));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            return Reply(id, error: (-32602, $"invalid params: {e.Message}"));
        }
    }

    private static JsonObject Reply(JsonNode? id, JsonObject? result = null, (int Code, string Message)? error = null)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is { } e) reply["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        else reply["result"] = result ?? new JsonObject();
        return reply;
    }
}

/// <summary>Reading tool arguments: absent or the wrong type falls back to the default.</summary>
public static class McpArgs
{
    public static string? Str(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static double? Num(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool? Bool(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public static JsonElement? Raw(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? v : null;
}
