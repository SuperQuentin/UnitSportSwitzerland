using System;
using System.Diagnostics;
using System.IO;
using Godot;
using UnitSport.Core;

namespace UnitSport.Trailer;

/// <summary>
/// Writes frames to an MP4 through ffmpeg (<see cref="BundledTools"/>): raw RGBA on its stdin,
/// x264 out. One recorder per shot. The frame is read back after the renderer has drawn it
/// (<c>FramePostDraw</c>; a read from <c>_Process</c> returns whatever the render thread last left,
/// <c>docs/notes/gpx/get-image-root-viewport-from.md</c>), so under <c>--fixed-fps</c> every frame
/// is exactly one step of game time however slowly it renders.
/// </summary>
public sealed class FrameRecorder : IDisposable
{
    private readonly Process _ffmpeg;
    private readonly Stream _pipe;
    private readonly int _width, _height;
    public string Path { get; }
    public int Frames { get; private set; }
    public string? Error { get; private set; }

    private FrameRecorder(Process ffmpeg, string path, int width, int height)
    {
        _ffmpeg = ffmpeg;
        _pipe = ffmpeg.StandardInput.BaseStream;
        Path = path;
        _width = width;
        _height = height;
    }

    /// <summary>Starts ffmpeg for a <paramref name="width"/>×<paramref name="height"/> film at <paramref name="fps"/>; null if it cannot run.</summary>
    public static FrameRecorder? Start(string path, int width, int height, int fps)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var info = new ProcessStartInfo(BundledTools.Resolve("ffmpeg"))
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // an argument list, never a shell string
        foreach (var a in new[]
                 {
                     "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba",
                     "-s", $"{width}x{height}", "-r", fps.ToString(), "-i", "-",
                     "-c:v", "libx264", "-preset", "medium", "-crf", "16", "-pix_fmt", "yuv420p",
                     "-movflags", "+faststart", path,
                 })
            info.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(info);
            if (p == null) return null;
            // drain stderr so a chatty ffmpeg never blocks on a full pipe
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) GD.PrintErr($"[trailer] ffmpeg: {e.Data}"); };
            p.BeginErrorReadLine();
            return new FrameRecorder(p, path, width, height);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[trailer] cannot start ffmpeg: {e.Message}");
            return null;
        }
    }

    /// <summary>Appends the viewport's last drawn frame.</summary>
    public void Write(Viewport viewport)
    {
        if (Error != null) return;
        var image = viewport.GetTexture().GetImage();
        if (image.GetWidth() != _width || image.GetHeight() != _height)
            image.Resize(_width, _height, Image.Interpolation.Bilinear);
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        try
        {
            _pipe.Write(image.GetData());
            Frames++;
        }
        catch (IOException e)
        {
            Error = e.Message;
            GD.PrintErr($"[trailer] ffmpeg pipe closed: {e.Message}");
        }
    }

    /// <summary>Closes the film and waits for ffmpeg to finish writing it.</summary>
    public void Dispose()
    {
        try { _pipe.Close(); }
        catch (IOException) { }
        if (!_ffmpeg.WaitForExit(60_000)) _ffmpeg.Kill();
        _ffmpeg.Dispose();
    }
}
