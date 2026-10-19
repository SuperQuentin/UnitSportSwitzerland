using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// The spectators' screen (#914): a player near an aura battle they are not in sees, at the top,
/// both names and their aura, the round and who performs; while a combo runs, the move on now and
/// its grades; after a turn, its score; at the end, the winner; and how to cheer (the emote wheel's
/// Cheer, Clap or Fist pump count for the crowd bonus). Text only rebuilt when it changes.
/// </summary>
public partial class AuraWatchHud : CanvasLayer
{
    private readonly View _view;

    public AuraWatchHud()
    {
        Layer = 8;
        Name = "AuraWatchHud";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Process(double delta)
    {
        var auras = AuraManager.Client;
        var w = auras?.Current == null ? auras?.Watched : null;
        _view.Visible = w != null;
        if (w == null) return;
        _view.Step(w);
        _view.QueueRedraw();
    }

    private sealed partial class View : Control
    {
        private AuraManager.WatchView? _w;
        private (int, int, int, int, AuraPhase, long, int) _key = (-1, 0, 0, 0, (AuraPhase)255, 0, 0);
        private string _title = "", _score = "", _line = "", _cheer = "";
        private int _shownMove = -2;
        private string _move = "";
        private double _hintsAt = double.NegativeInfinity;

        private static readonly Color Gold = new(0.98f, 0.84f, 0.18f);
        private static readonly Color Violet = new(0.72f, 0.45f, 1f);
        private static readonly Color Red = new(0.9f, 0.25f, 0.2f);
        private static readonly Color Green = new(0.45f, 0.95f, 0.5f);
        private static readonly Color Shadow = new(0, 0, 0, 0.7f);

        public void Step(AuraManager.WatchView w)
        {
            _w = w;
            if (GameClock.Now - _hintsAt > 1.0)
            {
                _hintsAt = GameClock.Now;
                _cheer = InputHints.Format("Cheer them on: {emote_wheel} > Cheer, Clap or Fist pump (it counts)");
            }
            var key = (w.Id, w.TotalA, w.TotalB, w.Turn, w.Phase, w.Winner, w.LaughsA + w.LaughsB);
            if (key != _key)
            {
                _key = key;
                _title = $"AURA BATTLE  ·  ROUND {Math.Min(w.Round, AuraMatch.Rounds)}/{AuraMatch.Rounds}"
                    + (w.Music.Length > 0 ? $"  ·  {w.Music}" : "");
                _score = $"{w.NameA}  {w.TotalA * 100}   vs   {w.TotalB * 100}  {w.NameB}";
                string performer = w.NameOf(w.Performer);
                _line = w.Phase switch
                {
                    AuraPhase.Intro => "They face off...",
                    AuraPhase.Plan => $"{performer} plans a combo",
                    AuraPhase.Countdown => $"{performer} is about to perform",
                    AuraPhase.Perform => $"{performer} performs",
                    AuraPhase.Verdict => $"{w.NameOf(w.LastPerformer)} scores +{w.LastTotal * 100}",
                    _ => w.Winner == 0 ? "A draw!" : $"{w.NameOf(w.Winner)} wins the aura battle!",
                };
            }
            int idx = w.Phase == AuraPhase.Perform ? AuraRules.MoveAt(w.Plan, w.T0, ClockSync.ServerNow, w.Beat) : -1;
            if (idx != _shownMove)
            {
                _shownMove = idx;
                _move = idx >= 0 ? AuraRules.Name(w.Plan[idx]).ToUpperInvariant() : "";
            }
        }

        public override void _Draw()
        {
            if (_w is not { } w) return;
            var size = Size;
            float width = Mathf.Min(560f, size.X - 32f);
            var box = new Rect2(size.X * 0.5f - width * 0.5f, 12f, width, w.Phase == AuraPhase.Perform ? 128f : 100f);
            DrawRect(box, UiTheme.Glass);
            DrawRect(new Rect2(box.Position, new Vector2(box.Size.X, 3f)), Violet);
            Text(_title, box.Position + new Vector2(0, 22f), UiTheme.FontSmall, UiTheme.TextDim, box.Size.X);
            Text(_score, box.Position + new Vector2(0, 48f), UiTheme.FontHeading, Gold, box.Size.X, bold: true);
            Text(_line, box.Position + new Vector2(0, 72f), UiTheme.FontBody, UiTheme.Text, box.Size.X);
            if (w.Phase == AuraPhase.Perform)
            {
                Text(_move, box.Position + new Vector2(0, 96f), UiTheme.FontBody, Violet, box.Size.X, bold: true);
                float pipX = box.Position.X + box.Size.X * 0.5f - (w.Plan.Length - 1) * 11f;
                for (int i = 0; i < w.Plan.Length; i++)
                {
                    var g = i < w.Grades.Length ? w.Grades[i] : AuraGrade.Pending;
                    var c = new Vector2(pipX + i * 22f, box.Position.Y + 114f);
                    DrawCircle(c, 6f, g switch
                    {
                        AuraGrade.Perfect => Gold, AuraGrade.Good => Green, AuraGrade.Ok => UiTheme.Text,
                        AuraGrade.Miss => Red, _ => new Color(0.25f, 0.25f, 0.3f),
                    });
                }
            }
            Text(_cheer, new Vector2(box.Position.X, box.End.Y + 20f), UiTheme.FontSmall, UiTheme.TextDim, box.Size.X);
        }

        private void Text(string s, Vector2 at, int size, Color colour, float width, bool bold = false)
        {
            if (s.Length == 0) return;
            var font = bold ? UiTheme.Bold : UiTheme.Font;
            DrawString(font, at + new Vector2(2f, 2f), s, HorizontalAlignment.Center, width, size, Shadow);
            DrawString(font, at, s, HorizontalAlignment.Center, width, size, colour);
        }
    }
}
