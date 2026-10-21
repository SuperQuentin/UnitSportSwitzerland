using System;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// The few Android system calls the client needs (#63), through Godot's <c>AndroidRuntime</c>
/// singleton and the Java objects it hands back. Every call is a no-op off Android and never
/// throws: a missing API costs the feature, not the game. Rule note: docs/notes/core/platform.md.
/// </summary>
public static class AndroidBridge
{
    public static readonly bool Available = OS.GetName() == "Android" && Engine.HasSingleton("AndroidRuntime");

    private static GodotObject? _multicastLock;

    /// <summary>
    /// Android drops incoming multicast (mDNS LAN discovery) unless a Wi-Fi MulticastLock is held.
    /// Idempotent; <see cref="ReleaseMulticast"/> lets it go (it costs battery).
    /// </summary>
    public static void AcquireMulticast()
    {
        if (!Available || _multicastLock != null) return;
        _multicastLock = Try("multicast lock", () =>
        {
            var wifi = SystemService("wifi");
            var lk = wifi?.Call("createMulticastLock", "unitsport-discovery").AsGodotObject();
            lk?.Call("setReferenceCounted", false);
            lk?.Call("acquire");
            return lk;
        });
    }

    public static void ReleaseMulticast()
    {
        if (_multicastLock == null) return;
        var lk = _multicastLock;
        _multicastLock = null;
        Try("multicast unlock", () => lk.Call("release"));
    }

    /// <summary>
    /// The network the phone is on, for the data settings: metered (cellular, a hotspot) and data
    /// saver on. Null off Android or when the system will not say.
    /// </summary>
    public static (bool Metered, bool DataSaver)? NetworkState()
    {
        if (!Available) return null;
        return Try<(bool, bool)?>("network state", () =>
        {
            var cm = SystemService("connectivity");
            if (cm == null) return null;
            bool metered = cm.Call("isActiveNetworkMetered").AsBool();
            // ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED = 3: the user turned data saver on
            bool saver = cm.Call("getRestrictBackgroundStatus").AsInt32() == 3;
            return (metered, saver);
        });
    }

    /// <summary>
    /// The file another app handed the game (#1029): "Open with" (<c>ACTION_VIEW</c>, the intent's data)
    /// or "Share to" (<c>ACTION_SEND</c>, its stream extra), as a <c>content://</c> or <c>file://</c> URI
    /// that <c>Godot.FileAccess</c> reads; null when there is none. Each intent answers once: its action
    /// is rewritten, so a resume without a new intent does not open the file again.
    /// </summary>
    public static string? OpenedFile()
    {
        if (!Available) return null;
        return Try<string?>("opened file", () =>
        {
            var activity = Engine.GetSingleton("AndroidRuntime").Call("getActivity").AsGodotObject();
            var intent = activity?.Call("getIntent").AsGodotObject();
            if (intent == null) return null;
            string action = intent.Call("getAction").AsString();
            GodotObject? uri = action switch
            {
                "android.intent.action.VIEW" => intent.Call("getData").AsGodotObject(),
                "android.intent.action.SEND" => intent.Call("getParcelableExtra", "android.intent.extra.STREAM").AsGodotObject(),
                _ => null,
            };
            if (uri == null) return null;
            intent.Call("setAction", "ch.unitsport.switzerland.OPENED");
            string s = uri.Call("toString").AsString();
            return s.Length > 0 ? s : null;
        });
    }

    private static GodotObject? SystemService(string name)
    {
        var context = Engine.GetSingleton("AndroidRuntime").Call("getApplicationContext").AsGodotObject();
        return context?.Call("getSystemService", name).AsGodotObject();
    }

    private static T? Try<T>(string what, Func<T?> call)
    {
        try { return call(); }
        catch (Exception e)
        {
            GD.PushWarning($"[android] {what}: {e.Message}");
            return default;
        }
    }

    private static void Try(string what, Func<Variant> call) => Try<object?>(what, () => { call(); return null; });
}
