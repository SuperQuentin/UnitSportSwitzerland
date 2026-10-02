using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace UnitSport.Core;

/// <summary>
/// The one background writer for saves (#221): files are written atomically, one at a time, in the
/// order they were first queued; a file queued again before it was written keeps its place and
/// only its newest text is written. <see cref="Flush"/> waits for everything queued (on quit).
/// Plain .NET, no Godot: <c>JsonStore.SaveAsync</c> is the game's entry point.
/// </summary>
public static class SaveQueue
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Order = new();
    private static readonly Dictionary<string, (string Text, Action<Exception>? OnError)> Pending = new();
    private static Task _worker = Task.CompletedTask;
    private static bool _running;

    static SaveQueue()
    {
        // belt and braces: Main flushes on quit, this covers a plain process exit
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    /// <summary>Queues <paramref name="text"/> for <paramref name="path"/> (an OS path).
    /// <paramref name="onError"/> runs on the writer thread if the write throws.</summary>
    public static void Enqueue(string path, string text, Action<Exception>? onError = null)
    {
        lock (Gate)
        {
            if (!Pending.ContainsKey(path)) Order.Enqueue(path);
            Pending[path] = (text, onError);
            if (_running) return;
            _running = true;
            _worker = Task.Run(Drain);
        }
    }

    /// <summary>Blocks until every queued save is on disk.</summary>
    public static void Flush()
    {
        while (true)
        {
            Task t;
            lock (Gate)
            {
                if (!_running) return;
                t = _worker;
            }
            t.Wait();
        }
    }

    private static void Drain()
    {
        while (true)
        {
            string path;
            (string Text, Action<Exception>? OnError) job;
            lock (Gate)
            {
                if (Order.Count == 0) { _running = false; return; }
                path = Order.Dequeue();
                job = Pending[path];
                Pending.Remove(path);
            }
            try { WriteAtomic(path, job.Text); }
            catch (Exception e) { job.OnError?.Invoke(e); }
        }
    }

    /// <summary>Writes <paramref name="text"/> (UTF-8, no BOM) to a unique <c>.part</c> next to
    /// <paramref name="path"/>, then moves it over: the file is always the old or the new version.</summary>
    public static void WriteAtomic(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // unique, so two saves of the same file at once (a worker thread and the main one) cannot collide
        string tmp = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
