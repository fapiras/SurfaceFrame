using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SurfaceFrame;

public partial class MainWindow : Window
{
    private enum Mode { Frame, Home, Desktop }

    private readonly LocalStorage storage;
    internal UpdateService Updates { get; }
    private readonly MicrosoftSignIn signIn;
    private readonly RcloneOneDrive rclone;
    private readonly PhotoPlayback playback;
    private readonly Dashboard dashboard;
    private readonly DispatcherTimer timer;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem pauseMenu;
    private readonly HttpClient graphClient = new(new HttpClientHandler
        { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private readonly HttpClient mediaClient = new(new HttpClientHandler
        { AllowAutoRedirect = true, UseCookies = false, MaxAutomaticRedirections = 5 }) { Timeout = TimeSpan.FromSeconds(25) };
    private FrameSettings settings;
    private CancellationTokenSource work = new();
    private Mode mode = Mode.Frame;
    private SettingsWindow? settingsWindow;
    private bool closed;
    private bool paused;
    private bool refreshing;
    private bool preparing;
    private bool fading;
    private string? photoError;
    private long latestInteraction = Stopwatch.GetTimestamp();
    private DateTime catalogDue = DateTime.MinValue;
    private DateTime slideDue = DateTime.MinValue;
    private DateTime preloadDue = DateTime.MinValue;
    private PhotoItem? current;
    private DisplayPhoto? next;

    internal MainWindow(LocalStorage storage)
    {
        InitializeComponent();
        this.storage = storage;
        Updates = new UpdateService(storage);
        settings = storage.Load();
        signIn = new MicrosoftSignIn(storage.Root);
        rclone = new RcloneOneDrive(storage.Root);
        playback = new PhotoPlayback(storage, signIn, new OneDrive(graphClient, mediaClient), rclone);
        dashboard = new Dashboard(BrowserHost, storage);
        dashboard.Changed += UpdateHomeStatus;
        dashboard.Activity += RecordInteraction;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Torna alla cornice", null, (_, _) => ShowFrame());
        menu.Items.Add("Apri Home Assistant", null, (_, _) => ShowHome());
        menu.Items.Add("Impostazioni", null, (_, _) => ShowSettings());
        pauseMenu = new Forms.ToolStripMenuItem("Sospendi presentazione", null, (_, _) => SetPaused(!paused));
        menu.Items.Add(pauseMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Chiudi Surface Frame", null, (_, _) => Close());
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Surface Frame",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowFrame();

        dashboard.Configure(settings.DashboardUri);
        ApplyPhotoFit();
        UpdateHomeStatus();
        UpdateFrameStatus();
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += TimerTick;
        timer.Start();
        Loaded += (_, _) => { if (mode != Mode.Desktop) PositionOnPrimaryScreen(); };
        StateChanged += (_, _) =>
        {
            if (!closed && WindowState == WindowState.Minimized && mode != Mode.Desktop)
                LeaveForDesktop(showShell: false);
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.F2)
            {
                args.Handled = true;
                ShowSettings();
            }
            else if (args.Key == Key.Escape && mode == Mode.Home)
            {
                args.Handled = true;
                ShowFrame();
            }
            else if (args.Key == Key.Enter && mode == Mode.Frame &&
                Keyboard.FocusedElement is not System.Windows.Controls.Button)
            {
                args.Handled = true;
                ShowHome();
            }
        };
        HomeLayer.AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => RecordInteraction()), true);
        HomeLayer.AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, _) => RecordInteraction()), true);
    }

    internal bool StartInFrame => settings.StartInFrame;
    internal bool Paused => paused;
    internal string OneDriveStatus => playback.Status;
    internal string DashboardStatus => dashboard.Status;
    internal string? PhotoError => photoError;
    internal string? StartupError => storage.StartupError;
    internal string LogPath => storage.LogPath;
    internal PhotoCompatibility Compatibility(Guid sourceId, double ratio) => playback.Compatibility(sourceId, ratio);
    internal string? SourceStatus(Guid id) => playback.SourceStatus(id);

    internal void StartOnDesktop()
    {
        mode = Mode.Desktop;
        PhotoLayer.Visibility = Visibility.Hidden;
    }

    private void ShowFrame()
    {
        if (closed || settingsWindow is not null)
            return;
        mode = Mode.Frame;
        HomeLayer.Visibility = Visibility.Hidden;
        PhotoLayer.Visibility = Visibility.Visible;
        slideDue = DateTime.UtcNow.AddSeconds(settings.SlideSeconds);
        preloadDue = DateTime.MinValue;
        ShowFullscreen();
    }

    private void ShowHome()
    {
        if (closed || settingsWindow is not null)
            return;
        mode = Mode.Home;
        PhotoLayer.Visibility = Visibility.Hidden;
        HomeLayer.Visibility = Visibility.Visible;
        RecordInteraction();
        ShowFullscreen();
        dashboard.TimerTick();
    }

    private void ShowFullscreen()
    {
        bool restore = !IsVisible || WindowState == WindowState.Minimized;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        if (!IsVisible)
            Show();
        if (restore)
            PositionOnPrimaryScreen();
        Topmost = true;
        Activate();
    }

    private void PositionOnPrimaryScreen()
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null)
            return;
        var bounds = screen.Bounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
        Topmost = settingsWindow is null;
    }

    private void LeaveForDesktop(bool showShell)
    {
        mode = Mode.Desktop;
        Topmost = false;
        HomeLayer.Visibility = Visibility.Hidden;
        PhotoLayer.Visibility = Visibility.Hidden;
        if (IsVisible)
        {
            WindowState = WindowState.Minimized;
            Hide();
        }

        if (!showShell)
            return;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try { shell.ToggleDesktop(); }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch (Exception error)
        {
            // The frame is already minimized and other Windows apps are accessible.
            storage.Log("Mostra desktop Windows", error);
        }
    }

    private void TimerTick(object? sender, EventArgs args)
    {
        if (closed || mode == Mode.Desktop && settingsWindow is null)
            return;
        DateTime now = DateTime.UtcNow;
        if (!refreshing && now >= catalogDue)
            _ = RefreshCatalogAsync();

        dashboard.TimerTick();
        if (mode == Mode.Home && settingsWindow is null &&
            Stopwatch.GetElapsedTime(latestInteraction) >= TimeSpan.FromSeconds(settings.InactivitySeconds))
        {
            ShowFrame();
            return;
        }

        if (mode != Mode.Frame || paused && current is not null || fading || settingsWindow is not null)
            return;
        if (next is not null && (current is null || slideDue <= now))
            ShowNext(now);
        if (!fading && next is null && !preparing && preloadDue <= now &&
            playback.HasCandidates(settings.MinimumAspectRatio))
            _ = PreloadAsync();
    }

    private async Task RefreshCatalogAsync()
    {
        refreshing = true;
        var snapshot = settings;
        var token = work.Token;
        try
        {
            if (current is null)
                FrameStatusText.Text = "Aggiornamento del catalogo OneDrive…";
            await playback.RefreshAsync(snapshot, token);
            if (token.IsCancellationRequested || closed)
                return;
            if (next is { } staged && !playback.Contains(staged.Item))
                next = null;
            if (current is not null && !playback.Contains(current))
                ClearPhotos();
            preloadDue = DateTime.MinValue;
            photoError = null;
            UpdateFrameStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A changed setting or shutdown superseded this refresh.
        }
        catch (Exception error)
        {
            storage.Log("Aggiornamento catalogo", error);
            photoError = PhotoPlayback.Describe(error);
            UpdateFrameStatus();
        }
        finally
        {
            refreshing = false;
            if (!token.IsCancellationRequested)
            {
                var normal = DateTime.UtcNow.AddMinutes(snapshot.CatalogMinutes);
                catalogDue = playback.RetryAt?.UtcDateTime ??
                    (playback.RefreshFailed ? DateTime.UtcNow.AddSeconds(45) : normal);
            }
        }
    }

    private async Task PreloadAsync()
    {
        preparing = true;
        var snapshot = settings;
        var token = work.Token;
        try
        {
            var screen = Forms.Screen.PrimaryScreen;
            int width = screen?.Bounds.Width ?? 2736;
            int height = screen?.Bounds.Height ?? 1824;
            var result = await playback.PrepareNextAsync(snapshot, width, height, current, token);
            if (token.IsCancellationRequested || closed)
                return;
            next = result;
            photoError = null;
            preloadDue = DateTime.UtcNow.AddSeconds(result is null ? 5 : 0);
            UpdateFrameStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A changed setting or shutdown superseded this preload.
        }
        catch (Exception error)
        {
            if (token.IsCancellationRequested || closed)
                return;
            storage.Log("Precaricamento foto", error);
            photoError = PhotoPlayback.Describe(error);
            preloadDue = (error as GraphRequestException)?.RetryAt?.UtcDateTime ?? DateTime.UtcNow.AddSeconds(15);
            UpdateFrameStatus();
        }
        finally
        {
            preparing = false;
        }
    }

    private void ShowNext(DateTime now)
    {
        var photo = next!;
        next = null;
        if (!playback.Contains(photo.Item))
            return;
        BackPhoto.Source = FrontPhoto.Source;
        FrontPhoto.Source = photo.Bitmap;
        current = photo.Item;
        slideDue = now.AddSeconds(settings.SlideSeconds);
        EmptyFrame.Visibility = Visibility.Collapsed;
        if (BackPhoto.Source is null || settings.FadeMilliseconds == 0)
        {
            BackPhoto.Source = null;
            return;
        }

        fading = true;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(settings.FadeMilliseconds))
            { FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) =>
        {
            BackPhoto.Source = null;
            fading = false;
        };
        FrontPhoto.BeginAnimation(OpacityProperty, animation);
    }

    private void ClearPhotos()
    {
        current = null;
        next = null;
        FrontPhoto.BeginAnimation(OpacityProperty, null);
        FrontPhoto.Source = null;
        BackPhoto.Source = null;
        fading = false;
        UpdateFrameStatus();
    }

    internal void SetPaused(bool pause)
    {
        paused = pause;
        pauseMenu.Text = paused ? "Riprendi presentazione" : "Sospendi presentazione";
        slideDue = DateTime.UtcNow.AddSeconds(settings.SlideSeconds);
    }

    internal void SaveSettings(FrameSettings updated)
    {
        var previous = settings;
        storage.Save(updated);
        try
        {
            if (updated.AutoStart != previous.AutoStart)
                LocalStorage.ConfigureAutoStart(updated.AutoStart);
        }
        catch
        {
            storage.Save(previous);
            throw;
        }

        var before = previous.Sources.Select(s => (s.Id, s.Address, s.Enabled, s.IncludeSubfolders));
        var after = updated.Sources.Select(s => (s.Id, s.Address, s.Enabled, s.IncludeSubfolders));
        bool sourceChanged = !before.SequenceEqual(after) || previous.UsesRclone != updated.UsesRclone ||
            !string.Equals(previous.MicrosoftClientId, updated.MicrosoftClientId, StringComparison.OrdinalIgnoreCase) ||
            previous.AllowSharedLinkScope != updated.AllowSharedLinkScope;
        bool ratioChanged = previous.MinimumAspectRatio != updated.MinimumAspectRatio;

        CancelWork();
        settings = updated;
        if (sourceChanged)
            playback.Reset();
        if (sourceChanged || ratioChanged)
            ClearPhotos();
        dashboard.Configure(updated.DashboardUri);
        ApplyPhotoFit();
        slideDue = DateTime.UtcNow.AddSeconds(settings.SlideSeconds);
        RefreshSoon();
    }

    internal Task ConnectRcloneAsync(CancellationToken token) => ChangeRcloneAsync(rclone.ConnectAsync, token);

    internal Task DisconnectRcloneAsync(CancellationToken token) => ChangeRcloneAsync(rclone.DisconnectAsync, token);

    private async Task ChangeRcloneAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        await action(token);
        if (closed || !settings.UsesRclone)
            return;
        CancelWork();
        playback.Reset();
        ClearPhotos();
        RefreshSoon();
    }

    internal async Task SignInAsync(CancellationToken token)
    {
        await signIn.ConnectAsync(settings, token);
        RefreshSoon();
    }

    internal async Task DisconnectAsync(CancellationToken token)
    {
        CancelWork();
        catalogDue = DateTime.MaxValue;
        try
        {
            await signIn.DisconnectAsync(settings.MicrosoftClientId, token);
            playback.Reset();
            ClearPhotos();
        }
        finally
        {
            RefreshSoon();
        }
    }

    internal void RefreshSoon() => catalogDue = DateTime.MinValue;

    private void CancelWork()
    {
        work.Cancel();
        work.Dispose();
        work = new CancellationTokenSource();
    }

    private void ApplyPhotoFit()
    {
        FrontPhoto.Stretch = BackPhoto.Stretch = settings.FillScreen ? Stretch.UniformToFill : Stretch.Uniform;
    }

    private void RecordInteraction()
    {
        if (mode == Mode.Home)
            latestInteraction = Stopwatch.GetTimestamp();
    }

    private void UpdateFrameStatus()
    {
        EmptyFrame.Visibility = current is null ? Visibility.Visible : Visibility.Collapsed;
        FrameStatusText.Text = storage.StartupError ?? photoError ??
            (playback.CatalogCount == 0 ? playback.Status :
                playback.HasCandidates(settings.MinimumAspectRatio)
                    ? "Cerco una foto orizzontale tra le sorgenti abilitate…"
                    : "Nessuna foto orizzontale rispetta il rapporto minimo impostato.");
    }

    private void UpdateHomeStatus()
    {
        HomeMessage.Visibility = dashboard.Ready ? Visibility.Collapsed : Visibility.Visible;
        HomeStatusText.Text = dashboard.Status;
        RetryButton.Visibility = settings.DashboardUri is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = dashboard.CanRetry;
    }

    internal void ShowSettings()
    {
        if (closed)
            return;
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }

        var dialog = new SettingsWindow(this, settings);
        if (IsVisible)
        {
            Topmost = false;
            dialog.Owner = this;
        }
        settingsWindow = dialog;
        try { dialog.ShowDialog(); }
        finally
        {
            settingsWindow = null;
            Topmost = mode != Mode.Desktop;
            RecordInteraction();
        }
    }

    private void Photo_TouchDown(object sender, TouchEventArgs args)
    {
        if (current is null)
            return;
        args.Handled = true;
        ShowHome();
    }

    private void Photo_MouseUp(object sender, MouseButtonEventArgs args)
    {
        if (current is not null || args.OriginalSource is not System.Windows.Controls.Button)
            ShowHome();
    }

    private void Frame_Click(object sender, RoutedEventArgs args) => ShowFrame();
    private void Minimize_Click(object sender, RoutedEventArgs args) => LeaveForDesktop(showShell: false);
    private void Exit_Click(object sender, RoutedEventArgs args) => Close();
    private void Windows_Click(object sender, RoutedEventArgs args) => LeaveForDesktop(showShell: true);
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowSettings();
    private async void RetryHome_Click(object sender, RoutedEventArgs args) => await dashboard.RetryAsync();

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        timer.Stop();
        work.Cancel();
        work.Dispose();
        dashboard.Dispose();
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        graphClient.Dispose();
        mediaClient.Dispose();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
