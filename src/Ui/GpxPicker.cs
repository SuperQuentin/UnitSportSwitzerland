using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// Picks the GPX tracks for a replay: the ones dropped in <c>user://gpx</c>, the ones used
/// recently, and (in a development checkout) the samples at the project root — or any file
/// through Browse. Several ticked make a race.
/// </summary>
public partial class GpxPicker : Screen
{
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
        var openFolder = UiKit.Button("Open folder");
        openFolder.TooltipText = "Drop .gpx files in here and they are listed";
        openFolder.Pressed += () =>
        {
            DirAccess.MakeDirRecursiveAbsolute(Folder);
            OS.ShellOpen(ProjectSettings.GlobalizePath(Folder));
        };
        header.AddChild(openFolder);
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
            _list.AddChild(UiKit.Text("No tracks yet. Browse for a .gpx file, or put some in the folder (Open folder).",
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
            _dialog.AddFilter("*.gpx", "GPX tracks");
            _dialog.FilesSelected += paths =>
            {
                foreach (string p in paths) { _extra.Add(p); _picked.Add(p); }
                Refresh();
            };
            AddChild(_dialog);
        }
        _dialog.PopupCentered();
    }

    public override void OnShown()
    {
        if (_list.GetChildCount() > 0) PlayerInput.FocusFirst(_list);
        else _use.CallDeferred(Control.MethodName.GrabFocus);
    }
}
