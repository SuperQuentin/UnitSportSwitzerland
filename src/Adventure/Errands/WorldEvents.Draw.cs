using Godot;
using UnitSport.Adventure.Navigation;
using UnitSport.Adventure.Places;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Farming;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Adventure.Errands;

// The client's half of the world events (#1339): the cow, the hiker, the debris cone, the flames, the prompts, the sounds and the
// smoke over the view. Every transform is set when something moves or changes; no mesh is built after the first.
public partial class WorldEvents
{
    public const float TalkM = 3, HelpM = 3, SmokeM = 60, HearM = 80;
    private const double GroundEveryS = 0.15, EaseRate = 9;

    /// <summary>What this peer knows of one event, and its nodes.</summary>
    internal sealed class View
    {
        public required int Id;
        public required WorldEventKind Kind;
        public double E, N;
        public float Ground;
        public string Canton = "", Near = "", Card = "";
        public uint Seed;
        public double CurE, CurN, TgtE, TgtN;
        public float Yaw, TgtYaw, StridePhase;
        public bool Walking, WasWalking, Found, Moved;
        public int[] Data = Array.Empty<int>();
        public Node3D Root = null!;
        public Node3D? Cow;
        public SkinnedFigure? Figure;
        public AvalancheSim? Avalanche;
        public MeshInstance3D[] Poles = Array.Empty<MeshInstance3D>();
        public MultiMeshInstance3D? Flames, Smoke;
        public float? StandY;
        public double NextGround, NextSound;
    }

    private readonly Dictionary<int, View> _views = new();
    private CanvasLayer? _ui;
    private Label _prompt = null!;
    private ProgressBar _bar = null!;
    private ColorRect _veil = null!;
    private float _veilAlpha;
    private (int Id, EventAct Act, int Arg)? _holding;
    private double _heldFor;
    private string? _promptFor;

    /// <summary>A help prompt is up this frame: the gathering hold leaves G / X to it.</summary>
    public static bool OwnsGather { get; private set; }

    /// <summary>The events drawn here (checks).</summary>
    public int ViewCount => _views.Count;

    internal IEnumerable<View> Views => _views.Values;

    public bool IsFound(int id) => _views.TryGetValue(id, out var v) && v.Found;

