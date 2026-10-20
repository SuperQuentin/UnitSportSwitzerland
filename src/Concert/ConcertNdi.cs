using System.Runtime.InteropServices;
using Godot;
using UnitSport.Core;

namespace UnitSport.Concert;

/// <summary>
/// The program camera out as an NDI source (#885), for OBS, TouchDesigner, vMix, Resolume: the NDI
/// runtime (free, installed with NDI Tools; the game ships no NDI code) is loaded from
/// <c>NDI_RUNTIME_DIR_V6</c> (or V5) when <c>/concert ndi on</c> asks; without it, a line says so and
/// nothing else changes. The program render (1280 x 720) is read back at most 30 times a second and
/// handed to a worker that sends it (RGBA, progressive, a synthesised timecode), so the frame never
/// waits on the network.
/// </summary>
public sealed class ConcertNdi : IDisposable
{
    // ---- the NDI SDK's C API, as its headers declare it -------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SendCreate
    {
        public IntPtr Name;       // const char* p_ndi_name
        public IntPtr Groups;     // const char* p_groups
        [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoFrame
    {
        public int XRes, YRes;
        public uint FourCC;
        public int FrameRateN, FrameRateD;
        public float AspectRatio;
        public int FrameFormat;   // 1 = progressive
        public long Timecode;     // NDIlib_send_timecode_synthesize = INT64_MAX
        public IntPtr Data;
        public int LineStride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    private const uint FourCcRgba = 'R' | 'G' << 8 | 'B' << 16 | (uint)'A' << 24;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create(ref SendCreate create);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr sender);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SendVideo(IntPtr sender, ref VideoFrame frame);

    private static IntPtr _lib;
    public static string? RuntimePath { get; private set; }

    // ---- finding and receiving, for the check: what OBS or TouchDesigner would do -------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FindCreate
    {
        [MarshalAs(UnmanagedType.U1)] public bool ShowLocal;
        public IntPtr Groups, ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Source
    {
        public IntPtr Name, Url;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecvCreate
    {
        public Source SourceToConnect;
        public int ColorFormat;      // 2 = RGBX_RGBA
        public int Bandwidth;        // 100 = highest
        [MarshalAs(UnmanagedType.U1)] public bool AllowFields;
        public IntPtr Name;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindCreateFn(ref FindCreate c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.U1)] private delegate bool FindWait(IntPtr f, uint ms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr FindSources(IntPtr f, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FindDestroy(IntPtr f);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RecvCreateFn(ref RecvCreate c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RecvCapture(IntPtr r, ref VideoFrame v, IntPtr audio, IntPtr meta, uint ms);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvFree(IntPtr r, ref VideoFrame v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RecvDestroy(IntPtr r);

    private static T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));

    /// <summary>
    /// Off the main thread: finds <paramref name="source"/> on the network as any NDI app would, connects
    /// a receiver to it and takes a frame. The answer says what came back.
    /// </summary>
    public static string Check(string source)
    {
        if (Load() is { } why) return why;
        var fc = new FindCreate { ShowLocal = true };
        var finder = Fn<FindCreateFn>("NDIlib_find_create_v2")(ref fc);
        if (finder == IntPtr.Zero) return "NDI check: no finder.";
        try
        {
            Source? found = null;
            var names = new List<string>();
            for (int tries = 0; tries < 10 && found == null; tries++)
            {
                Fn<FindWait>("NDIlib_find_wait_for_sources")(finder, 1000);
                var list = Fn<FindSources>("NDIlib_find_get_current_sources")(finder, out uint count);
                names.Clear();
                for (int i = 0; i < count; i++)
                {
                    var src = Marshal.PtrToStructure<Source>(list + i * Marshal.SizeOf<Source>());
                    string n = Marshal.PtrToStringUTF8(src.Name) ?? "";
                    names.Add(n);
                    if (n.Contains(source, StringComparison.Ordinal)) found = src;
                }
            }
            if (found is not { } s) return $"NDI check: \"{source}\" is not on the network (sources seen: {string.Join(", ", names)}).";
            string full = Marshal.PtrToStringUTF8(s.Name) ?? source;
            var rc = new RecvCreate { SourceToConnect = s, ColorFormat = 2, Bandwidth = 100, AllowFields = false };
            var recv = Fn<RecvCreateFn>("NDIlib_recv_create_v3")(ref rc);
            if (recv == IntPtr.Zero) return $"NDI check: found \"{full}\" but could not connect to it.";
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var v = new VideoFrame();
                    int type = Fn<RecvCapture>("NDIlib_recv_capture_v2")(recv, ref v, IntPtr.Zero, IntPtr.Zero, 200);
                    if (type != 1) continue;   // 1 = a video frame
                    string got = $"NDI check: \"{full}\" found and received: {v.XRes}x{v.YRes} at {v.FrameRateN}/{v.FrameRateD} fps.";
                    Fn<RecvFree>("NDIlib_recv_free_video_v2")(recv, ref v);
                    return got;
                }
                return $"NDI check: \"{full}\" found, but no frame in 10 s.";
            }
            finally { Fn<RecvDestroy>("NDIlib_recv_destroy")(recv); }
        }
        finally { Fn<FindDestroy>("NDIlib_find_destroy")(finder); }
    }
    private static Create? _create;
    private static Destroy? _destroy;
    private static SendVideo? _send;

    /// <summary>Loads the installed NDI runtime once; the reason when there is none.</summary>
    private static string? Load()
    {
        if (_send != null) return null;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return "NDI: not on this platform.";
        string file = OperatingSystem.IsWindows() ? "Processing.NDI.Lib.x64.dll" : OperatingSystem.IsMacOS() ? "libndi.dylib" : "libndi.so.6";
        // the installer sets the variable machine-wide, but a game started from a shell opened before
        // the install does not see it: the installers' own folders too
        foreach (var dir in new[]
        {
            System.Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"), System.Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"),
            OperatingSystem.IsWindows() ? @"C:\Program Files\NDI\NDI 6 Tools\Runtime" : null,
            OperatingSystem.IsWindows() ? @"C:\Program Files\NDI\NDI 6 Runtime\v6" : null, "",
        })
        {
            if (dir == null) continue;
            string path = dir.Length > 0 ? System.IO.Path.Combine(dir, file) : file;
            if (!NativeLibrary.TryLoad(path, out _lib)) continue;
            try
            {
                var init = Marshal.GetDelegateForFunctionPointer<Initialize>(NativeLibrary.GetExport(_lib, "NDIlib_initialize"));
                if (!init()) return "NDI: the runtime would not start on this CPU.";
                _create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(_lib, "NDIlib_send_create"));
                _destroy = Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(_lib, "NDIlib_send_destroy"));
                _send = Marshal.GetDelegateForFunctionPointer<SendVideo>(NativeLibrary.GetExport(_lib, "NDIlib_send_send_video_v2"));
                RuntimePath = path;
                return null;
            }
            catch (EntryPointNotFoundException e) { return $"NDI: the runtime at {path} is not one this game knows ({e.Message})."; }
        }
        return "NDI: no runtime installed. Install NDI Tools (ndi.video) on this machine, restart the game, then '/concert ndi on'.";
    }

    // ---- receiving onto a screen (#975) ---------------------------------------------------------------

    /// <summary>
    /// An NDI source received for an imported screen (#975): a worker finds it by name (as
    /// <see cref="Check"/>, a part of its full "MACHINE (name)" will do), connects at the lowest
    /// bandwidth (NDI's preview stream: plenty for an LED wall of the game's pixels) and keeps the newest
    /// frame; the main thread takes it when it wants one. Two buffers swapped, so nothing is allocated
    /// per frame once the size is known. The runtime keeps the link through a source's restarts.
    /// </summary>
    public sealed class Receiver : IDisposable
    {
        public string Source { get; }
        /// <summary>What the worker is doing, for the log: looking, connected, gone.</summary>
        public volatile string Status = "looking";
        private readonly Thread _worker;
        private volatile bool _stop;
        private byte[]? _back, _ready;
        private int _w, _h;
        private bool _fresh;

        private Receiver(string source)
        {
            Source = source;
            _worker = new Thread(Run) { IsBackground = true, Name = "NDI receive" };
            _worker.Start();
        }

        /// <summary>A receiver looking for <paramref name="source"/>, or null with the reason (no runtime). Main thread.</summary>
        public static Receiver? Start(string source, out string? why)
        {
            why = Load();
            return why == null ? new Receiver(source) : null;
        }

        /// <summary>Main thread: the newest frame (RGBA) in place of <paramref name="buffer"/> when one came since the last take.</summary>
        public bool Take(ref byte[]? buffer, out int w, out int h)
        {
            lock (this)
            {
                w = _w;
                h = _h;
                if (!_fresh) return false;
                (buffer, _ready) = (_ready, buffer);
                _fresh = false;
                return buffer != null;
            }
        }

        private void Run()
        {
            var findCreate = Fn<FindCreateFn>("NDIlib_find_create_v2");
            var findWait = Fn<FindWait>("NDIlib_find_wait_for_sources");
            var findSources = Fn<FindSources>("NDIlib_find_get_current_sources");
            var findDestroy = Fn<FindDestroy>("NDIlib_find_destroy");
            var capture = Fn<RecvCapture>("NDIlib_recv_capture_v2");
            var free = Fn<RecvFree>("NDIlib_recv_free_video_v2");
            var fc = new FindCreate { ShowLocal = true };
            var finder = findCreate(ref fc);
            if (finder == IntPtr.Zero) { Status = "no finder"; return; }
            IntPtr recv = IntPtr.Zero;
            try
            {
                while (!_stop && recv == IntPtr.Zero)
                {
                    findWait(finder, 1000);
                    var list = findSources(finder, out uint count);
                    for (int i = 0; i < count && recv == IntPtr.Zero; i++)
                    {
                        var src = Marshal.PtrToStructure<Source>(list + i * Marshal.SizeOf<Source>());
                        if (!(Marshal.PtrToStringUTF8(src.Name) ?? "").Contains(Source, StringComparison.OrdinalIgnoreCase)) continue;
                        var rc = new RecvCreate { SourceToConnect = src, ColorFormat = 2, Bandwidth = 0, AllowFields = false };
                        recv = Fn<RecvCreateFn>("NDIlib_recv_create_v3")(ref rc);
                        Status = recv != IntPtr.Zero ? $"connected to {Marshal.PtrToStringUTF8(src.Name)}" : "could not connect";
                    }
                }
            }
            finally { findDestroy(finder); }
            try
            {
                while (!_stop && recv != IntPtr.Zero)
                {
                    var v = new VideoFrame();
                    if (capture(recv, ref v, IntPtr.Zero, IntPtr.Zero, 200) != 1) continue;   // 1 = a video frame
                    try
                    {
                        int w = v.XRes, h = v.YRes, row = w * 4;
                        if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || v.Data == IntPtr.Zero || v.LineStride < row) continue;
                        if (_back == null || _back.Length != row * h) _back = new byte[row * h];
                        for (int y = 0; y < h; y++) Marshal.Copy(v.Data + y * v.LineStride, _back, y * row, row);
                        lock (this)
                        {
                            (_back, _ready) = (_ready, _back);
                            _w = w;
                            _h = h;
                            _fresh = true;
                        }
                    }
                    finally { free(recv, ref v); }
                }
            }
            finally { if (recv != IntPtr.Zero) Fn<RecvDestroy>("NDIlib_recv_destroy")(recv); }
        }

        public void Dispose()
        {
            _stop = true;
            _worker.Join(1500);
        }
    }

