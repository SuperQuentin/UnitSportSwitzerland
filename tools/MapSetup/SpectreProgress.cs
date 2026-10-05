using Spectre.Console;
using UnitSport.Map;

namespace UnitSport.Tools.MapSetup;

/// <summary>
/// The terminal tool's <see cref="IStepProgress"/>: a step's progress on a Spectre bar, with its
/// latest line beside it. The escaping lives here rather than in MapCore — a step's output is
/// arbitrary text (file names with brackets in them), and only this renderer treats it as markup.
/// The log file is written by <see cref="StepRun"/> itself, so <see cref="Log"/> has nothing to do.
/// </summary>
public sealed class SpectreProgress : IStepProgress
{
    private readonly ProgressTask _task;
    private readonly string _title;

    public SpectreProgress(ProgressTask task, string title)
    {
        _task = task;
        _title = title;
    }

    public double Value { set => _task.Value = value; }

    public void Show(string text) => _task.Description = $"{Markup.Escape(_title)} [grey]{Markup.Escape(text)}[/]";

    public void Log(string line) { }
}
