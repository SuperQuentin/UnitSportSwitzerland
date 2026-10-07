using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using FileAccess = System.IO.FileAccess;

namespace UnitSport.Audio.Cd;

/// <summary>
/// A player's own audio file sent to the server to become a shared CD (#736). At
/// <c>World/CdUpload</c> on both sides (the RPC path).
///
/// <para>
/// <b>Client</b>: <see cref="Send"/> checks the file (an audio extension, at most
/// <see cref="MaxBytes"/>), asks the server with its name, size and SHA-256, and once let in streams
/// it in <see cref="ChunkSize"/> chunks, <see cref="Window"/> at a time ahead of the server's
/// acknowledgements, so a 20 MB song does not choke the link the game plays on. Progress and the
/// server's word come back as the burn box's status lines (<see cref="CdLibrary.BurnStatus"/>).
/// </para>
///
/// <para>
/// <b>Server</b>, before a byte is kept: the player may burn here, one upload a minute each and
/// <see cref="MaxConcurrent"/> at once, the size and extension, a working antivirus
/// (<see cref="CdScanner"/>: none, and uploads are off: it fails closed), and room on the disk
/// (<see cref="DiskReserve"/> left over). The chunks go straight to a file in a folder of its own
/// under <c>user://cds/uploads</c>, in order, from that peer only; then the size and hash must
/// match, the antivirus must call it clean, and only then does the burner see it: ffmpeg decodes
/// it (no audio, no CD), cuts it at 10 minutes and re-encodes it; only that Ogg is kept and the
/// upload's folder is deleted (<see cref="CdLibrary.BurnUpload"/>). A stalled or abandoned upload
/// (the peer leaves, <see cref="StallSeconds"/> without a chunk) is deleted too.
/// </para>
/// </summary>
public partial class CdUpload : Node
{
    public const int MaxBytes = CdUploadRules.MaxBytes;
    public static string[] Extensions => CdUploadRules.Extensions;
    private static bool AudioFile(string path) => CdUploadRules.AudioFile(path);
    private static string SafeName(string name) => CdUploadRules.SafeName(name);

    public const string NodeName = "CdUpload";
    public const int ChunkSize = 32 * 1024;
    private const int Window = 16;
    private const int MaxConcurrent = 2;
    private const double Cooldown = 60, StallSeconds = 30;
    /// <summary>Free space the disk must keep after an upload and its burn, bytes.</summary>
    private const long DiskReserve = 500L * 1024 * 1024;


    public static CdUpload? Instance { get; private set; }

    private bool _server;

    public static CdUpload Create(Node world, bool server)
    {
        var u = new CdUpload { Name = NodeName, _server = server };
        world.AddChild(u);
        Instance = u;
        return u;
    }

    public override void _Ready()
    {
        if (!_server) return;
        Multiplayer.PeerDisconnected += Abandon;
        TryDelete(UploadRoot);   // what a crash left half-sent or unscanned
        _ = CdScanner.AvailableAsync();   // a scan of a harmless file takes a moment: start it now
    }

    public override void _ExitTree()
    {
        if (_server) Multiplayer.PeerDisconnected -= Abandon;
        foreach (var peer in new List<long>(_incoming.Keys)) Abandon(peer);
        if (Instance == this) Instance = null;
    }

    private static void Say(string text) => CdLibrary.Instance?.Report(0, text);


    // ---- client: sending ---------------------------------------------------------------------

    private FileStream? _sending;
    private long _sendSize;
    private int _nextChunk, _acked, _chunks;