    // ---- one sender ------------------------------------------------------------------------------------

    public string SourceName { get; }
    private IntPtr _sender;
    private IntPtr _name;
    private readonly Thread _worker;
    private readonly AutoResetEvent _ready = new(false);
    private byte[]? _pending;
    private int _w, _h;
    private volatile bool _stop;
    public long FramesSent;

    private ConcertNdi(string name)
    {
        SourceName = name;
        _name = Marshal.StringToCoTaskMemUTF8(name);
        var create = new SendCreate { Name = _name, Groups = IntPtr.Zero, ClockVideo = false, ClockAudio = false };
        _sender = _create!(ref create);
        _worker = new Thread(Run) { IsBackground = true, Name = "NDI send" };
        _worker.Start();
    }

    /// <summary>A sender named <paramref name="name"/>, or the reason there cannot be one.</summary>
    public static (ConcertNdi? Sender, string Message) Start(string name)
    {
        if (Load() is { } why) return (null, why);
        var s = new ConcertNdi(name);
        return s._sender == IntPtr.Zero ? (null, "NDI: the runtime refused to create a sender.") : (s, $"NDI: sending \"{name}\" (1280x720, up to 30 fps).");
    }

    /// <summary>Main thread: the program's latest picture for the worker (the newest wins; a slow network drops frames, never the game).</summary>
    public void Offer(Image image)
    {
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        var bytes = image.GetData();
        lock (this)
        {
            _pending = bytes;
            _w = image.GetWidth();
            _h = image.GetHeight();
        }
        _ready.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            if (!_ready.WaitOne(500)) continue;
            byte[]? frame;
            int w, h;
            lock (this)
            {
                frame = _pending;
                _pending = null;
                w = _w;
                h = _h;
            }
            if (frame == null || _sender == IntPtr.Zero) continue;
            var pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                var v = new VideoFrame
                {
                    XRes = w, YRes = h, FourCC = FourCcRgba, FrameRateN = 30000, FrameRateD = 1001, AspectRatio = (float)w / h,
                    FrameFormat = 1, Timecode = long.MaxValue, Data = pin.AddrOfPinnedObject(), LineStride = w * 4,
                    Metadata = IntPtr.Zero, Timestamp = 0,
                };
                _send!(_sender, ref v);
                Interlocked.Increment(ref FramesSent);
            }
            finally { pin.Free(); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _ready.Set();
        _worker.Join(1000);
        if (_sender != IntPtr.Zero) _destroy?.Invoke(_sender);
        _sender = IntPtr.Zero;
        if (_name != IntPtr.Zero) Marshal.FreeCoTaskMem(_name);
        _name = IntPtr.Zero;
    }
}

