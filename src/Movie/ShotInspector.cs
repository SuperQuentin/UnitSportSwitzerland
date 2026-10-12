using System.Globalization;
using Godot;
using UnitSport.Ui;

namespace UnitSport.Movie;

/// <summary>
/// The picked shot's settings (#777), beside the player on the Shots screen: its name and group, its rig
/// and the actor it follows, where it hangs (distance, height, side) and how smoothly, its lens and aim,
/// its shake and its beat links, plus "Frame from view" and "Add move". Built again when another shot is
/// picked or the rig changes; a slider writes straight into the shot, so the player shows it at once.
/// </summary>
public partial class ShotInspector : ScrollContainer
{
    private readonly Func<MovieProject> _project;
    private readonly Func<double> _time;
    private readonly VBoxContainer _box;
    private int _index = -1;

    /// <summary>"Frame from view" and "Add move", which need the studio's view.</summary>
    public event Action? FrameFromView, AddMove;
    /// <summary>Something about the shot changed: the stills and gizmos follow.</summary>
    public event Action? Changed;

    private static readonly string[] Rigs = { "World", "Lead", "Chase", "Mount" };
    private static readonly string[] Parts = { "Body", "Head", "Ride" };
    private static readonly string[] Styles = { "Off", "Handheld", "Walking", "Engine", "Impact" };
    private static readonly string[] Params = { "Lens", "Shake", "Distance", "Height", "Roll", "Orbit", "Fade" };
    private static readonly string[] Shapes = { "Smooth pulse", "Step", "Ramp up" };
    private static readonly string[] Everies = { "Markers", "Every beat", "Every 2", "Every bar" };
    private static readonly int[] EveryValues = { 0, 1, 2, 4 };

    public ShotInspector(Func<MovieProject> project, Func<double> time)
    {
        _project = project;
        _time = time;
        CustomMinimumSize = new Vector2(330, 0);
        HorizontalScrollMode = ScrollMode.Disabled;
        _box = UiKit.VBox(5);
        _box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddChild(_box);
    }

    public int Index => _index;