    public void Send(string path)
    {
        if (_sending != null) { Say("Already uploading a file; wait for it."); return; }
        if (!AudioFile(path)) { Say("Only audio files: " + string.Join(" ", Extensions)); return; }
        var info = new FileInfo(path);
        if (!info.Exists) { Say("That file is gone."); return; }
        if (info.Length == 0 || info.Length > MaxBytes) { Say($"Files up to {MaxBytes / (1024 * 1024)} MB only ({info.Length / (1024 * 1024)} MB)."); return; }
        string hash;
        try
        {
            using var read = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(read));
            _sending = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) { Say($"Could not read the file: {e.Message}"); return; }
        _sendSize = info.Length;
        _chunks = (int)((info.Length + ChunkSize - 1) / ChunkSize);
        _nextChunk = 0;
        _acked = 0;
        _sendingStarted = false;
        Say("Uploading… asking the server");
        RpcId(1, MethodName.Begin, Path.GetFileName(path), info.Length, hash);
    }

    private bool _sendingStarted;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Accepted() => _sendingStarted = _sending != null;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refused(string why)
    {
        StopSending();
        Say(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Ack(int received)
    {
        if (_sending == null) return;
        int before = _acked * 10 / Math.Max(1, _chunks);
        _acked = Math.Max(_acked, received);
        if (_acked * 10 / Math.Max(1, _chunks) != before || _acked == _chunks)
            Say($"Uploading… {_acked * 100 / Math.Max(1, _chunks)} %");
        if (_acked >= _chunks) StopSending();
    }

    private void StopSending()
    {
        _sending?.Dispose();
        _sending = null;
        _sendingStarted = false;
    }

    public override void _Process(double delta)
    {
        if (_server) { Housekeeping(); return; }
        if (_sending == null || !_sendingStarted || !NetLink.Online(this)) return;
        var buffer = new byte[ChunkSize];
        while (_nextChunk < _chunks && _nextChunk - _acked < Window)
        {
            int n = _sending.Read(buffer, 0, ChunkSize);
            if (n <= 0) break;
            RpcId(1, MethodName.Chunk, _nextChunk, n == ChunkSize ? buffer : buffer[..n]);
            _nextChunk++;
            if (n == ChunkSize) buffer = new byte[ChunkSize];
        }
    }

    // ---- server: receiving -------------------------------------------------------------------

    private sealed class Incoming
    {
        public required string Folder, File, Name, Hash;
        public required long Size;
        public required FileStream Stream;
        public int Next, Chunks;
        public double LastAt;
    }

    private readonly Dictionary<long, Incoming> _incoming = new();
    private readonly Dictionary<long, double> _lastStart = new();
    private readonly ConcurrentQueue<(long Peer, string File, string Folder, ScanVerdict Verdict, string Engine)> _scanned = new();

    private static string UploadRoot => Path.Combine(CdLibrary.Directory, "uploads");

    private static double Now => Time.GetTicksMsec() / 1000.0;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Begin(string name, long size, string hash)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (Refusal(peer, name, size, hash) is { } why) { RpcId(peer, MethodName.Refused, why); return; }
        string folder = Path.Combine(UploadRoot, $"{peer}_{Guid.NewGuid():N}");
        string file = Path.Combine(folder, SafeName(name));
        try
        {
            Directory.CreateDirectory(folder);
            var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            _incoming[peer] = new Incoming
            {
                Folder = folder, File = file, Name = name, Hash = hash, Size = size, Stream = stream,
                Chunks = (int)((size + ChunkSize - 1) / ChunkSize), LastAt = Now,
            };
        }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] upload from {peer}: {e.Message}");
            TryDelete(folder);
            RpcId(peer, MethodName.Refused, "The server could not take the file.");
            return;
        }
        _lastStart[peer] = Now;
        GD.Print($"[cd] upload from peer {peer}: {name} ({size} bytes)");
        RpcId(peer, MethodName.Accepted);
    }

    /// <summary>Why the server will not take this upload, or null when it will.</summary>
    private string? Refusal(long peer, string name, long size, string hash)
    {
        if (CdLibrary.Instance is { MayBurn: { } may } && !may(peer)) return "You may not burn CDs on this server.";
        if (_incoming.ContainsKey(peer)) return "You are already uploading a file.";
        if (_incoming.Count >= MaxConcurrent) return "The server is busy with other uploads; try again in a minute.";
        if (_lastStart.TryGetValue(peer, out double last) && Now - last < Cooldown)
            return $"One upload a minute: {(int)(Cooldown - (Now - last))} s to wait.";
        if (size <= 0 || size > MaxBytes) return $"Files up to {MaxBytes / (1024 * 1024)} MB only.";
        if (name.Length is 0 or > 200 || !AudioFile(name)) return "Only audio files: " + string.Join(" ", Extensions);
        if (hash.Length != 64) return "The upload was garbled.";
        var scanner = CdScanner.AvailableAsync();
        if (!scanner.IsCompleted) return "The server is still starting its virus scanner; try again in a moment.";
        if (!scanner.Result) return "This server cannot check files for viruses, so uploads are off. YouTube links still work.";
        if (!CdBurner.FfmpegAvailable()) return "ffmpeg is missing on the server: files cannot be burnt.";
        if (FreeSpace() is long free && free - size * 3 < DiskReserve) return "The server's disk is too full for another CD.";
        return null;
    }

    /// <summary>Free bytes on the disk the CDs are on, or null when it cannot be told.</summary>
    private static long? FreeSpace()
    {
        try
        {
            Directory.CreateDirectory(CdLibrary.Directory);
            string? root = Path.GetPathRoot(Path.GetFullPath(CdLibrary.Directory));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Chunk(int index, byte[] data)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_incoming.TryGetValue(peer, out var up)) return;
        bool last = index == up.Chunks - 1;
        long expected = last ? up.Size - (long)index * ChunkSize : ChunkSize;
        if (index != up.Next || data.Length != expected)
        {
            Drop(peer, "The upload was garbled; try again.");
            return;
        }
        try { up.Stream.Write(data, 0, data.Length); }
        catch (Exception e)
        {
            GD.PushWarning($"[cd] upload from {peer}: {e.Message}");
            Drop(peer, "The server could not store the file.");
            return;
        }
        up.Next++;
        up.LastAt = Now;
        RpcId(peer, MethodName.Ack, up.Next);
        if (!last) return;

        up.Stream.Dispose();
        _incoming.Remove(peer);
        CdLibrary.Instance?.Report(peer, "Scanning for viruses…");
        string file = up.File, folder = up.Folder, hash = up.Hash;
        long size = up.Size;
        Task.Run(async () =>
        {
            var verdict = ScanVerdict.Failed;
            string engine = "";
            try
            {
                var info = new FileInfo(file);
                string got;
                using (var read = File.OpenRead(file)) got = Convert.ToHexString(SHA256.HashData(read));
                if (info.Length != size || !got.Equals(hash, StringComparison.OrdinalIgnoreCase)) verdict = ScanVerdict.Failed;
                else (verdict, engine) = await CdScanner.ScanAsync(file, CancellationToken.None);
            }
            catch (Exception e) { GD.Print($"[cd] scanning an upload failed: {e.Message}"); }
            _scanned.Enqueue((peer, file, folder, verdict, engine));
        });
    }

    private void Housekeeping()
    {
        while (_scanned.TryDequeue(out var s))
        {
            var library = CdLibrary.Instance;
            switch (s.Verdict)
            {
                case ScanVerdict.Clean when library != null:
                    GD.Print($"[cd] upload from {s.Peer} is clean ({s.Engine}): burning it");
                    if (!library.BurnUpload(s.Peer, s.File, s.Folder, out string refusal))
                    {
                        TryDelete(s.Folder);
                        library.Report(s.Peer, refusal);
                    }
                    break;
                case ScanVerdict.Infected:
                    GD.PushWarning($"[cd] upload from {s.Peer} flagged by {s.Engine}: deleted");
                    TryDelete(s.Folder);
                    library?.Report(s.Peer, "The virus scanner flagged that file: it was deleted.");
                    break;
                default:
                    TryDelete(s.Folder);
                    library?.Report(s.Peer, s.Verdict == ScanVerdict.Unavailable
                        ? "This server cannot check files for viruses, so uploads are off."
                        : "The file could not be checked; it was deleted. Try again, or another file.");
                    break;
            }
        }
        if (_incoming.Count == 0) return;
        double now = Now;
        foreach (var (peer, up) in _incoming)
            if (now - up.LastAt > StallSeconds) { Drop(peer, "The upload stalled and was dropped."); return; }
    }

    private void Drop(long peer, string why)
    {
        Abandon(peer);
        if (NetLink.Online(this)) RpcId(peer, MethodName.Refused, why);
    }

    private void Abandon(long peer)
    {
        if (!_incoming.Remove(peer, out var up)) return;
        try { up.Stream.Dispose(); } catch { }
        TryDelete(up.Folder);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) { GD.PushWarning($"[cd] could not delete {folder}: {e.Message}"); }
    }
}
