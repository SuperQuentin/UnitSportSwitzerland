using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--moodshots [dir]</c> (#434, windowed, offline, on real terrain: <c>--chunks</c>): plans the
/// houses round the spawn, picks one of each kind worth looking at and photographs a room of it
/// from a corner, into <c>test_output/moodshots/</c> (or <c>dir</c>), prefixed by <c>--style</c>:
/// a fancy house's double-height living room (else a fancy living room), a messy and an
/// abandoned house, a home cinema from behind its sofa, a music room, a bathroom with the tap
/// running. Prints what it found and <c>[moodshots] RESULT: ok</c> once every shot it could take is saved.
/// </summary>
public partial class MoodShots : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--moodshots") >= 0;

    private readonly Func<FootPlayer?> _local;

    public MoodShots(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "MoodShots";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[moodshots] {what}");

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (done()) return true;
            await Wait(0.1);
        }
        return done();
    }

    private sealed record Shot(string Name, InteriorLayout Layout, int Floor, RoomPlan Room, Vector3 Eye, Vector3 Look, int Tap = -1);

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null || InteriorManager.Instance is not { } interiors) { Finish("no local player"); return; }
        string dir = CmdArgs.Value("--moodshots", notFlag: true) ?? ProjectSettings.GlobalizePath("res://test_output/moodshots");
        System.IO.Directory.CreateDirectory(dir);
        string style = CmdArgs.Value("--style") ?? "ps1";
        await Until(() => DoorIndex.All().Any(), 60);
        await Wait(3);

        var shots = new Dictionary<string, Shot>();
        var here = me.GlobalPosition;
        int looked = 0;
        foreach (var d in DoorIndex.All().Where(d => d.Kind is BuildingKind.House or BuildingKind.Apartment)
                     .OrderBy(d => new Vector2(d.World.X - here.X, d.World.Z - here.Z).Length()).Take(160))
        {
            InteriorLayout? l = null;
            try { l = await interiors.GetOrCreate(d.Key.ToString()); } catch { }
            if (l == null) continue;
            looked++;
            Pick(shots, l);
            if (shots.Count >= 7) break;
        }
        Log($"looked at {looked} houses: {string.Join(", ", shots.Values.Select(s => $"{s.Name} {s.Layout.Key} ({s.Layout.Mood}, {s.Room.Type})"))}");

        int saved = 0;
        foreach (var s in shots.Values)
        {
            var place = InteriorManager.PlacementFor(s.Layout, interiors.Origin!);
            var eye = place * s.Eye;
            var to = place.Basis * (s.Look - s.Eye);
            me.EnterInterior(s.Layout.Key, eye - Vector3.Up * 1.6f, Mathf.Atan2(-to.X, -to.Z));
            me.Velocity = Vector3.Zero;
            if (!await Until(() => interiors.CurrentNode?.Layout.Key == s.Layout.Key, 20)) { Log($"{s.Name}: never got in"); continue; }
            if (s.Tap >= 0 && HouseProps.Instance is { } props && !props.Running(s.Layout.Key, s.Tap)) props.ToggleTap(s.Layout.Key, s.Tap);
            for (int k = 0; k < 25; k++)
            {
                // held where the shot is taken, whatever the body would rather do
                me.GlobalPosition = place * s.Eye - Vector3.Up * 1.6f;
                me.Velocity = Vector3.Zero;
                me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
                me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
                await Wait(0.1);
            }
            string path = System.IO.Path.Combine(dir, $"{style}_{s.Name}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
            Log($"shot {path}");
            saved++;
        }
        Finish(saved > 0 ? null : "no shot taken");
    }

    /// <summary>Keeps the first house of each kind worth a picture.</summary>
    private static void Pick(Dictionary<string, Shot> shots, InteriorLayout l)
    {
        for (int f = 0; f < l.Floors.Count; f++)
            foreach (var r in l.Floors[f].Rooms)
            {
                float y0 = l.FloorY(f);
                if (r.Span > 1 && !shots.ContainsKey("fancy_tall")) shots["fancy_tall"] = Corner("fancy_tall", l, f, r, y0, 2.4f);
                if (l.Mood == InteriorMood.Fancy && r.Type == RoomType.Living && !shots.ContainsKey("fancy")) shots["fancy"] = Corner("fancy", l, f, r, y0, 1.1f);
                if (l.Mood == InteriorMood.Messy && r.Type is RoomType.Bedroom or RoomType.Living && r.Area > 10 && !shots.ContainsKey("messy"))
                    shots["messy"] = Corner("messy", l, f, r, y0, 0.6f);
                if (l.Mood == InteriorMood.Abandoned && r.Type is RoomType.Living or RoomType.Bedroom or RoomType.Kitchen && r.Area > 10
                    && !shots.ContainsKey("abandoned"))
                    shots["abandoned"] = Corner("abandoned", l, f, r, y0, 1.2f);
                if (l.Mood == InteriorMood.Lived && r.Type == RoomType.Living && !shots.ContainsKey("lived")) shots["lived"] = Corner("lived", l, f, r, y0, 1.0f);
                if (r.Type == RoomType.HomeCinema && !shots.ContainsKey("cinema") && Cinema(l, f, r, y0) is { } cinema) shots["cinema"] = cinema;
                if (r.Type == RoomType.MusicRoom && !shots.ContainsKey("music")) shots["music"] = Corner("music", l, f, r, y0, 0.8f);
                if (r.Type == RoomType.Bathroom && !shots.ContainsKey("tap") && Tap(l, f, r, y0) is { } tap) shots["tap"] = tap;
            }
    }

    /// <summary>From 0.45 m out of the corner nearest the room's door side, looking at its middle at <paramref name="lookY"/>.</summary>
    private static Shot Corner(string name, InteriorLayout l, int f, RoomPlan r, float y0, float lookY)
    {
        var eye = new Vector3(r.X0 + 0.45f, y0 + 1.65f, r.Z0 + 0.45f);
        var look = new Vector3((r.X0 + r.X1) / 2, y0 + lookY, (r.Z0 + r.Z1) / 2);
        return new Shot(name, l, f, r, eye, look);
    }

    /// <summary>Behind the cinema's sofa (or as far back as the room goes), looking at the screen.</summary>
    private static Shot? Cinema(InteriorLayout l, int f, RoomPlan r, float y0)
    {
        bool In(FurniturePlan p) => p.Floor == f && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1;
        var screen = l.Furniture.FirstOrDefault(p => p.Type == FurnitureType.CinemaScreen && In(p));
        if (screen == null) return null;
        var front = new Basis(Vector3.Up, screen.Turns * Mathf.Pi / 2) * Vector3.Back;
        var look = new Vector3(screen.X, y0 + 1.4f, screen.Z);
        var eye = look + front * 10f;
        eye.X = Mathf.Clamp(eye.X, r.X0 + 0.35f, r.X1 - 0.35f);
        eye.Z = Mathf.Clamp(eye.Z, r.Z0 + 0.35f, r.Z1 - 0.35f);
        eye.Y = y0 + 1.75f;
        return new Shot("cinema", l, f, r, eye, look);
    }

    /// <summary>In front of the bathroom's sink, its tap turned on.</summary>
    private static Shot? Tap(InteriorLayout l, int f, RoomPlan r, float y0)
    {
        int i = l.Furniture.FindIndex(p => p.Type == FurnitureType.Sink && p.Floor == f && p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1);
        if (i < 0) return null;
        var p = l.Furniture[i];
        var frame = HouseProps.FrameOf(l, p);
        var look = frame * new Vector3(0, p.H, 0);
        var eye = frame * new Vector3(0.35f, 1.6f, p.D / 2 + 0.9f);
        eye.X = Mathf.Clamp(eye.X, r.X0 + 0.3f, r.X1 - 0.3f);
        eye.Z = Mathf.Clamp(eye.Z, r.Z0 + 0.3f, r.Z1 - 0.3f);
        return new Shot("tap", l, f, r, eye, look, i);
    }

    private void Finish(string? why)
    {
        Log(why == null ? "RESULT: ok" : $"RESULT: FAILED {why}");
        GetTree().Quit(why == null ? 0 : 1);
    }
}
