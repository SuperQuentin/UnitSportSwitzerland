using System.Reflection;
using Godot;
using UnitSport.Core;

namespace UnitSport.Audio;

/// <summary>
/// Player for every synthesised game sound (docs/notes/audio/sound-player.md).
///
/// <para>
/// <c>godot --path . -- --sounds</c>: Left/Right (A/D, D-pad) step through a category and play the
/// sound, Up/Down (W/S, D-pad) switch category, Enter/Space/A replays it (stops a loop), -/+ the
/// volume, a click on the list plays that sound. <c>--sounds,check</c> builds every sound without
/// playing, flags a NaN, a clipped or an empty one, and quits with a RESULT line.
/// </para>
/// <para>
/// The list is not written here: static <see cref="SfxBank"/> and <c>AudioStreamWav</c> members are
/// found by reflection (a bank is one entry per variant), and <see cref="SoundShowcaseAttribute"/>
/// methods add sets (surfaces, engines, occasions).
/// </para>
/// </summary>
public partial class SoundPlayer : Control
{
    private sealed record Entry(string Category, string Name, Func<AudioStreamWav> Make);

    private List<(string Name, List<Entry> Entries)> _categories = new();
    private int _category, _index;
    private AudioStreamPlayer _player = null!;
    private Label _header = null!, _footer = null!;
    private ItemList _list = null!;
    private WaveView _wave = null!;
    private float _volumeDb = -12f;
    private double _length;
    private string? _check;

    public static bool Requested() => CmdArgs.FlagWithShot("--sounds").Requested;

    public override void _Ready()
    {
        _check = CmdArgs.FlagWithShot("--sounds").Shot;
        _categories = Discover()
            .GroupBy(e => e.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()))
            .ToList();
        if (_check != null) { CheckAll(); return; }

        SetAnchorsPreset(LayoutPreset.FullRect);
        RenderingServer.SetDefaultClearColor(new Color(0.12f, 0.14f, 0.17f));
        _player = new AudioStreamPlayer { VolumeDb = _volumeDb };
        AddChild(_player);