    private void ReadyDraw()
    {
        _ui = new CanvasLayer { Layer = 9, Name = "EventUi" };
        _veil = new ColorRect { Color = new Color(0.2f, 0.19f, 0.18f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _veil.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_veil);
        _prompt = UiTheme.Prompt(-210);
        _ui.AddChild(_prompt);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.9f, 0.45f, 0.2f) });
        _bar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _bar.Position = new Vector2(-90, -176);
        _bar.Size = new Vector2(180, 8);
        _ui.AddChild(_bar);
        AddChild(_ui);
        AdventureMap.AddOverlay(EventMapOverlay.Instance);
    }

    private static void ExitDraw()
    {
        OwnsGather = false;
        AdventureMap.RemoveOverlay(EventMapOverlay.Instance);
    }

    // ---- what the server says ----------------------------------------------------------------------------------

    private void OnPlan(int id, int kind, double e, double n, float ground, string canton, uint seed, string near, string card)
    {
        if (!Visual || _views.ContainsKey(id) || !Enum.IsDefined((WorldEventKind)kind)) return;
        var v = new View { Id = id, Kind = (WorldEventKind)kind, E = e, N = n, Ground = ground, Canton = canton, Near = near, Card = card, Seed = seed, CurE = e, CurN = n, TgtE = e, TgtN = n };
        v.Root = new Node3D { Name = "Event" + id };
        switch (v.Kind)
        {
            case WorldEventKind.RunawayCow:
                v.Cow = CowMeshBuilder.Cow(CowBreed.Simmental);
                v.Root.AddChild(v.Cow);
                break;
            case WorldEventKind.LostHiker:
                v.Figure = new SkinnedFigure { Name = "Hiker", MaterialOverride = HumanMeshBuilder.FigureMaterial() };
                v.Figure.Show(HikerLook(id));
                HumanMeshBuilder.PosePosed(v.Figure, HumanPose.Standing);
                v.Root.AddChild(v.Figure);
                break;
            case WorldEventKind.Avalanche:
                v.Avalanche = new AvalancheSim(e, n, seed);
                v.Root.AddChild(new MeshInstance3D
                {
                    Name = "Debris", Mesh = WorldEventMeshes.Debris(), MaterialOverride = Rewards.RewardMeshes.Material(),
                    Rotation = new Vector3(0, (float)(ErrandHash.Unit(ErrandHash.Of(seed, 5)) * Mathf.Tau), 0),
                });
                v.Poles = new MeshInstance3D[v.Avalanche.Buried];
                for (int i = 0; i < v.Poles.Length; i++)
                {
                    var spot = v.Avalanche.Spots[i];
                    v.Poles[i] = new MeshInstance3D
                    {
                        Name = "Probe" + i, Mesh = WorldEventMeshes.Pole(), MaterialOverride = Rewards.RewardMeshes.Material(), Visible = false,
                        Position = new Vector3((float)(spot.E - e), 1.6f, -(float)(spot.N - n)),
                    };
                    v.Root.AddChild(v.Poles[i]);
                }
                break;
            default:
                v.Flames = MakeInstances(WorldEventMeshes.Flame(), WorldEventMeshes.FlameMaterial(), "Flames");
                v.Smoke = MakeInstances(WorldEventMeshes.Smoke(), WorldEventMeshes.SmokeMaterial(), "Smoke");
                v.Root.AddChild(v.Flames);
                v.Root.AddChild(v.Smoke);
                break;
        }
        AddChild(v.Root);
        _views[id] = v;
        Place(v);
        if (Me?.Invoke() is { } me && Dist2(me.Global.E, me.Global.N, e, n) < 1500.0 * 1500) Toast?.Invoke($"{WorldEventRules.Name(v.Kind)} nearby.");
        GD.Print($"[worldevents] drawing {v.Kind} {id} at LV95 {e:F0}/{n:F0}");
    }

    private static double Dist2(double e, double n, double e2, double n2) => (e - e2) * (e - e2) + (n - n2) * (n - n2);

    private static FigureLook HikerLook(int id)
    {
        var palette = HumanPalette.ForRider(id & 0x7FFFFF);
        if (Garments.Get(ItemId.GuideHat) is { } hat) palette = palette with { Outfit = palette.Outfit.With(hat.Slot, hat.Code) };
        return new FigureLook(palette);
    }

    private static MultiMeshInstance3D MakeInstances(Mesh mesh, Material material, string name)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = FireSim.Max, VisibleInstanceCount = 0 };
        return new MultiMeshInstance3D { Name = name, Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    private void OnMoved(int id, double e, double n, float yaw, bool walking)
    {
        if (!_views.TryGetValue(id, out var v)) return;
        v.TgtE = e; v.TgtN = n; v.TgtYaw = yaw; v.Walking = walking && (v.Kind == WorldEventKind.LostHiker && v.Found || v.Kind == WorldEventKind.RunawayCow);
        if (!v.Moved) { v.CurE = e; v.CurN = n; v.Yaw = yaw; v.Moved = true; }
    }

    private void OnState(int id, int[] data)
    {
        if (!_views.TryGetValue(id, out var v)) return;
        v.Data = data;
        switch (v.Kind)
        {
            case WorldEventKind.LostHiker:
                v.Found = data.Length > 0 && data[0] == 1;
                if (!v.Found) v.Walking = false;
                break;
            case WorldEventKind.Avalanche:
                for (int i = 0; i < v.Poles.Length; i++) v.Poles[i].Visible = data.Length > 0 && (data[0] & (1 << i)) != 0;
                break;
            case WorldEventKind.ForestFire:
                SetFire(v, data);
                break;
        }
    }

    private void SetFire(View v, int[] cells)
    {
        int n = Math.Min(cells.Length, FireSim.Max);
        var flames = v.Flames!.Multimesh;
        var smoke = v.Smoke!.Multimesh;
        for (int i = 0; i < n; i++)
        {
            var (a, b) = FireSim.Unpack(cells[i]);
            double e = v.E + a * FireSim.CellM, nn = v.N + b * FireSim.CellM;
            float y = GroundAt?.Invoke(e, nn) is { } g ? g - v.Ground : 0f;
            var at = new Vector3(a * FireSim.CellM, y, -b * FireSim.CellM);
            flames.SetInstanceTransform(i, new Transform3D(Basis.Identity, at));
            smoke.SetInstanceTransform(i, new Transform3D(Basis.Identity, at));
        }
        flames.VisibleInstanceCount = n;
        smoke.VisibleInstanceCount = n;
    }

    private void OnEnded(int id, bool success, string line)
    {
        if (!_views.Remove(id, out var v)) return;
        if (_holding is { } h && h.Id == id) { _holding = null; _bar.Visible = false; }
        v.Root.QueueFree();
        if (Me?.Invoke() is { } me && Dist2(me.Global.E, me.Global.N, v.CurE, v.CurN) < WorldEventRules.InformM * WorldEventRules.InformM) Toast?.Invoke(line);
        GD.Print($"[worldevents] {v.Kind} {id} over: {(success ? "helped" : "timed out")}");
    }

    // ---- the frame ------------------------------------------------------------------------------------------------------

    private void Place(View v)
    {
        if (Origin?.Invoke() is not { } origin) return;
        float y = v.StandY ?? v.Ground;
        v.Root.Position = origin.ToWorld(v.CurE, v.CurN, y);
        if (v.Cow != null) v.Cow.Rotation = new Vector3(0, v.Yaw, 0);
        if (v.Figure != null) v.Figure.Rotation = new Vector3(0, v.Yaw, 0);
    }

    public override void _Process(double delta)
    {
        if (!Visual || _ui == null) return;
        var me = Me?.Invoke();
        if (_views.Count == 0 || me == null || !IsInstanceValid(me))
        {
            Stand();
            return;
        }
        double now = GameClock.Now;
        var at = me.Global;
        float ease = (float)Math.Min(1, EaseRate * delta);
        float veil = 0;
        foreach (var v in _views.Values)
        {
            bool moving = v.Cow != null || v.Figure != null;
            if (moving)
            {
                v.CurE += (v.TgtE - v.CurE) * ease;
                v.CurN += (v.TgtN - v.CurN) * ease;
                v.Yaw = Mathf.LerpAngle(v.Yaw, v.TgtYaw, ease);
                if (now >= v.NextGround)
                {
                    v.NextGround = now + GroundEveryS;
                    if (GroundAt?.Invoke(v.CurE, v.CurN) is { } g) v.StandY = g;
                }
                Place(v);
                if (v.Figure != null) PoseHiker(v, (float)delta);
            }
            else if (v.Root.Position == Vector3.Zero) Place(v);
            double d2 = Dist2(at.E, at.N, v.CurE, v.CurN);
            veil = Math.Max(veil, v.Kind == WorldEventKind.ForestFire ? SmokeAt(v, at) : 0f);
            if (now >= v.NextSound && d2 < HearM * HearM) Sound(v, at, d2, now);
        }
        SetVeil(veil);
        Help(me, at);
    }

    private void Stand()
    {
        if (_holding is { } h) { AskHelp(h.Id, h.Act == EventAct.DigStart ? EventAct.DigStop : EventAct.BeatStop, h.Arg); _holding = null; }
        OwnsGather = false;
        _prompt.Visible = false;
        _bar.Visible = false;
        SetVeil(0);
    }

    private void PoseHiker(View v, float delta)
    {
        if (v.Walking)
        {
            v.StridePhase = HumanMeshBuilder.AdvancePhase(v.StridePhase, HikerSim.WalkMps, delta);
            HumanMeshBuilder.PoseStride(v.Figure!, HikerSim.WalkMps, v.StridePhase);
        }
        else if (v.WasWalking) HumanMeshBuilder.PosePosed(v.Figure!, HumanPose.Standing);
        v.WasWalking = v.Walking;
    }

    /// <summary>How much smoke dims the view from here: 0 beyond 60 m of a burning cell, up to 0.55 in the fire.</summary>
    private static float SmokeAt(View v, GlobalPos at)
    {
        float best = SmokeM;
        foreach (int key in v.Data)
        {
            var (a, b) = FireSim.Unpack(key);
            float d = (float)Math.Sqrt(Dist2(at.E, at.N, v.E + a * FireSim.CellM, v.N + b * FireSim.CellM));
            if (d < best) best = d;
        }
        return (SmokeM - best) / SmokeM * 0.55f;
    }

    private void SetVeil(float alpha)
    {
        if (Math.Abs(alpha - _veilAlpha) < 0.02f && (alpha > 0) == _veil.Visible) return;
        _veilAlpha = alpha;
        _veil.Visible = alpha > 0.02f;
        _veil.Color = new Color(0.2f, 0.19f, 0.18f, alpha);
    }

    private void Sound(View v, GlobalPos at, double d2, double now)
    {
        float near = (float)Math.Sqrt(d2);
        switch (v.Kind)
        {
            case WorldEventKind.RunawayCow when near < 40:
                v.NextSound = now + 2.5;
                WorldEventSounds.Play(this, WorldEventSounds.Bell, -14f + (40 - near) * 0.15f - 6);
                break;
            case WorldEventKind.LostHiker when !v.Found:
                v.NextSound = now + 8;
                WorldEventSounds.Play(this, WorldEventSounds.Call, -10f);
                break;
            case WorldEventKind.ForestFire when near < 60:
                v.NextSound = now + 1.6;
                WorldEventSounds.Play(this, WorldEventSounds.Crackle, -12f);
                break;
            case WorldEventKind.Avalanche when v.Avalanche is { } av:
                int victim = av.Nearest(at.E, at.N, AvalancheSim.DetectM, out double dist);
                var dug = v.Data.Length > 0 ? v.Data[0] : 0;
                if (victim < 0 || (dug & (1 << victim)) != 0) { v.NextSound = now + 0.5; break; }
                v.NextSound = now + 1.0 / Math.Max(1f, AvalancheSim.BeepHz(dist));
                WorldEventSounds.Play(this, WorldEventSounds.Beep, -8f);
                break;
        }
    }

    // ---- helping, the keys -----------------------------------------------------------------------------------------------

    /// <summary>The dig or the beating in reach of a body (the hold on G / X / a VR grip-and-pull), the prompt, the bar.</summary>
    private void Help(FootPlayer me, GlobalPos at)
    {
        (int Id, EventAct Act, int Arg, string Text)? target = null;
        double best = HelpM * HelpM;
        foreach (var v in _views.Values)
        {
            if (v.Avalanche is { } av)
            {
                int dug = v.Data.Length > 0 ? v.Data[0] : 0;
                for (int i = 0; i < av.Buried; i++)
                {
                    if ((dug & (1 << i)) != 0) continue;
                    double d = Dist2(at.E, at.N, av.Spots[i].E, av.Spots[i].N);
                    if (d <= best) { best = d; target = (v.Id, EventAct.DigStart, i, "dig out the person"); }
                }
            }
            else if (v.Kind == WorldEventKind.ForestFire)
            {
                foreach (int key in v.Data)
                {
                    var (a, b) = FireSim.Unpack(key);
                    double d = Dist2(at.E, at.N, v.E + a * FireSim.CellM, v.N + b * FireSim.CellM);
                    if (d <= best) { best = d; target = (v.Id, EventAct.BeatStart, key, "beat out the flames"); }
                }
            }
        }
        if (me.Indoors || target == null)
        {
            OwnsGather = false;
            if (_holding is { } h) { AskHelp(h.Id, h.Act == EventAct.DigStart ? EventAct.DigStop : EventAct.BeatStop, h.Arg); _holding = null; }
            _prompt.Visible = SignalLine(at);
            _bar.Visible = false;
            return;
        }
        var t = target.Value;
        OwnsGather = true;
        bool held = PlayerInput.Held(PlayerInput.Gather);
        if (held)
        {
            if (_holding is { } cur && (cur.Id != t.Id || cur.Arg != t.Arg))
            {
                AskHelp(cur.Id, cur.Act == EventAct.DigStart ? EventAct.DigStop : EventAct.BeatStop, cur.Arg);
                _holding = null;
            }
            if (_holding == null)
            {
                AskHelp(t.Id, t.Act, t.Arg);
                _holding = (t.Id, t.Act, t.Arg);
                _heldFor = 0;
            }
            _heldFor += GetProcessDeltaTime();
        }
        else if (_holding is { } was)
        {
            AskHelp(was.Id, was.Act == EventAct.DigStart ? EventAct.DigStop : EventAct.BeatStop, was.Arg);
            _holding = null;
            _heldFor = 0;
        }
        string key2 = InputHints.Label(PlayerInput.Gather);
        string want = $"[{key2}] Hold to {t.Text}";
        if (_promptFor != want) { _promptFor = want; _prompt.Text = want; }
        _prompt.Visible = _holding == null;
        _bar.Visible = _holding != null;
        if (_holding != null) _bar.Value = t.Act == EventAct.DigStart ? DigShare(t.Id, t.Arg) : Math.Min(1.0, _heldFor / FireSim.BeatS);
    }

    private float DigShare(int id, int victim) =>
        _views.TryGetValue(id, out var v) && v.Data.Length > 1 + victim ? v.Data[1 + victim] / 100f : 0f;

    /// <summary>The transceiver's reading within 60 m of somebody buried: "Signal 23 m". True when a line is up.</summary>
    private bool SignalLine(GlobalPos at)
    {
        foreach (var v in _views.Values)
        {
            if (v.Avalanche is not { } av) continue;
            int dug = v.Data.Length > 0 ? v.Data[0] : 0;
            int i = av.Nearest(at.E, at.N, AvalancheSim.DetectM, out double d);
            if (i < 0 || (dug & (1 << i)) != 0) continue;
            string line = $"Signal {(int)(d / 5) * 5 + 5} m";
            if (_promptFor != line) { _promptFor = line; _prompt.Text = line; }
            return true;
        }
        _promptFor = null;
        return false;
    }

    // ---- talking to the hiker ---------------------------------------------------------------------------------------------

    /// <summary>The lost hiker within reach of a body who has not been found yet, or null.</summary>
    private View? HikerInReach(FootPlayer p)
    {
        if (p.Indoors) return null;
        var at = p.Global;
        View? best = null;
        double bd = TalkM * TalkM;
        foreach (var v in _views.Values)
        {
            if (v.Kind != WorldEventKind.LostHiker || v.Found) continue;
            double d = Dist2(at.E, at.N, v.CurE, v.CurN);
            if (d <= bd) { bd = d; best = v; }
        }
        return best;
    }

    /// <summary>E / Y / a VR hand at a lost hiker: talks to them, and they follow. False when none is in reach.</summary>
    public static bool TryInteract(FootPlayer p)
    {
        if (Instance is not { } m || m.HikerInReach(p) is not { } v) return false;
        m.AskHelp(v.Id, EventAct.Talk, 0);
        return true;
    }

    private string? _talkPrompt;

    /// <summary>"[E] Talk to the hiker", their figure outlined; null away from one.</summary>
    public static Interaction? InteractionFor(FootPlayer p)
    {
        if (Instance is not { } m || m.HikerInReach(p) is not { Figure: { } figure }) return null;
        m._talkPrompt ??= InputHints.Prompt(PlayerInput.InteractMount, "Talk to the lost hiker");
        return new Interaction(m._talkPrompt, Outline.Of(figure));
    }

    // ---- the compass and the map --------------------------------------------------------------------------------------------

    /// <summary>The events within reach as compass markers (key, LV95 point, label); a lost hiker not found yet shows 120 m off, as "last seen".</summary>
    public IEnumerable<(int Key, double E, double N, WorldEventKind Kind, string Label)> Markers
    {
        get
        {
            foreach (var v in _views.Values)
            {
                double e = v.CurE, n = v.CurN;
                string label = WorldEventRules.Name(v.Kind);
                if (v.Kind == WorldEventKind.LostHiker && !v.Found)
                {
                    (e, n) = WorldEventRules.Offset(e, n, ErrandHash.Unit(ErrandHash.Of(v.Seed, 9)), 120);
                    label = v.Near.Length > 0 ? "Last seen near " + v.Near : label;
                }
                yield return (7000 + v.Id % 900, e, n, v.Kind, label);
            }
        }
    }

    public static Color ColorOf(WorldEventKind kind) => kind switch
    {
        WorldEventKind.RunawayCow => new Color(0.95f, 0.85f, 0.4f),
        WorldEventKind.LostHiker => new Color(0.4f, 0.8f, 1f),
        WorldEventKind.Avalanche => new Color(0.85f, 0.9f, 1f),
        _ => new Color(1f, 0.45f, 0.15f),
    };

    private sealed class EventMapOverlay : IMapOverlay
    {
        public static EventMapOverlay Instance { get; } = new();

        public void Draw(MapView view, CanvasItem on)
        {
            if (WorldEvents.Instance is not { } events) return;
            foreach (var (_, e, n, kind, _) in events.Markers)
            {
                var at = view.ToScreen(e, n);
                if (at.X < -10 || at.Y < -10 || at.X > view.Size.X + 10 || at.Y > view.Size.Y + 10) continue;
                on.DrawCircle(at, 8, new Color(0, 0, 0, 0.6f));
                on.DrawCircle(at, 6, ColorOf(kind));
                on.DrawString(UiTheme.Font, at + new Vector2(-6, 4.5f), kind switch { WorldEventKind.RunawayCow => "C", WorldEventKind.LostHiker => "H", WorldEventKind.Avalanche => "A", _ => "F" },
                    HorizontalAlignment.Center, 12, 11, Colors.Black);
            }
        }

        public bool Click(Vector2 lv95, MouseButton button) => false;
    }
}
