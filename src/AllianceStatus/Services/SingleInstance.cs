using System;
using System.Threading;

namespace AllianceStatus.Services;

/// <summary>Keeps one instance per user session; a second launch just opens the flyout.</summary>
internal static class SingleInstance
{
    const string Id = "AllianceStatus-3f6c2e1a";
    static Mutex? _mutex;
    static EventWaitHandle? _signal;
    static RegisteredWaitHandle? _wait;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out bool created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    public static void SignalExisting()
    {
        try
        {
            Native.AllowSetForegroundWindow(-1); // let the running instance take focus
            using var ev = EventWaitHandle.OpenExisting($@"Local\{Id}-show");
            ev.Set();
        }
        catch
        {
            // The other instance is starting or shutting down; nothing to do.
        }
    }

    public static void Listen(Action onSignal)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Id}-show");
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public static void Release()
    {
        try
        {
            _wait?.Unregister(null);
            _signal?.Dispose();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
        }
    }
}
