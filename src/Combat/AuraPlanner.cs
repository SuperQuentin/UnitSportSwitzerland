using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// The aura battle's combo planner (#907): open while it is the local player's turn to plan. A card
/// per move (family, beats, difficulty, base aura, and whether it answers the rival's last combo,
/// is stale, or bites it), the combo strip being built, and its estimate part by part from
/// <see cref="AuraRules.Estimate"/>, every press Good. Lock in sends it; when the time runs out
/// whatever is slotted goes (the server pads it to three).
/// <list type="bullet">
/// <item>Mouse: click a card to add it, the buttons for the rest (the pointer is let go while it is open).</item>
/// <item>Keyboard and pad: the cards take focus, so the arrows / stick / D-pad move between them
/// and Enter / A adds one; <c>aura_remove</c> takes the last back, <c>aura_suggest</c> fills in a
/// strong plan, <c>aura_lock</c> locks in.</item>
/// <item>VR: the same buttons through <c>XrPad</c> (stick, A, X, Y, R-stick click).</item>
/// </list>
/// </summary>
public partial class AuraPlanner : CanvasLayer
{
    private readonly List<AuraMove> _plan = new(AuraRules.MaxMoves);
    private readonly List<Button> _cards = new();
    private PanelContainer _panel = null!;
    private Label _title = null!, _rival = null!, _strip = null!, _estimate = null!, _hints = null!;
    private int _openTurn = -1, _openId;
    private bool _sent, _open, _freedMouse;
    private bool _lockHeld, _removeHeld, _suggestHeld;
    private int _shownSecs = -1;
    private AuraContext _ctx = AuraContext.Empty;

    public AuraPlanner()
    {
        Layer = 7;
        Name = "AuraPlanner";
    }

    public override void _Ready()
    {
        _panel = new PanelContainer { Visible = false, Theme = UiTheme.Get() };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel());
        _panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _panel.GrowHorizontal = Control.GrowDirection.Both;
        _panel.GrowVertical = Control.GrowDirection.Begin;
        _panel.Position = new Vector2(-480, -40);
        _panel.CustomMinimumSize = new Vector2(960, 0);
        AddChild(_panel);

        var box = UiKit.VBox(8);
        _panel.AddChild(box);
        _title = UiKit.Text("PLAN YOUR COMBO", UiTheme.FontHeading, UiTheme.Amber);
        box.AddChild(_title);
        _rival = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        box.AddChild(_rival);

        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(grid);
        foreach (var m in AuraRules.Moves)
        {
            var move = m;
            var card = UiKit.Button(AuraRules.Name(m));
            card.CustomMinimumSize = new Vector2(180, 64);
            card.ClipText = true;
            card.Pressed += () => Add(move);
            grid.AddChild(card);
            _cards.Add(card);
        }