/// <summary>
/// The program on this screen (#885): the on-air camera full screen, for a player watching the show as
/// a broadcast or OBS capturing the window. <c>/concert view</c>, the desk's button; Esc closes.
/// Also feeds <see cref="ConcertNdi"/> when it sends. Client only.
/// </summary>
public partial class ConcertBroadcast : CanvasLayer
{
    public static ConcertBroadcast? Instance { get; private set; }

    private TextureRect _view = null!;
    private Label _caption = null!;
    private ConcertNdi? _ndi;
    private double _sentAt;
    private bool _viewing;

    public bool Viewing => _viewing;

    public ConcertBroadcast()
    {
        Name = "ConcertBroadcast";
        Layer = 11;
        Instance = this;
    }

    public override void _Ready()
    {
        _view = new TextureRect
        {
            Visible = false, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        _caption = new Label { Visible = false, Position = new Vector2(24, 18) };
        _caption.AddThemeFontSizeOverride("font_size", 18);
        _caption.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.85f));
        AddChild(_caption);
    }

    public override void _ExitTree()
    {
        _ndi?.Dispose();
        if (_viewing || _ndi != null) ConcertCameras.ProgramWanted = 0;
        if (Instance == this) Instance = null;
    }

    private static ConcertRig? Rig => ConcertRig.Live.Values.FirstOrDefault(r => IsInstanceValid(r) && r.IsInsideTree());

    /// <summary>Client-local <c>/concert view|ndi|camera</c>: what this screen shows and sends.</summary>
    public string? Command(string[] args)
    {
        switch (args.Length == 0 ? "" : args[0])
        {
            case "view":
                _viewing = !_viewing;
                ConcertCameras.ProgramWanted += _viewing ? 1 : -1;
                return _viewing ? "Program view on: the camera on air full screen (Esc or '/concert view' to leave)." : "Program view off.";
            case "ndi":
                if (args.Length > 1 && args[1] == "check")
                {
                    if (_ndi == null) return "Start NDI first ('/concert ndi on').";
                    string src = _ndi.SourceName;
                    // the finder waits for the network: off the main thread, the answer printed when it comes
                    Task.Run(() => ConcertNdi.Check(src)).ContinueWith(t => Callable.From(() => GD.Print($"[concert] {t.Result}")).CallDeferred());
                    return "NDI check: looking for the source as OBS would...";
                }
                bool on = args.Length < 2 || args[1] != "off";
                if (!on)
                {
                    if (_ndi == null) return "NDI is not sending.";
                    _ndi.Dispose();
                    _ndi = null;
                    ConcertCameras.ProgramWanted--;
                    return "NDI stopped.";
                }
                if (_ndi != null) return $"NDI already sending \"{_ndi.SourceName}\" ({_ndi.FramesSent} frames).";
                string name = $"UnitSport {Rig?.Event.Stadium.Name ?? "concert"} PGM";
                var (sender, message) = ConcertNdi.Start(name);
                GD.Print($"[concert] {message}");
                _ndi = sender;
                if (sender != null) ConcertCameras.ProgramWanted++;
                return message;
            case "camera" when args.Length > 1:
                return CameraCommand(args[1..]);
            case "snap":
                // a still of the program, as NDI and the program view get it
                if (Rig?.Cameras is not { } cams) return "No concert here.";
                if (!_snapping)
                {
                    _snapping = true;
                    ConcertCameras.ProgramWanted++;
                }
                _snapAt = Core.RealClock.Now + 0.3;   // a few frames for the program render to catch up
                return "Snapping the program camera...";
            default:
                return null;
        }
    }

    // ---- operating a camera ----------------------------------------------------------------------------

    private int _stadium = -1, _cam = -1;
    private Vector2 _mouse;
    private float _wheel;
    private double _sentState;

    private static int StadiumIndex(ConcertRig rig) => Array.IndexOf(Terrain.Stadiums.StadiumCatalog.All, rig.Event.Stadium);

    private string CameraCommand(string[] a)
    {
        if (Rig is not { } rig || ConcertService.Instance is not { } svc) return "No concert here.";
        int stadium = StadiumIndex(rig);
        switch (a[0])
        {
            case "take":
                if (a.Length < 2 || !int.TryParse(a[1], out int n) || n < 1 || n > ConcertCameras.Names.Length)
                    return $"/concert camera take <1..{ConcertCameras.Names.Length}>: " + string.Join(", ", ConcertCameras.Names.Select((x, i) => $"{i + 1} {x}"));
                svc.AskCamera(stadium, n - 1);
                return $"Asking for camera {n} ({ConcertCameras.Names[n - 1]})...";
            case "leave":
                svc.AskCamera(stadium, -1);
                return "Leaving the camera.";
            default:
                return string.Join(", ", rig.Cameras.Rigs.Select((r, i) => $"{i + 1} {r.Name}{(r.Operator != 0 ? " (operated)" : "")}"));
        }
    }

    /// <summary>The service says this player took a camera (true), or a camera was left (false).</summary>
    public void Operating(int stadium, int cam, bool? mine)
    {
        if (mine == true)
        {
            _stadium = stadium;
            _cam = cam;
            if (Rig?.Cameras is { } c) c.Operating = cam;
            UiFocus.Set(this, true);
            Input.MouseMode = Input.MouseModeEnum.Captured;
            _view.Texture = Rig?.Cameras.FinderTexture;
        }
        else if (mine == false && cam == _cam && stadium == _stadium)
        {
            if (Rig?.Cameras is { } c) c.Operating = -1;
            _cam = -1;
            UiFocus.Set(this, false);
            if (_viewing && Rig?.Cameras is { } cc) _view.Texture = cc.ProgramTexture;
        }
    }

    private void Operate(ConcertCameras cams, double dt)
    {
        if (_cam < 0 || _cam >= cams.Rigs.Count) return;
        var rig = cams.Rigs[_cam];
        if (cams.Operating != _cam) cams.Operating = _cam;
        var move = Input.GetVector(PlayerInput.MoveLeft, PlayerInput.MoveRight, PlayerInput.MoveBack, PlayerInput.MoveForward);
        float rise = Input.GetActionStrength(PlayerInput.FlyUp) - Input.GetActionStrength(PlayerInput.FlyDown);
        var stick = Input.GetVector(PlayerInput.LookLeft, PlayerInput.LookRight, PlayerInput.LookUp, PlayerInput.LookDown) * 1.6f * (float)dt;
        // a tight lens turns slower, as a real head does
        float slow = Mathf.Clamp(rig.Fov / 45f, 0.15f, 1.2f);
        var look = (_mouse * 0.0025f + stick) * slow;
        _mouse = Vector2.Zero;
        float zoom = Input.GetActionStrength(PlayerInput.CamZoomIn) - Input.GetActionStrength(PlayerInput.CamZoomOut) + _wheel * 8f;
        _wheel = 0;
        rig.Drive(move, rise, look, zoom, (float)dt);
        if (Core.RealClock.Now - _sentState > 0.05)
        {
            _sentState = Core.RealClock.Now;
            ConcertService.Instance?.SendCamera(_stadium, _cam, rig.Pack());
        }
        if (Input.IsActionJustPressed(PlayerInput.CamCut)) ConcertService.Instance?.CutTo(_stadium, _cam);
    }

    public override void _Input(InputEvent ev)
    {
        if (_cam < 0) return;
        if (ev is InputEventMouseMotion motion) _mouse += motion.Relative;
        else if (ev is InputEventMouseButton { Pressed: true } b && b.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            _wheel += b.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
    }

    private bool _snapping;
    private double _snapAt;
    private int _snaps;

    private void Snap(ConcertCameras cams)
    {
        _snapping = false;
        ConcertCameras.ProgramWanted--;
        if (cams.ProgramTexture.GetImage() is not { } image) return;
        string dir = ProjectSettings.GlobalizePath("user://concert");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"program_{++_snaps}_cam{cams.OnAir + 1}.png");
        image.SavePng(path);
        GD.Print($"[concert] program still: {path}");
    }

    public override void _Process(double delta)
    {
        var rig = Rig;
        var cams = rig?.Cameras;
        bool operating = _cam >= 0 && cams != null;
        if (operating) Operate(cams!, delta);
        bool show = (_viewing || operating) && cams != null;
        if (_view.Visible != show)
        {
            _view.Visible = show;
            _caption.Visible = show;
        }
        if (show)
        {
            var want = operating ? cams!.FinderTexture : cams!.ProgramTexture;
            if (_view.Texture != want) _view.Texture = want;
            string caption = operating
                ? $"CAM {_cam + 1} {ConcertCameras.Names[_cam]} · {(cams.OnAir == _cam ? "ON AIR" : "off air")} · zoom {cams.Rigs[_cam].Fov:F0}°\n"
                  + InputHints.Format("{move_forward} move · {fly_up}/{fly_down} up/down · mouse, right stick: aim · {cam_zoom_in}/{cam_zoom_out} zoom · {cam_cut} cut to air · {ui_cancel} leave")
                : $"{rig!.Event.Artist} · CAM {cams.OnAir + 1} {ConcertCameras.Names[cams.OnAir]}{(cams.Manual ? "" : " · auto")}";
            if (_caption.Text != caption) _caption.Text = caption;
        }
        if (_snapping && cams != null && Core.RealClock.Now >= _snapAt) Snap(cams);
        // NDI: the program read back at most 30 times a second
        if (_ndi != null && cams != null && Core.RealClock.Now - _sentAt >= 1.0 / 30)
        {
            _sentAt = Core.RealClock.Now;
            if (cams.ProgramTexture.GetImage() is { } image) _ndi.Offer(image);
        }
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (_cam >= 0 && ev.IsActionPressed("ui_cancel"))
        {
            ConcertService.Instance?.AskCamera(_stadium, -1);
            GetViewport().SetInputAsHandled();
        }
        else if (_viewing && ev.IsActionPressed("ui_cancel"))
        {
            Command(new[] { "view" });
            GetViewport().SetInputAsHandled();
        }
    }
}