        _header = new Label { Position = new Vector2(12, 8) };
        AddChild(_header);
        _list = new ItemList { Position = new Vector2(12, 70), Size = new Vector2(360, 600), FocusMode = FocusModeEnum.None };
        _list.ItemSelected += i => Show(_category, (int)i);
        AddChild(_list);
        _wave = new WaveView { Position = new Vector2(390, 70), Size = new Vector2(860, 300) };
        AddChild(_wave);
        _footer = new Label { Position = new Vector2(12, 690), Text = "Left/Right: sound   Up/Down: category   Enter/Space: replay or stop   -/+: volume   click: play" };
        AddChild(_footer);
        GetViewport().SizeChanged += Layout;
        Layout();
        Show(0, 0);
    }

    private void Layout()
    {
        var size = GetViewport().GetVisibleRect().Size;
        _list.Size = new Vector2(360, Mathf.Max(100, size.Y - 130));
        _wave.Size = new Vector2(Mathf.Max(100, size.X - 402), 300);
        _footer.Position = new Vector2(12, size.Y - 32);
    }

    // ---- discovery -------------------------------------------------------------------------

    private static IEnumerable<Entry> Discover()
    {
        var banks = new List<(string Category, string Member, SfxBank Bank)>();
        var wavs = new List<(string Category, string Member, AudioStreamWav Wav)>();
        foreach (var type in Types())
        {
            foreach (var (member, value) in StaticValues(type))
                switch (value)
                {
                    case SfxBank bank: banks.Add((type.Name, member, bank)); break;
                    case AudioStreamWav wav: wavs.Add((type.Name, member, wav)); break;
                }
        }

        // a property and its private cache field, or Landing => LandingBank.Variants[0]: one entry
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var (category, _, bank) in banks)
        {
            if (!seen.Add(bank)) continue;
            foreach (var v in bank.Variants) seen.Add(v);
            for (int i = 0; i < bank.Variants.Length; i++)
            {
                var variant = bank.Variants[i];
                yield return new Entry(category, $"{bank.Name} {i + 1}/{bank.Variants.Length}", () => variant);
            }
        }
        foreach (var (category, member, wav) in wavs)
            if (seen.Add(wav))
                yield return new Entry(category, wav.LoopMode == AudioStreamWav.LoopModeEnum.Disabled ? member : member + " (loop)", () => wav);

        foreach (var type in Types())
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (method.GetCustomAttribute<SoundShowcaseAttribute>() is { } tag)
                    foreach (var entry in FromSet(method, tag))
                        yield return entry;
    }

    private static IEnumerable<Entry> FromSet(MethodInfo method, SoundShowcaseAttribute tag)
    {
        if (method.GetParameters().Length != 0 || !typeof(IEnumerable<(string, string, Func<float[]>)>).IsAssignableFrom(method.ReturnType))
        {
            GD.PushError($"[sounds] [SoundShowcase] on {method.DeclaringType!.Name}.{method.Name}: it must take no parameters and return IEnumerable<(string Category, string Name, Func<float[]> Make)>");
            yield break;
        }
        var set = (IEnumerable<(string Category, string Name, Func<float[]> Make)>)method.Invoke(null, null)!;
        foreach (var (category, name, make) in set)
            yield return new Entry(category.Length > 0 ? category : tag.Category, name, () => Dsp.Encode(make()));
    }

    private static IEnumerable<Type> Types() =>
        typeof(SoundPlayer).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("UnitSport", StringComparison.Ordinal) == true && !t.ContainsGenericParameters)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    /// <summary>The static properties (before fields, so a cache field is seen after its property) holding a bank or a stream.</summary>
    private static IEnumerable<(string Member, object Value)> StaticValues(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var p in type.GetProperties(all).OrderBy(p => p.MetadataToken))
            if (IsSound(p.PropertyType) && p.GetMethod != null && p.GetIndexParameters().Length == 0)
                if (Read(() => p.GetValue(null), type, p.Name) is { } v) yield return (p.Name, v);
        foreach (var f in type.GetFields(all).OrderBy(f => f.MetadataToken))
            if (IsSound(f.FieldType) && !f.Name.Contains('<'))
                if (f.GetValue(null) is { } v) yield return (f.Name.TrimStart('_'), v);
    }

    private static bool IsSound(Type t) => t == typeof(SfxBank) || t == typeof(AudioStreamWav);

    private static object? Read(Func<object?> get, Type type, string name)
    {
        try { return get(); }
        catch (Exception e)
        {
            GD.PushError($"[sounds] {type.Name}.{name}: {(e is TargetInvocationException { InnerException: { } i } ? i : e)}");
            return null;
        }
    }

    // ---- samples ---------------------------------------------------------------------------

    /// <summary>The stream's samples mixed to mono floats.</summary>
    private static float[] Samples(AudioStreamWav w)
    {
        var d = w.Data;
        if (w.Format != AudioStreamWav.FormatEnum.Format16Bits) return [];
        int channels = w.Stereo ? 2 : 1, n = d.Length / 2 / channels;
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (i * channels + c) * 2;
                sum += (short)(d[at] | (d[at + 1] << 8)) / 32767f;
            }
            s[i] = sum / channels;
        }
        return s;
    }

    private static (float Peak, float Rms, bool Finite) Stats(float[] s)
    {
        float peak = 0; double sum = 0; bool finite = true;
        foreach (float v in s)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { finite = false; continue; }
            peak = Math.Max(peak, Math.Abs(v)); sum += v * v;
        }
        return (peak, (float)Math.Sqrt(sum / Math.Max(1, s.Length)), finite);
    }

    // ---- check -----------------------------------------------------------------------------

    private void CheckAll()
    {
        int count = 0, bad = 0;
        foreach (var (name, entries) in _categories)
            foreach (var e in entries)
            {
                count++;
                try
                {
                    var w = e.Make();
                    var (peak, _, finite) = Stats(Samples(w));
                    if (!finite || peak > 1.0001f || peak < 1e-4f)
                    {
                        bad++;
                        GD.Print($"[sounds] {name} / {e.Name}: peak={peak:F4}{(finite ? "" : " not finite")}");
                    }
                }
                catch (Exception ex)
                {
                    bad++;
                    GD.Print($"[sounds] {name} / {e.Name}: threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        GD.Print($"[sounds] {count} sounds in {_categories.Count} categories");
        GD.Print(bad == 0 ? "[sounds] RESULT: ok" : $"[sounds] RESULT: FAILED ({bad} sounds clip, are empty or throw)");
        GetTree().Quit(bad == 0 ? 0 : 1);
    }

    // ---- display and playback --------------------------------------------------------------

    private static int Wrap(int i, int n) => (i % n + n) % n;

    private void Show(int category, int index)
    {
        if (_categories.Count == 0) { _header.Text = "No sounds found"; return; }
        bool newCategory = category != _category || _list.ItemCount == 0;
        _category = Wrap(category, _categories.Count);
        var entries = _categories[_category].Entries;
        _index = Wrap(index, entries.Count);
        var entry = entries[_index];

        if (newCategory)
        {
            _list.Clear();
            foreach (var e in entries) _list.AddItem(e.Name);
        }
        _list.Select(_index);
        _list.EnsureCurrentIsVisible();

        string detail;
        try
        {
            var stream = entry.Make();
            var samples = Samples(stream);
            var (peak, rms, _) = Stats(samples);
            _length = samples.Length / (double)Math.Max(1, stream.MixRate);
            bool loop = stream.LoopMode != AudioStreamWav.LoopModeEnum.Disabled;
            detail = $"{_length:0.00} s{(loop ? " (loop)" : "")}   peak {peak:0.00}   rms {rms:0.000}   {(stream.Stereo ? "stereo" : "mono")} {stream.MixRate} Hz";
            _wave.Set(samples);
            _player.Stream = stream;
            _player.Play();
        }
        catch (Exception e)
        {
            detail = $"FAILED: {e.GetType().Name}: {e.Message}";
            GD.PushError($"[sounds] {entry.Category} / {entry.Name}: {e}");
            _player.Stop();
            _wave.Set([]);
        }
        _header.Text = $"{_categories[_category].Name}  ({_category + 1}/{_categories.Count})   ›   {entry.Name}  ({_index + 1}/{entries.Count})\n{detail}   volume {_volumeDb:0} dB";
    }

    public override void _Process(double delta)
    {
        if (_check != null) return;
        _wave.Head = _player.Playing && _length > 0 ? (float)(_player.GetPlaybackPosition() % _length / _length) : -1f;
        _wave.QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_check != null) return;
        switch (e)
        {
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Right or Key.D:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadRight }:
                Show(_category, _index + 1); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Left or Key.A:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadLeft }:
                Show(_category, _index - 1); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Down or Key.S:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadDown }:
                Show(_category + 1, 0); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Up or Key.W:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadUp }:
                Show(_category - 1, 0); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Enter or Key.Space:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.A }:
                if (_player.Playing && _player.Stream is AudioStreamWav { LoopMode: not AudioStreamWav.LoopModeEnum.Disabled }) _player.Stop();
                else _player.Play();
                break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Minus or Key.KpSubtract:
                Volume(-3f); break;
            case InputEventKey { Pressed: true } k when k.PhysicalKeycode is Key.Equal or Key.KpAdd:
                Volume(3f); break;
        }
    }

    private void Volume(float step)
    {
        _volumeDb = Mathf.Clamp(_volumeDb + step, -40f, 0f);
        _player.VolumeDb = _volumeDb;
        Show(_category, _index);
    }

    /// <summary>The waveform as a min/max column per pixel, with the playhead.</summary>
    private sealed partial class WaveView : Control
    {
        private float[] _samples = [];
        public float Head = -1f;

        public void Set(float[] samples) { _samples = samples; QueueRedraw(); }

        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.07f, 0.08f, 0.1f));
            float mid = Size.Y * 0.5f;
            DrawLine(new Vector2(0, mid), new Vector2(Size.X, mid), new Color(1, 1, 1, 0.15f));
            int w = (int)Size.X;
            if (_samples.Length == 0 || w < 2) return;
            for (int x = 0; x < w; x++)
            {
                int a = (int)((long)x * _samples.Length / w), b = Math.Max(a + 1, (int)((long)(x + 1) * _samples.Length / w));
                float lo = 0, hi = 0;
                for (int i = a; i < b && i < _samples.Length; i++) { lo = Math.Min(lo, _samples[i]); hi = Math.Max(hi, _samples[i]); }
                DrawLine(new Vector2(x, mid - hi * mid), new Vector2(x, mid - lo * mid + 1), new Color(0.45f, 0.8f, 0.55f));
            }
            if (Head >= 0) DrawLine(new Vector2(Head * Size.X, 0), new Vector2(Head * Size.X, Size.Y), new Color(1f, 0.85f, 0.3f), 2f);
        }
    }
}