        _strip = UiKit.Text("", UiTheme.FontBody, UiTheme.Text);
        box.AddChild(_strip);
        _estimate = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _estimate.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_estimate);

        var row = UiKit.HBox(8);
        box.AddChild(row);
        var back = UiKit.Button("Take back");
        back.Pressed += RemoveLast;
        row.AddChild(back);
        var suggest = UiKit.Button("Suggest");
        suggest.Pressed += Suggest;
        row.AddChild(suggest);
        row.AddChild(UiKit.Spacer(expand: true));
        var lockIn = UiKit.Button("Lock in");
        lockIn.Pressed += Lock;
        row.AddChild(lockIn);
        _hints = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        box.AddChild(_hints);
    }

    public override void _Process(double delta)
    {
        var v = AuraManager.Client?.Current;
        bool should = v is { MyTurn: true, Phase: AuraPhase.Plan } && !(_sent && v.Turn == _openTurn && v.Id == _openId);
        if (should && (!_open || v!.Turn != _openTurn || v.Id != _openId)) Open(v!);
        else if (!should && _open) Close();
        if (!_open || v == null) return;

        int secs = Mathf.CeilToInt(v.Left);
        if (secs != _shownSecs)
        {
            _shownSecs = secs;
            _title.Text = $"PLAN YOUR COMBO  ·  {secs} s";
        }
        // out of time: whatever is slotted goes (a hair early, so it lands inside the grace)
        if (v.Left < 0.15f) Lock();

        bool lockKey = PlayerInput.Held(PlayerInput.AuraLock), removeKey = PlayerInput.Held(PlayerInput.AuraRemove),
            suggestKey = PlayerInput.Held(PlayerInput.AuraSuggest);
        if (lockKey && !_lockHeld) Lock();
        if (removeKey && !_removeHeld) RemoveLast();
        if (suggestKey && !_suggestHeld) Suggest();
        _lockHeld = lockKey; _removeHeld = removeKey; _suggestHeld = suggestKey;
    }

    private void Open(AuraManager.View v)
    {
        _open = true;
        _sent = false;
        _openTurn = v.Turn;
        _openId = v.Id;
        _shownSecs = -1;
        _plan.Clear();
        _ctx = v.ContextForMe();
        _rival.Text = _ctx.RivalLast.Length > 0
            ? $"{v.OpponentName} just did: {AuraManager.Combo(_ctx.RivalLast)}. Answer it, and don't copy it."
            : "You open the battle. Pick 3 to 6 moves, at most 16 beats; mix the families, chain them, end on a finisher.";
        _hints.Text = InputHints.Format("{ui_accept} add  ·  {aura_remove} take back  ·  {aura_suggest} suggest  ·  {aura_lock} lock in");
        _lockHeld = PlayerInput.Held(PlayerInput.AuraLock);
        _removeHeld = PlayerInput.Held(PlayerInput.AuraRemove);
        _suggestHeld = PlayerInput.Held(PlayerInput.AuraSuggest);
        _panel.Visible = true;
        if (!XR.XrSession.Active && Input.MouseMode != Input.MouseModeEnum.Visible)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _freedMouse = true;
        }
        Refresh();
        _cards[0].GrabFocus();
    }

    private void Close()
    {
        _open = false;
        _panel.Visible = false;
        if (_freedMouse)
        {
            _freedMouse = false;
            MouseCapture.Capture();
        }
    }

    private void Add(AuraMove m)
    {
        if (!_open || _plan.Count >= AuraRules.MaxMoves) return;
        if (AuraRules.Beats(_plan) + AuraRules.Def(m).Beats > AuraRules.MaxBeats) return;
        _plan.Add(m);
        Refresh();
    }

    private void RemoveLast()
    {
        if (!_open || _plan.Count == 0) return;
        _plan.RemoveAt(_plan.Count - 1);
        Refresh();
    }

    private void Suggest()
    {
        if (!_open) return;
        _plan.Clear();
        _plan.AddRange(AuraRules.SuggestPlan(_ctx));
        Refresh();
    }

    private void Lock()
    {
        if (!_open || _sent) return;
        _sent = true;
        AuraManager.Client?.SendPlan(_plan);
        Close();
    }

    /// <summary>The cards' badges, the strip and the estimate: only when the plan changes.</summary>
    private void Refresh()
    {
        int beats = AuraRules.Beats(_plan);
        for (int i = 0; i < _cards.Count; i++)
        {
            var m = AuraRules.Moves[i];
            var d = AuraRules.Def(m);
            var sb = new StringBuilder();
            sb.Append(d.Name).Append('\n').Append(d.Family).Append(" · ").Append(d.Beats).Append(" beats · ")
                .Append(new string('*', d.Difficulty)).Append(" · ").Append(d.Base);
            string badge = "";
            foreach (var theirs in _ctx.RivalLast)
                if (AuraRules.Counters(m, theirs)) { badge = $"ANSWERS {AuraRules.Name(theirs)}"; break; }
            if (badge.Length == 0 && Array.IndexOf(_ctx.RivalLast, m) >= 0) badge = "BITING x0.5";
            if (badge.Length == 0 && Array.IndexOf(_ctx.OwnPrevious, m) >= 0) badge = "STALE x0.5";
            else if (badge.Length == 0 && Array.IndexOf(_ctx.OwnEarlier, m) >= 0) badge = "STALE x0.7";
            _cards[i].Text = sb.ToString();
            _cards[i].TooltipText = badge.Length > 0 ? badge : d.Tags.ToString();
            _cards[i].Modulate = badge.StartsWith("ANSWERS") ? new Color(1f, 0.95f, 0.6f)
                : badge.Length > 0 ? new Color(0.75f, 0.75f, 0.8f) : Colors.White;
            _cards[i].Disabled = _plan.Count >= AuraRules.MaxMoves || beats + d.Beats > AuraRules.MaxBeats;
        }

        _strip.Text = _plan.Count == 0
            ? $"Combo: (empty)  ·  0/{AuraRules.MaxBeats} beats"
            : $"Combo: {AuraManager.Combo(_plan.ToArray())}  ·  {beats}/{AuraRules.MaxBeats} beats";
        if (_plan.Count == 0)
        {
            _estimate.Text = "";
            return;
        }
        var s = AuraRules.Estimate(_plan, _ctx);
        var e = new StringBuilder();
        e.Append("Estimate, every cut Good: ").Append(s.Total * 100).Append(" aura  (");
        bool first = true;
        for (int i = 0; i < AuraScore.PartCount; i++)
        {
            float p = s.Part(i);
            if (Mathf.Abs(p) < 0.05f || i == AuraScore.PartCount - 1) continue;
            if (!first) e.Append(", ");
            first = false;
            e.Append(AuraScore.PartNames[i]).Append(' ').Append(p > 0 ? "+" : "").Append(p.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }
        e.Append(')');
        if (_plan.Count < AuraRules.MinMoves) e.Append($"  ·  {AuraRules.MinMoves - _plan.Count} more to go (stand tall pads it)");
        _estimate.Text = e.ToString();
    }

    public override void _ExitTree()
    {
        if (_open) Close();
    }
}
