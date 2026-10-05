using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// A dialog over a dimmed screen: a text prompt or a confirmation. It takes focus and gives it
/// back to whatever had it when it closes. Esc / B cancels, unless the prompt is mandatory (the
/// first-time player name), in which case the key is still consumed so nothing behind it acts.
/// </summary>
public partial class Modal : Control
{
    private Control? _previousFocus;
    private bool _mandatory;
    private Action? _cancel;
    private LineEdit? _field;

    public static Modal? Current { get; private set; }

    private Modal() { }

    private static Modal Build(Control host, string title, string? message, out VBoxContainer body)
    {
        Current?.QueueFree();
        var m = new Modal { Name = "Modal", Theme = UiTheme.Get(), MouseFilter = MouseFilterEnum.Stop };
        m.SetAnchorsPreset(LayoutPreset.FullRect);
        m._previousFocus = host.GetViewport().GuiGetFocusOwner();

        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.62f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        m.AddChild(dim);

        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        m.AddChild(centre);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(440, 0) };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.96f, 14, 26));
        centre.AddChild(panel);
        body = UiKit.VBox(12);
        panel.AddChild(body);
        body.AddChild(UiKit.Text(title, UiTheme.FontHeading, UiTheme.Text, bold: true));
        if (message != null)
        {
            var msg = UiKit.Text(message, UiTheme.FontSmall, UiTheme.TextDim, wrap: true);
            msg.CustomMinimumSize = new Vector2(388, 0);
            body.AddChild(msg);
        }

        host.AddChild(m);
        Current = m;
        // fade and lift in
        m.Modulate = new Color(1, 1, 1, 0);
        panel.Position += new Vector2(0, 10);
        var tw = m.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(m, "modulate:a", 1f, 0.16f);
        UiFocus.Set(m, true);
        return m;
    }

    /// <summary>
    /// Asks for one line of text. <paramref name="validate"/> returns an error to show, or null
    /// when the text is acceptable; OK stays disabled while it complains.
    /// </summary>
    public static Modal Prompt(Control host, string title, string? message, string initial, string placeholder,
        Func<string, string?> validate, Action<string> ok, Action? cancel = null, bool mandatory = false,
        string okText = "Save", int maxLength = 32)
    {
        var m = Build(host, title, message, out var body);
        m._mandatory = mandatory;
        m._cancel = cancel;
        var field = new LineEdit { Text = initial, PlaceholderText = placeholder, MaxLength = maxLength, SelectAllOnFocus = true };
        field.CustomMinimumSize = new Vector2(0, 42);
        field.AddThemeFontSizeOverride("font_size", 17);
        body.AddChild(field);
        var error = UiKit.Text("", UiTheme.FontSmall, UiTheme.Bad);
        body.AddChild(error);

        var buttons = UiKit.HBox(10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        body.AddChild(buttons);
        if (!mandatory)
        {
            var c = UiKit.Button("Cancel", minWidth: 96);
            c.Pressed += m.Cancel;
            buttons.AddChild(c);
        }
        var okButton = UiKit.Button(okText, primary: true, minWidth: 110);
        buttons.AddChild(okButton);

        void Check()
        {
            string? why = validate(field.Text);
            error.Text = why ?? "";
            okButton.Disabled = why != null;
        }
        void Submit()
        {
            if (validate(field.Text) != null) return;
            string text = field.Text;
            m.CloseModal();
            ok(text);
        }
        field.TextChanged += _ => Check();
        field.TextSubmitted += _ => Submit();
        okButton.Pressed += Submit;
        Check();
        // an empty first-time prompt does not shout before anything was typed
        if (initial.Length == 0) error.Text = "";
        m._field = field;
        field.CallDeferred(Control.MethodName.GrabFocus);
        return m;
    }

    /// <summary>A yes/no question.</summary>
    public static Modal Confirm(Control host, string title, string message, string okText, Action ok,
        Action? cancel = null, bool danger = false, string cancelText = "Cancel")
    {
        var m = Build(host, title, message, out var body);
        m._cancel = cancel;
        var buttons = UiKit.HBox(10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        body.AddChild(UiKit.Spacer(4));
        body.AddChild(buttons);
        var c = UiKit.Button(cancelText, minWidth: 96);
        c.Pressed += m.Cancel;
        buttons.AddChild(c);
        var okButton = UiKit.Button(okText, primary: true, minWidth: 110);
        if (danger)
        {
            okButton.AddThemeStyleboxOverride("normal", UiTheme.Flat(UiTheme.Bad, 8, 18, 8));
            okButton.AddThemeStyleboxOverride("hover", UiTheme.Flat(UiTheme.Bad.Lightened(0.12f), 8, 18, 8));
        }
        okButton.Pressed += () => { m.CloseModal(); ok(); };
        buttons.AddChild(okButton);
        c.CallDeferred(Control.MethodName.GrabFocus);
        return m;
    }

    /// <summary>
    /// A small form: <paramref name="content"/> holds the fields, <paramref name="validate"/>
    /// returns the error to show (OK disabled) or null, re-checked by calling the returned
    /// <c>Recheck</c> action whenever a field changes.
    /// </summary>
    public static (Modal Modal, Action Recheck) Form(Control host, string title, string? message, Control content,
        string okText, Func<string?> validate, Action ok, Control? focus = null)
    {
        var m = Build(host, title, message, out var body);
        body.AddChild(content);
        var error = UiKit.Text("", UiTheme.FontSmall, UiTheme.Bad);
        body.AddChild(error);
        var buttons = UiKit.HBox(10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        body.AddChild(buttons);
        var c = UiKit.Button("Cancel", minWidth: 96);
        c.Pressed += m.Cancel;
        buttons.AddChild(c);
        var okButton = UiKit.Button(okText, primary: true, minWidth: 110);
        buttons.AddChild(okButton);
        void Recheck()
        {
            string? why = validate();
            error.Text = why ?? "";
            okButton.Disabled = why != null;
        }
        okButton.Pressed += () =>
        {
            if (validate() != null) return;
            m.CloseModal();
            ok();
        };
        Recheck();
        (focus ?? okButton).CallDeferred(Control.MethodName.GrabFocus);
        return (m, Recheck);
    }

    /// <summary>A message with one OK button.</summary>
    public static Modal Inform(Control host, string title, string message, Action? done = null)
    {
        var m = Build(host, title, message, out var body);
        m._cancel = done;
        var buttons = UiKit.HBox(10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        body.AddChild(buttons);
        var okButton = UiKit.Button("OK", primary: true, minWidth: 110);
        okButton.Pressed += m.Cancel;
        buttons.AddChild(okButton);
        okButton.CallDeferred(Control.MethodName.GrabFocus);
        return m;
    }

    /// <summary>
    /// A progress bar, a status line under it and Cancel (also Esc / B). The caller fills both and
    /// closes the modal with <see cref="CloseModal"/> when the work ends.
    /// </summary>
    public static Modal Progress(Control host, string title, string? message, Action cancel,
        out ProgressBar bar, out Label status)
    {
        var m = Build(host, title, message, out var body);
        m._cancel = cancel;
        bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 12) };
        body.AddChild(bar);
        status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        body.AddChild(status);
        var buttons = UiKit.HBox(10);
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        body.AddChild(buttons);
        var c = UiKit.Button("Cancel", minWidth: 96);
        c.Pressed += m.Cancel;
        buttons.AddChild(c);
        c.CallDeferred(Control.MethodName.GrabFocus);
        return m;
    }

    private void Cancel()
    {
        CloseModal();
        _cancel?.Invoke();
    }

    public void CloseModal()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
        if (_previousFocus != null && IsInstanceValid(_previousFocus) && _previousFocus.IsVisibleInTree())
            _previousFocus.CallDeferred(Control.MethodName.GrabFocus);
        QueueFree();
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
    }

    // _Input: a modal is above everything, so it answers Esc before any screen behind it
    public override void _Input(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (!e.IsActionPressed(PlayerInput.Menu) && !e.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        if (!_mandatory) Cancel();
    }
}
