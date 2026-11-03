using System.Runtime.InteropServices;
using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The PC's own sound for a screen share (#1298 phase 3), on Windows: WASAPI <b>process loopback</b> (Windows 10 build
/// 20348 and later, Windows 11), the API OBS's and Discord's "share audio" use. It records everything the PC plays
/// except this game's process tree, so the hall's own sound (the share itself, coming back from the server) never
/// echoes into it. COM interop only, no native plugin: <c>ActivateAudioInterfaceAsync</c> on the virtual
/// <c>VAD\Process_Loopback</c> device, then a shared-mode <c>IAudioClient</c> in 48 kHz stereo 16-bit, read on its own
/// MTA thread on each buffer event. Hands out mono at <see cref="CinemaVideo.RelaySoundRate"/> (each two frames
/// averaged: a mild low-pass before halving the rate).
/// </summary>
public sealed class PcSoundLoopback : IDisposable
{
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    /// <summary>Why it is not recording, once it gave up ("" while it records or starts).</summary>
    public string Problem { get; private set; } = "";

    private readonly Action<short[]> _samples;
    private volatile bool _stop;
    private readonly Thread _thread;

    /// <summary>Starts recording; <paramref name="samples"/> gets blocks of mono samples, on the recording thread.</summary>
    public PcSoundLoopback(Action<short[]> samples)
    {
        _samples = samples;
        _thread = new Thread(Run) { IsBackground = true, Name = "cinema-pc-sound" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }

    private void Run()
    {
        IAudioClient? client = null;
        try
        {
            client = Activate();
            var format = new WaveFormatEx { Tag = 1, Channels = 2, Rate = 48000, BytesPerSecond = 48000 * 4, BlockAlign = 4, Bits = 16 };
            IntPtr fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, fmt, false);
                // loopback, signalled per buffer, 200 ms of room
                Check(client.Initialize(0, Loopback | EventCallback | AutoConvertPcm | SrcDefaultQuality, 2_000_000, 0, fmt, IntPtr.Zero), "Initialize");
            }
            finally { Marshal.FreeHGlobal(fmt); }
            using var ready = new AutoResetEvent(false);
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out object service), "GetService");
            var capture = (IAudioCaptureClient)service;
            Check(client.Start(), "Start");
            GD.Print("[cinema] PC sound: recording every app but the game");
            while (!_stop)
            {
                ready.WaitOne(100);
                while (!_stop && capture.GetNextPacketSize(out uint packet) == 0 && packet > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _) != 0) break;
                    var mono = new short[frames / 2];
                    if ((flags & SilentFlag) == 0 && frames >= 2)
                    {
                        var raw = new short[frames * 2];
                        Marshal.Copy(data, raw, 0, raw.Length);
                        for (int i = 0; i < mono.Length; i++)
                            mono[i] = (short)((raw[i * 4] + raw[i * 4 + 1] + raw[i * 4 + 2] + raw[i * 4 + 3]) / 4);
                    }
                    capture.ReleaseBuffer(frames);
                    if (mono.Length > 0) _samples(mono);
                }
            }
            client.Stop();
        }
        catch (Exception e)
        {
            Problem = $"The PC's sound cannot be recorded: {e.Message}";
            GD.Print($"[cinema] {Problem}");
        }
        finally
        {
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    /// <summary>The process-loopback audio client excluding this process tree, activated asynchronously and waited for.</summary>
    private static IAudioClient Activate()
    {
        var p = new ActivationParams { Type = 1, TargetProcessId = (uint)System.Environment.ProcessId, Mode = 1 };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new PropVariantBlob { Vt = 65, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = pParams }, pVariant, false);
            var handler = new Completion();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pVariant, handler, out var op);
            if (!handler.Done.WaitOne(5000)) throw new TimeoutException("the audio system did not answer");
            op.GetActivateResult(out int hr, out object iface);
            Check(hr, "Activate");
            return (IAudioClient)iface;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pParams);
        }
    }

    private const int Loopback = 0x00020000, EventCallback = 0x00040000, SrcDefaultQuality = 0x08000000;
    private const int AutoConvertPcm = unchecked((int)0x80000000);
    private const uint SilentFlag = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int Type;
        public uint TargetProcessId;
        public int Mode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>Marks the handler free-threaded: the audio system calls it back from its own thread.</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation) => Done.Set();
    }
}
