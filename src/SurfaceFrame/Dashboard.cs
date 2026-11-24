using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SurfaceFrame;

internal sealed class Dashboard(Grid host, LocalStorage storage) : IDisposable
{
    private const string ActivityMessage = "surface-frame-activity";
    private const string ActivityScript = """
        (() => {
          let last = 0;
          const activity = (event) => {
            if (!event.isTrusted || (event.type === 'pointermove' && !event.buttons)) return;
            const now = Date.now();
            if (now - last < 200) return;
            last = now;
            window.chrome?.webview?.postMessage('surface-frame-activity');
          };
          for (const name of ['pointerdown','pointermove','touchstart','touchmove','click','wheel','keydown','input','scroll']) {
            window.addEventListener(name, activity, { capture: true, passive: true });
          }
        })();
        """;

    private WebView2? view;
    private CoreWebView2Environment? environment;
    private Uri? target;
    private bool disposed;
    private bool active;
    private bool busy;
    private bool ready;
    private bool pendingNavigation;
    private DateTime retryAt = DateTime.MaxValue;

    internal event Action? Changed;
    internal event Action? Activity;
    internal string Status { get; private set; } = "Inserisci l’URL della dashboard nelle impostazioni.";
    internal bool Ready => ready;
    internal bool CanRetry => active && target is not null && !busy;

    internal void SetActive(bool visible)
    {
        active = visible;
        Changed?.Invoke();
    }

    internal void Configure(Uri? address)
    {
        if (Equals(target, address))
            return;
        target = address;
        ready = false;
        pendingNavigation = address is not null;
        if (address is null)
        {
            if (view is not null && !busy)
                ResetView();
            SetStatus("Inserisci l’URL della dashboard nelle impostazioni.");
            return;
        }
        retryAt = DateTime.MinValue;
        SetStatus("Indirizzo salvato. Apri Home Assistant per verificare la connessione.");
    }

    internal void TimerTick()
    {
        if (CanRetry && retryAt <= DateTime.UtcNow)
            _ = NavigateAsync();
    }

    internal Task RetryAsync() => NavigateAsync();

    private async Task NavigateAsync()
    {
        if (disposed || !CanRetry)
            return;
        busy = true;
        ready = false;
        pendingNavigation = false;
        retryAt = DateTime.MaxValue;
        SetStatus("Apertura della dashboard…");
        try
        {
            if (view is null)
            {
                environment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(storage.Root, "home-assistant"));
                if (disposed || target is null)
                    return;
                if (!active)
                {
                    pendingNavigation = true;
                    return;
                }
                var control = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(17, 24, 32) };
                view = control;
                host.Children.Add(control);
                await control.EnsureCoreWebView2Async(environment);
                if (disposed || target is null)
                    return;
                var core = control.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.NavigationStarting += NavigationStarting;
                core.NavigationCompleted += NavigationCompleted;
                core.NewWindowRequested += NewWindowRequested;
                core.WebMessageReceived += MessageReceived;
                core.FrameCreated += (_, args) => args.Frame.WebMessageReceived += MessageReceived;
                core.ProcessFailed += ProcessFailed;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ActivityScript);
            }

            if (!disposed && target is not null)
            {
                pendingNavigation = !active;
                if (active)
                    view!.CoreWebView2.Navigate(target.AbsoluteUri);
            }
        }
        catch (WebView2RuntimeNotFoundException error)
        {
            ResetView();
            SetStatus("Microsoft Edge WebView2 Runtime mancante. Installa la versione Evergreen e riprova.");
            storage.Log("WebView2 Runtime mancante", error);
        }
        catch (Exception error)
        {
            if (disposed)
                return;
            ResetView();
            retryAt = DateTime.UtcNow.AddSeconds(20);
            SetStatus("Impossibile aprire la finestra web. Riprovo automaticamente.");
            storage.Log("Inizializzazione WebView2", error);
        }
        finally
        {
            busy = false;
            if (pendingNavigation)
                retryAt = DateTime.MinValue;
            if (!disposed && target is null && view is not null)
                ResetView();
            Changed?.Invoke();
        }
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsDashboardOrigin(args.Uri))
            args.Cancel = true;
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (target is null || pendingNavigation)
            return;
        if (!args.IsSuccess && args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            return;
        ready = args.IsSuccess && args.HttpStatusCode < 400;
        if (ready)
        {
            retryAt = DateTime.MaxValue;
            SetStatus("Dashboard caricata");
            return;
        }

        retryAt = DateTime.UtcNow.AddSeconds(15);
        SetStatus("Home Assistant non raggiungibile. Verifica l’indirizzo e che la VM sia avviata. Riprovo automaticamente.");
        storage.Log($"Caricamento Home Assistant ({args.WebErrorStatus}, HTTP {args.HttpStatusCode})");
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (IsDashboardOrigin(args.Uri))
            view?.CoreWebView2.Navigate(args.Uri);
    }

    private void MessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (IsDashboardOrigin(args.Source) && args.WebMessageAsJson == JsonSerializer.Serialize(ActivityMessage))
            Activity?.Invoke();
    }

    private void ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        storage.Log($"Processo WebView2 ({args.ProcessFailedKind})");
        if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
            return;

        var failedView = view;
        host.Dispatcher.BeginInvoke(() =>
        {
            if (disposed || failedView is null || failedView != view)
                return;
            ResetView();
            retryAt = DateTime.UtcNow.AddSeconds(5);
            SetStatus("Connessione alla dashboard interrotta. Riprovo automaticamente.");
        });
    }

    private bool IsDashboardOrigin(string value) => target is not null &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == target.Scheme &&
        uri.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == target.Port &&
        uri.UserInfo.Length == 0;

    private void SetStatus(string message)
    {
        Status = message;
        Changed?.Invoke();
    }

    private void ResetView()
    {
        ready = false;
        var previous = view;
        view = null;
        environment = null;
        if (previous is null)
            return;
        try
        {
            host.Children.Remove(previous);
            previous.Dispose();
        }
        catch (Exception error)
        {
            storage.Log("Chiusura WebView2", error);
        }
    }

    public void Dispose()
    {
        disposed = true;
        ResetView();
    }
}
