using System.Windows;
using System.Windows.Threading;

namespace SurfaceFrame;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    private EventWaitHandle? restoreSignal;
    private RegisteredWaitHandle? restoreWait;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var storage = new LocalStorage();
        if (e.Args is ["--apply-update", var job, var versionText, var pidText, var ticksText])
        {
            try
            {
                if (!UpdateService.TryVersion(versionText, out var version) ||
                    !int.TryParse(pidText, out int pid) || !long.TryParse(ticksText, out long ticks))
                    throw new InvalidDataException("Parametri di aggiornamento non validi.");
                await new UpdateService(storage).ApplyAsync(job, version, pid, ticks);
            }
            catch (Exception error)
            {
                storage.Log("Errore nel programma di aggiornamento", error);
            }
            finally { Shutdown(); }
            return;
        }
        restoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SurfaceFrame-Restore-2CE15A63");
        instance = new Mutex(true, @"Local\SurfaceFrame-2CE15A63", out ownsMutex);
        if (!ownsMutex)
        {
            if (e.Args is not ["--update-probe", _])
                restoreSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => storage.Log("Errore imprevisto", args.Exception);
        var window = new MainWindow(storage);
        MainWindow = window;
        restoreWait = ThreadPool.RegisterWaitForSingleObject(restoreSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(window.RestoreFromLaunch)),
            null, Timeout.Infinite, executeOnlyOnce: false);
        if (e.Args.Contains("--setup", StringComparer.Ordinal))
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(new Action(window.ShowSettings));
        if (e.Args is ["--update-probe", var probe])
            window.Loaded += (_, _) => window.Dispatcher.InvokeAsync(() =>
                new UpdateService(storage).SignalStartup(probe), DispatcherPriority.ApplicationIdle);
        if (e.Args.Contains("--autostart", StringComparer.Ordinal) && !window.StartInFrame)
            window.StartOnDesktop();
        else
            window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        restoreWait?.Unregister(null);
        restoreSignal?.Dispose();
        if (ownsMutex)
            instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
