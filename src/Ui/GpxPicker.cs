using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// Picks the GPX tracks for a replay: the ones dropped in <c>user://gpx</c>, the ones used
/// recently, and (in a development checkout) the samples at the project root — or any file
/// through Browse. Several ticked make a race.
///
/// <para>
/// On Android (#1027) Browse is the system document picker: an app sees none of the shared storage
/// through the filesystem (scoped storage), and the picker needs no permission. What it returns are
/// <c>content://</c> URIs, copied into <see cref="Folder"/> (<see cref="Import"/>), so a track stays
/// listed. Note: docs/notes/gpx/android-gpx-picker.md.
/// </para>
/// </summary>
public partial class GpxPicker : Screen
{
    private static readonly bool Android = OS.GetName() == "Android";

    private readonly HashSet<string> _picked;
    private readonly Action<IReadOnlyList<string>> _done;
    private VBoxContainer _list = null!;
    private Button _use = null!;
    private FileDialog? _dialog;
    private readonly List<string> _extra = new();

    public const string Folder = "user://gpx";

    private GpxPicker(IEnumerable<string> picked, Action<IReadOnlyList<string>> done)
    {
        _picked = new HashSet<string>(picked);
        _extra.AddRange(_picked);
        _done = done;
        Name = "GpxPicker";
    }

    public static GpxPicker Create(IEnumerable<string> picked, Action<IReadOnlyList<string>> done) => new(picked, done);

    public override void _Ready()
    {
        var (body, header) = Framed("Choose tracks", "Tick one to replay it, several to race them", new Vector2(760, 540));
        // the app's folder is private on a phone: no file manager reaches it
        if (!Android)
        {
            var openFolder = UiKit.Button("Open folder");
            openFolder.TooltipText = "Drop .gpx files in here and they are listed";
            openFolder.Pressed += () =>
            {
                DirAccess.MakeDirRecursiveAbsolute(Folder);
                OS.ShellOpen(ProjectSettings.GlobalizePath(Folder));
            };
            header.AddChild(openFolder);
        }
        var browse = UiKit.Button("Browse…");
        browse.Pressed += Browse;
        header.AddChild(browse);

        var (scroll, rows) = UiKit.ScrollPage(4);
        body.AddChild(scroll);
        _list = rows;

        var footer = UiKit.HBox(10);
        footer.Alignment = BoxContainer.AlignmentMode.End;
        _use = UiKit.Button("Use tracks", primary: true, minWidth: 160);
        _use.Pressed += () => { _done(_picked.ToList()); Shell.Back(); };
        footer.AddChild(_use);
        body.AddChild(footer);
        Refresh();
    }

    private IEnumerable<string> Candidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Gpx(string dir) =>
            System.IO.Directory.Exists(dir) ? System.IO.Directory.EnumerateFiles(dir, "*.gpx") : Array.Empty<string>();

        var all = _extra
            .Concat(GameSettings.Current.RecentGpx)
            .Concat(Gpx(ProjectSettings.GlobalizePath(Folder)))
            .Concat(OS.HasFeature("template") ? Array.Empty<string>() : Gpx(ProjectSettings.GlobalizePath("res://")));
        foreach (string p in all)
            if (System.IO.File.Exists(p) && seen.Add(System.IO.Path.GetFullPath(p)))
                yield return p;
    }

    private void Refresh()
    {
        foreach (var c in _list.GetChildren()) c.QueueFree();
        var files = Candidates().ToList();
        if (files.Count == 0)
        {
            _list.AddChild(UiKit.Text(Android
                    ? "No tracks yet. Browse picks .gpx files anywhere on the phone (Downloads, Drive, a file manager)."
                    : "No tracks yet. Browse for a .gpx file, or put some in the folder (Open folder).",
                UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        }
        foreach (string path in files)
        {
            var info = new System.IO.FileInfo(path);
            var row = new CheckBox
            {
                Text = $"{System.IO.Path.GetFileNameWithoutExtension(path)}",
                ButtonPressed = _picked.Contains(path),
                FocusMode = FocusModeEnum.All,
                TooltipText = path,
                CustomMinimumSize = new Vector2(0, 40),
            };
            string p = path;
            row.Toggled += on => { if (on) _picked.Add(p); else _picked.Remove(p); UpdateUse(); };
            var line = UiKit.HBox(10);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.AddChild(row);
            line.AddChild(UiKit.Text($"{info.Length / 1024.0:F0} KB  ·  {info.LastWriteTime:d MMM yyyy}", UiTheme.FontSmall, UiTheme.TextFaint));
            _list.AddChild(line);
        }
        UpdateUse();
    }

    private void UpdateUse()
    {
        _use.Disabled = _picked.Count == 0;
        _use.Text = _picked.Count switch { 0 => "Use tracks", 1 => "Use 1 track", _ => $"Race {_picked.Count} tracks" };
    }

    private void Browse()
    {
        if (_dialog == null)
        {
            _dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFiles,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Add GPX tracks",
                Size = new Vector2I(860, 580),
            };
            // Android: the system picker, unfiltered (file managers often call a .gpx octet-stream,
            // which an application/gpx+xml filter would hide); Import refuses what is not GPX
            if (Android) _dialog.UseNativeDialog = true;
            else _dialog.AddFilter("*.gpx", "GPX tracks");
            _dialog.FilesSelected += paths =>
            {
                var refused = new List<string>();
                foreach (string p in paths)
                {
                    if ((Android ? Import(p) : p) is not { } local) { refused.Add(p); continue; }
                    _extra.Add(local);
                    _picked.Add(local);
                }
                Refresh();
                if (refused.Count > 0)
                    Modal.Inform(this, "Not a GPX track", refused.Count == 1
                        ? $"{DisplayName(refused[0])} is not a GPX track, or could not be read."
                        : $"{refused.Count} files are not GPX tracks, or could not be read.");
            };
            AddChild(_dialog);
        }
        _dialog.PopupCentered();
    }

    /// <summary>
    /// Copies a GPX track (a <c>content://</c> URI from the Android picker, or any path Godot can
    /// read) into <see cref="Folder"/>, and returns where it landed; null when it is not a GPX track or
    /// cannot be read. The same track picked twice is kept once.
    /// </summary>
    public static string? Import(string source)
    {
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(source);
        if (bytes.Length == 0) return null;
        string head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        if (!head.Contains("<gpx", StringComparison.OrdinalIgnoreCase)) return null;

        string dir = ProjectSettings.GlobalizePath(Folder);
        System.IO.Directory.CreateDirectory(dir);
        string name = DisplayName(source);
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (!name.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase)) name += ".gpx";
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
        string path = System.IO.Path.Combine(dir, name);
        for (int n = 2; System.IO.File.Exists(path); n++)
        {
            if (System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return path;
            path = System.IO.Path.Combine(dir, $"{stem} ({n}).gpx");
        }
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// The file name in a path or a content URI: a document URI ends in an encoded id such as
    /// <c>primary%3ADownload%2Fride.gpx</c>, whose last segment is the name.
    /// </summary>
    private static string DisplayName(string source)
    {
        string s = Uri.UnescapeDataString(source);
        int cut = Math.Max(s.LastIndexOf('/'), Math.Max(s.LastIndexOf(':'), s.LastIndexOf('\\')));
        s = s[(cut + 1)..];
        return s.Length > 0 ? s : "track.gpx";
    }

    public override void OnShown()
    {
        if (_list.GetChildCount() > 0) PlayerInput.FocusFirst(_list);
        else _use.CallDeferred(Control.MethodName.GrabFocus);
    }
}
