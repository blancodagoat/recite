namespace Recite;

/// <summary>
/// Two instances would both try to own the same global hotkeys and one would silently
/// lose, breaking capture with no visible cause. The second launch instead pokes the
/// first into showing its settings window and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Recite.SingleInstance";
    private const string SignalName = @"Local\Recite.ShowSettings";
    private const string QuitName = @"Local\Recite.Quit";

    private readonly Mutex mutex;
    private EventWaitHandle? signal;
    private EventWaitHandle? quit;
    private Thread? watcher;
    private volatile bool stopping;

    private SingleInstance(Mutex mutex, bool owned)
    {
        this.mutex = mutex;
        IsFirstInstance = owned;
    }

    public bool IsFirstInstance { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool created);
        return new SingleInstance(mutex, created);
    }

    /// <summary>Wakes the running instance. Best-effort — a missing handle just means no-op.</summary>
    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch
        {
            // Nothing to hand off to.
        }
    }

    /// <summary>
    /// Starts listening for later launches. The signal is forwarded to the UI thread by
    /// posting to the hotkey window rather than by marshalling through a Control.
    /// </summary>
    public void ListenForSignals(IntPtr targetWindow)
    {
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        // A named quit event so an outside manager (the Triumvirate suite) can ask for
        // a clean exit instead of killing the process.
        quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);

        watcher = new Thread(() =>
        {
            var handles = new WaitHandle[] { signal, quit };
            while (!stopping)
            {
                int which = WaitHandle.WaitAny(handles);
                if (stopping)
                {
                    return;
                }

                Native.PostMessageW(
                    targetWindow,
                    which == 1 ? Native.WM_APP_QUIT : Native.WM_APP_SHOW_SETTINGS,
                    IntPtr.Zero, IntPtr.Zero);
            }
        })
        {
            IsBackground = true,
            Name = "Recite single-instance watcher",
        };

        watcher.Start();
    }

    public void Dispose()
    {
        stopping = true;

        try
        {
            signal?.Set();
        }
        catch
        {
            // Already torn down.
        }

        signal?.Dispose();
        quit?.Dispose();

        if (IsFirstInstance)
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
                // Not held; nothing to release.
            }
        }

        mutex.Dispose();
    }
}
