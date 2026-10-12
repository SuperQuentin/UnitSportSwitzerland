using Godot;

namespace UnitSport.Playtest;

/// <summary>
/// The last lines the engine logged (prints, warnings, errors), kept for Claude's <c>log_tail</c>.
/// Godot calls a logger from any thread, at once, so the ring is locked; it never logs itself.
/// </summary>
public partial class PlaytestLog : Logger
{
    private const int Keep = 400;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    public override void _LogMessage(string message, bool error) => Add(error ? "ERR " + message.TrimEnd() : message.TrimEnd());

    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
        int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces) =>
        Add($"{(errorType == (int)ErrorType.Warning ? "WARN" : "ERROR")} {(string.IsNullOrEmpty(rationale) ? code : rationale)} ({System.IO.Path.GetFileName(file)}:{line} {function})");

    private void Add(string line)
    {
        if (line.Length == 0) return;
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Keep) _lines.Dequeue();
        }
    }

    /// <summary>The last <paramref name="count"/> lines, oldest first, those containing <paramref name="filter"/> if given.</summary>
    public string Tail(int count, string? filter)
    {
        lock (_gate)
        {
            var lines = filter is { Length: > 0 } f
                ? _lines.Where(l => l.Contains(f, StringComparison.OrdinalIgnoreCase))
                : _lines;
            var list = lines.ToList();
            return string.Join("\n", list.Skip(Math.Max(0, list.Count - count)));
        }
    }
}