    /// <summary>Shows shot <paramref name="index"/>; built again even if it is the one shown (after an edit elsewhere).</summary>
    public void Show(int index)
    {
        _index = index;
        foreach (var child in _box.GetChildren()) child.QueueFree();
        var p = _project();
        if (index < 0 || index >= p.Cameras.Count) return;
        var cam = p.Cameras[index];

        // name and group
        var name = new LineEdit { Text = cam.Name, PlaceholderText = "Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        name.TextChanged += s => { if (s.Trim().Length > 0) { cam.Name = s.Trim(); Changed?.Invoke(); } };
        Row("Name", name);
        var group = new LineEdit { Text = cam.Group, PlaceholderText = "Group (Take-off, Landing…)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        group.TextSubmitted += s => { cam.Group = s.Trim(); Changed?.Invoke(); };
        group.FocusExited += () => { if (cam.Group != group.Text.Trim()) { cam.Group = group.Text.Trim(); Changed?.Invoke(); } };
        Row("Group", group);

        // the rig: four buttons, one lit
        Heading("Rig");
        var rigs = UiKit.HBox(4);
        var rigGroup = new ButtonGroup();
        for (int r = 0; r < Rigs.Length; r++)
        {
            int rig = r;
            var b = SmallButton(Rigs[r], RigTip((ShotRig)r));
            b.ToggleMode = true;
            b.ButtonGroup = rigGroup;
            b.ButtonPressed = (int)cam.Rig == r;
            b.Pressed += () =>
            {
                if (cam.Rig == (ShotRig)rig) return;
                SetRig(p, cam, (ShotRig)rig);
                Changed?.Invoke();
                Callable.From(() => Show(_index)).CallDeferred();
            };
            rigs.AddChild(b);
        }
        _box.AddChild(rigs);

        if (cam.Rig != ShotRig.World)
        {
            var follows = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "The actor the camera moves with" };
            follows.AddItem("Nobody (stays in the world)");
            foreach (var lane in p.Lanes) follows.AddItem(lane.Label);
            follows.Selected = Math.Clamp(cam.Target + 1, 0, follows.ItemCount - 1);
            follows.ItemSelected += i => { cam.Target = (int)i - 1; Changed?.Invoke(); Callable.From(() => Show(_index)).CallDeferred(); };
            Row("Follows", follows);
            var part = Options(Parts, (int)cam.Part, i => { cam.Part = (TargetPart)i; Changed?.Invoke(); }, "What of the actor: its body, its head, its vehicle");
            Row("Part", part);
        }

        if (cam.Relative)
        {
            // where it hangs, in the actor's frame: the key at the playhead, or the rig's default made one
            var key = KeyHere(cam);
            float sign = key.N < 0 ? -1 : key.N > 0 ? 1 : cam.Rig == ShotRig.Lead ? -1 : 1;
            Slider("Distance", 0, 40, 0.1, Math.Abs(key.N), v => { KeyHere(cam).N = sign * v; Changed?.Invoke(); }, v => $"{v:F1} m",
                cam.Rig == ShotRig.Lead ? "How far in front of the actor" : "How far behind the actor");
            Slider("Height", -5, 30, 0.1, key.Alt, v => { KeyHere(cam).Alt = v; Changed?.Invoke(); }, v => $"{v:F1} m", "Above the followed part");
            Slider("Side", -20, 20, 0.1, key.E, v => { KeyHere(cam).E = v; Changed?.Invoke(); }, v => $"{v:+0.0;-0.0;0} m", "Right (+) or left (−) of the actor");
            if (cam.Rig != ShotRig.Mount)
                Slider("Smoothing", 0, 3, 0.05, cam.Smoothing, v => { cam.Smoothing = (float)v; Changed?.Invoke(); }, v => v < 0.01 ? "rigid" : $"{v:F2} s",
                    "How much the camera lags behind the actor's moves");
        }

        // lens and aim, on the key at the playhead
        Heading("Lens and aim");
        var lensKey = cam.Keys.Count > 0 ? KeyAtOrBefore(cam) : null;
        Slider("Lens", CameraTrack.MinLens, CameraTrack.MaxLens, 1, lensKey?.Lens ?? 35, v =>
        {
            var k = cam.Keys.Count > 0 ? KeyAtOrBefore(cam) : KeyHere(cam);
            k.Lens = (float)v;
            Changed?.Invoke();
        }, v => $"{v:F0} mm", "Focal length: 14 mm wide … 200 mm tele");
        var aim = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Keep the camera turned to an actor" };
        aim.AddItem("Where it was framed");
        foreach (var lane in p.Lanes) aim.AddItem($"Look at {lane.Label}");
        aim.Selected = Math.Clamp((lensKey?.LookAt ?? -1) + 1, 0, aim.ItemCount - 1);
        aim.ItemSelected += i => { (cam.Keys.Count > 0 ? KeyAtOrBefore(cam) : KeyHere(cam)).LookAt = (int)i - 1; Changed?.Invoke(); };
        Row("Aim", aim);

        var actions = UiKit.HBox(4);
        var frame = SmallButton("Frame from view", "Fly the view where the camera should be, then this keeps it (G)" + (cam.Relative ? ", in the actor's frame" : ""));
        frame.Pressed += () => FrameFromView?.Invoke();
        actions.AddChild(frame);
        var move = SmallButton("Add move", "A key at the playhead from the view: the shot moves between its keys (I)");
        move.Pressed += () => AddMove?.Invoke();
        actions.AddChild(move);
        _box.AddChild(actions);
        _box.AddChild(UiKit.Text(cam.Keys.Count switch
        {
            0 => cam.Relative ? "No key yet: the rig's default place." : "No key yet: frame it from the view.",
            1 => "A placement: one key.",
            _ => $"A move: {cam.Keys.Count} keys.",
        }, UiTheme.FontTiny, UiTheme.TextFaint));

        // shake
        Heading("Shake");
        var styles = UiKit.HBox(3);
        var styleGroup = new ButtonGroup();
        for (int s = 0; s < Styles.Length; s++)
        {
            int style = s;
            var b = SmallButton(Styles[s], $"{Styles[s]} shake");
            b.ToggleMode = true;
            b.ButtonGroup = styleGroup;
            b.ButtonPressed = (int)cam.Shake.Style == s;
            b.Pressed += () => { cam.Shake.Style = (ShakeStyle)style; Changed?.Invoke(); };
            styles.AddChild(b);
        }
        _box.AddChild(styles);
        Slider("Strength", 0, 1, 0.01, cam.Shake.Strength, v => { cam.Shake.Strength = (float)v; Changed?.Invoke(); }, v => $"{v:F2}", "How hard it shakes");
        Slider("Speed", 0, 25, 0.1, cam.Shake.Speed, v => { cam.Shake.Speed = (float)v; Changed?.Invoke(); }, v => v < 0.05 ? "preset" : $"{v:F1} Hz", "0 = the style's own");
        Slider("Turn", 0, 1, 0.01, cam.Shake.Turn, v => { cam.Shake.Turn = (float)v; Changed?.Invoke(); }, v => $"{v * 100:F0} %", "Turning versus moving");
        Slider("Roll", 0, 1, 0.01, cam.Shake.Roll, v => { cam.Shake.Roll = (float)v; Changed?.Invoke(); }, v => $"{v * 100:F0} %", "How much of the turning tilts the horizon");

        // beat links
        var linksHead = UiKit.HBox(4);
        var title = UiKit.Text("Beat links", UiTheme.FontSmall, UiTheme.Amber, bold: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        linksHead.AddChild(title);
        var add = SmallButton("Link", "A camera setting that follows the beat of the sequence's music (or the markers)");
        add.Pressed += () =>
        {
            cam.Links.Add(new BeatLink { Param = BeatParam.Lens, Shape = BeatShape.Pulse, Amount = 10, Every = 1 });
            Changed?.Invoke();
            Callable.From(() => Show(_index)).CallDeferred();
        };
        add.Icon = Icons.Plus;
        linksHead.AddChild(add);
        _box.AddChild(linksHead);
        if (cam.Links.Count == 0)
            _box.AddChild(UiKit.Text("None: the shot does not move with the music.", UiTheme.FontTiny, UiTheme.TextFaint));
        for (int l = 0; l < cam.Links.Count; l++) LinkRow(cam, cam.Links[l]);
    }

    private void LinkRow(CameraTrack cam, BeatLink link)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.5f, 6, 6));
        var box = UiKit.VBox(3);
        card.AddChild(box);
        var top = UiKit.HBox(3);
        top.AddChild(Options(Params, (int)link.Param, i => { link.Param = (BeatParam)i; Changed?.Invoke(); }, "What follows the beat"));
        top.AddChild(Options(Shapes, (int)link.Shape, i => { link.Shape = (BeatShape)i; Changed?.Invoke(); Callable.From(() => Show(_index)).CallDeferred(); }, "How it moves"));
        var remove = SmallButton("Remove", "Remove this beat link");
        remove.Icon = Icons.Trash;
        remove.Pressed += () => { cam.Links.Remove(link); Changed?.Invoke(); Callable.From(() => Show(_index)).CallDeferred(); };
        top.AddChild(remove);
        box.AddChild(top);
        var bottom = UiKit.HBox(3);
        bottom.AddChild(Options(Everies, Math.Max(0, Array.IndexOf(EveryValues, link.Every)), i => { link.Every = EveryValues[i]; Changed?.Invoke(); }, "On which beats"));
        if (link.Shape == BeatShape.Step)
        {
            // the values a step walks through, in invariant numbers whatever the locale
            var steps = new LineEdit
            {
                Text = string.Join(" ", link.Steps.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))),
                PlaceholderText = "0 26   (one value per beat)", SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = "Values one per beat, then round again: for the lens, 0 26 goes 24 mm, 50 mm, 24 mm…",
            };
            steps.TextChanged += s =>
            {
                link.Steps = s.Split(new[] { ' ', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => float.TryParse(x.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : float.NaN)
                    .Where(float.IsFinite).ToArray();
                Changed?.Invoke();
            };
            bottom.AddChild(steps);
        }
        else
        {
            var amount = new SpinBox { MinValue = -200, MaxValue = 200, Step = 0.5, Value = link.Amount, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "How far: mm, metres, degrees, or 0..1 for shake and fade" };
            amount.ValueChanged += v => { link.Amount = (float)v; Changed?.Invoke(); };
            bottom.AddChild(amount);
        }
        box.AddChild(bottom);
        _box.AddChild(card);
    }

    /// <summary>A new rig: a following one starts at its default place on the first actor if it had none.</summary>
    private static void SetRig(MovieProject p, CameraTrack cam, ShotRig rig)
    {
        bool wasRelative = cam.Relative;
        cam.Rig = rig;
        if (rig != ShotRig.World && cam.Target < 0 && p.Lanes.Count > 0) cam.Target = 0;
        if (cam.Relative != wasRelative) cam.Keys.Clear();   // world keys and frame keys mean different things
    }

    private static string RigTip(ShotRig rig) => rig switch
    {
        ShotRig.World => "Fixed in the world: a tripod, or a keyed dolly or crane move",
        ShotRig.Lead => "A camera car in front of the actor, moving with it",
        ShotRig.Chase => "Behind or beside the actor, following with some smoothing",
        _ => "Rigid on the actor or its vehicle: bumper, wing, cockpit, helmet",
    };

    /// <summary>The key at the playhead, or the last before it, for edits that change how the shot looks now.</summary>
    private CameraKey KeyAtOrBefore(CameraTrack cam)
    {
        double t = _time();
        CameraKey best = cam.Keys[0];
        foreach (var k in cam.Keys)
            if (k.T <= t + 1e-6) best = k;
        return best;
    }

    /// <summary>The key a placement edit changes: the one before the playhead, or a first one from the rig's default.</summary>
    private CameraKey KeyHere(CameraTrack cam)
    {
        if (cam.Keys.Count > 0) return KeyAtOrBefore(cam);
        var d = _project().DefaultPose(Index, _time());
        var key = new CameraKey { T = 0, E = d.E, N = d.N, Alt = d.Alt, Qx = d.Qx, Qy = d.Qy, Qz = d.Qz, Qw = d.Qw, Lens = d.Lens };
        if (!cam.Relative) return key;   // a world shot is framed from the view, not from sliders
        cam.Keys.Add(key);
        return key;
    }

    // ---- small builders --------------------------------------------------------------------------

    private void Heading(string text)
    {
        _box.AddChild(UiKit.Spacer(4));
        _box.AddChild(UiKit.Text(text, UiTheme.FontSmall, UiTheme.Amber, bold: true));
    }

    private void Row(string label, Control control)
    {
        var row = UiKit.HBox(6);
        var l = UiKit.Text(label, UiTheme.FontSmall, UiTheme.TextDim);
        l.CustomMinimumSize = new Vector2(72, 0);
        row.AddChild(l);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(control);
        _box.AddChild(row);
    }

    private void Slider(string label, double min, double max, double step, double value, Action<double> set, Func<double, string> show, string tip)
    {
        var row = UiKit.HBox(6);
        var l = UiKit.Text(label, UiTheme.FontSmall, UiTheme.TextDim);
        l.CustomMinimumSize = new Vector2(72, 0);
        l.TooltipText = tip;
        l.MouseFilter = MouseFilterEnum.Pass;
        row.AddChild(l);
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = tip, Scrollable = false };
        var shown = UiKit.Text(show(value), UiTheme.FontSmall, UiTheme.Text);
        shown.CustomMinimumSize = new Vector2(64, 0);
        shown.HorizontalAlignment = HorizontalAlignment.Right;
        slider.ValueChanged += v => { shown.Text = show(v); set(v); };
        row.AddChild(slider);
        row.AddChild(shown);
        _box.AddChild(row);
    }

    private static OptionButton Options(string[] items, int selected, Action<int> set, string tip)
    {
        var o = new OptionButton { TooltipText = tip, SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true };
        foreach (var item in items) o.AddItem(item);
        o.Selected = Math.Clamp(selected, 0, items.Length - 1);
        o.ItemSelected += i => set((int)i);
        o.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        return o;
    }

    private static Button SmallButton(string text, string tip)
    {
        var b = UiKit.Button(text);
        b.TooltipText = tip;
        b.CustomMinimumSize = new Vector2(0, 26);
        b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
        return b;
    }
}
