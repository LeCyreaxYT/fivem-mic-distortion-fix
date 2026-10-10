namespace ApolloMicBridge;

// Kernel events sleep until another launch requests a window or shutdown; no polling loop.
internal sealed class SingleInstance : IDisposable
{
    internal const string MutexName = @"Local\SkySystems.ApolloMicBridge";
    private const string ShowEventName = @"Local\SkySystems.ApolloMicBridge.Show";
    private const string ExitEventName = @"Local\SkySystems.ApolloMicBridge.Exit";
    private readonly EventWaitHandle show = new(false, EventResetMode.AutoReset, ShowEventName);
    private readonly EventWaitHandle exit = new(false, EventResetMode.AutoReset, ExitEventName);
    private readonly Mutex mutex = new(false, MutexName);
    private readonly List<RegisteredWaitHandle> registrations = [];
    public bool IsFirst { get; }

    public SingleInstance()
    {
        try { IsFirst = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsFirst = true; }
    }

    public void Listen(MainForm form)
    {
        _ = form.Handle;
        registrations.Add(ThreadPool.RegisterWaitForSingleObject(show, (_, _) => form.PostToUi(form.OpenWindow),
            null, Timeout.Infinite, false));
        registrations.Add(ThreadPool.RegisterWaitForSingleObject(exit, (_, _) => form.PostToUi(form.RequestExit),
            null, Timeout.Infinite, false));
    }

    public void HandleAnotherLaunch(bool background)
    {
        if (!background)
            show.Set();
    }

    public static bool RequestShutdown()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var signal))
            return true;
        using (signal)
        {
            signal.Set();
            if (!Mutex.TryOpenExisting(MutexName, out var running))
                return true;
            using (running)
            {
                try
                {
                    if (!running.WaitOne(TimeSpan.FromSeconds(10)))
                        return false;
                }
                catch (AbandonedMutexException) { }
                running.ReleaseMutex();
                return true;
            }
        }
    }

    public void Dispose()
    {
        foreach (var registration in registrations)
            registration.Unregister(null);
        if (IsFirst)
            mutex.ReleaseMutex();
        mutex.Dispose();
        show.Dispose();
        exit.Dispose();
    }
}
