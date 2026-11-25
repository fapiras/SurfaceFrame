using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly PhotoCache photoCache;
    private readonly HashSet<string> cachedPhotosSeen = new(StringComparer.Ordinal);
    private DisplayPhoto? previousPhoto;
    private DisplayPhoto? forwardPhoto;
    private bool showNextRequested;
    private bool showingCachedPhoto;
    private bool useCachedPhotos;
    private bool nightWasActive;
    private bool nightOverridden;
    private DateTime commandsUntil;
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
    private bool restoringWindow;
    private readonly DisplayBrightness brightness = new();
    private bool adjustingBrightness;
    private bool brightnessRequested = true;
    private DateTime nextBrightnessReading;
    private int? brightnessTarget;
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
        rclone = new RcloneOneDrive(storage.Root, graphClient);
        photoCache = new PhotoCache(storage);
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
        CommandMenu.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((_, _) => CloseCommands()), true);
        CommandPopup.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(Math.Max(0, target.Width - popup.Width - 16),
                Math.Max(0, target.Height - popup.Height - 16)), PopupPrimaryAxis.None)];
        SizeChanged += (_, _) => PositionCommands();
        LocationChanged += (_, _) => PositionCommands();
        Loaded += (_, _) =>
        {
            if (mode != Mode.Desktop)
                PositionOnPrimaryScreen();
            UpdateCommands();
        };
        StateChanged += (_, _) =>
        {
            if (!closed && !restoringWindow && WindowState == WindowState.Minimized && mode != Mode.Desktop)
                LeaveForDesktop(showShell: false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                CommandPopup.IsOpen = false;
        };
        PreviewKeyDown += (_, args) =>
        {
            if (NightBlack.Visibility == Visibility.Visible)
            {
                Wake_Click(this, args);
                args.Handled = true;
            }
            else if (args.Key == Key.F2)
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
    internal string BrightnessStatus { get; private set; } = "Controllo luminosità in attesa.";

    internal void RestoreFromLaunch()
    {
        if (closed)
            return;
        if (settingsWindow is { } dialog)
        {
            if (dialog.WindowState == WindowState.Minimized)
                dialog.WindowState = WindowState.Normal;
            dialog.Activate();
        }
        else if (mode == Mode.Home)
            ShowHome();
        else
            ShowFrame();
    }
    internal string OneDriveStatus => playback.LastRefresh is { } last
        ? $"{playback.Status} · Ultimo aggiornamento {last.LocalDateTime:g}"
        : playback.Status;
    internal Task<IReadOnlyList<string>> BrowseFoldersAsync(string path, CancellationToken token) =>
        rclone.ListFoldersAsync(path, token);
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
        CloseCommands();
        dashboard.SetActive(false);
        HomeLayer.Visibility = Visibility.Hidden;
        PhotoLayer.Visibility = Visibility.Visible;
        slideDue = DateTime.UtcNow.AddSeconds(settings.SlideSeconds);
        preloadDue = DateTime.MinValue;
        ShowFullscreen();
        UpdatePhotoInfo();
        UpdateCommands();
    }

    private void ShowHome()
    {
        if (closed || settingsWindow is not null)
            return;
        mode = Mode.Home;
        CloseCommands();
        PhotoLayer.Visibility = Visibility.Hidden;
        HomeLayer.Visibility = Visibility.Visible;
        RecordInteraction();
        ShowFullscreen();
        dashboard.SetActive(true);
        UpdatePhotoInfo();
        UpdateCommands();
    }

    private void ShowFullscreen()
    {
        bool restore = !IsVisible || WindowState == WindowState.Minimized;
        restoringWindow = true;
        CommandPopup.IsOpen = false;
        try
        {
            if (!IsVisible)
                Show();
            if (restore)
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (WindowState == WindowState.Minimized || IsIconic(handle))
                {
                    ShowWindow(handle, 9); // SW_RESTORE
                    WindowState = WindowState.Normal;
                }
                PositionOnPrimaryScreen();
                UpdateLayout();
                brightnessRequested = true;
            }
            Topmost = true;
            Activate();
        }
        finally { restoringWindow = false; }

        // A Popup needs the restored visual bounds, not the hidden window's last position.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateCommands));
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
        CloseCommands();
        UpdateCommands();
        dashboard.SetActive(false);
        Topmost = false;
        HomeLayer.Visibility = Visibility.Hidden;
        PhotoLayer.Visibility = Visibility.Hidden;
        if (IsVisible)
            Hide();

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
        if (mode != Mode.Desktop && IsVisible && !adjustingBrightness && now >= nextBrightnessReading &&
            (brightnessRequested || settings.BrightnessMode == DisplayBrightnessMode.Adaptive))
            _ = UpdateBrightnessAsync();
        UpdateNightMode();
        if (CommandMenu.Visibility == Visibility.Visible && now >= commandsUntil && !CommandMenu.IsMouseOver)
            CloseCommands();
        if (!refreshing && now >= catalogDue)
            _ = RefreshCatalogAsync();

        if (mode == Mode.Home && settingsWindow is null)
        {
            if (Stopwatch.GetElapsedTime(latestInteraction) >= TimeSpan.FromSeconds(settings.InactivitySeconds))
            {
                ShowFrame();
                return;
            }
            dashboard.TimerTick();
        }

        if (mode != Mode.Frame || fading || settingsWindow is not null ||
            paused && current is not null && !showNextRequested)
            return;
        if ((forwardPhoto ?? next) is not null && (current is null || showNextRequested || slideDue <= now))
            ShowNext(now);
        if (!fading && next is null && !preparing && preloadDue <= now &&
            (useCachedPhotos || playback.HasCandidates(settings.PhotoRatioFilter)))
            _ = PreloadAsync();
    }

    private async Task UpdateBrightnessAsync()
    {
        adjustingBrightness = true;
        brightnessRequested = false;
        var snapshot = settings;
        nextBrightnessReading = DateTime.UtcNow.AddSeconds(4);
        try
        {
            if (snapshot.BrightnessMode == DisplayBrightnessMode.Windows)
            {
                BrightnessStatus = "Luminosità gestita da Windows.";
                return;
            }

            var result = await Task.Run(() => brightness.Update(snapshot.BrightnessMode,
                snapshot.ManualBrightness, brightnessTarget));
            if (closed)
                return;
            if (settings.BrightnessMode != snapshot.BrightnessMode ||
                settings.ManualBrightness != snapshot.ManualBrightness)
                return;
            brightnessTarget = result.Target;
            BrightnessStatus = result.Status;
        }
        catch (Exception error)
        {
            BrightnessStatus = "Luminosità del display non disponibile. Controlla le impostazioni di Windows.";
            nextBrightnessReading = DateTime.UtcNow.AddSeconds(30);
            storage.Log("Luminosità del display", error);
        }
        finally { adjustingBrightness = false; }
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
            useCachedPhotos = playback.RefreshFailed;
            if (!useCachedPhotos && snapshot.CachePhotos)
            {
                var revisions = playback.CatalogRevisions;
                await Task.Run(() => photoCache.Prune(snapshot, revisions, token), token);
                if (token.IsCancellationRequested || closed)
                    return;
            }
            if (next is { } staged && !IsPlayable(staged))
                next = null;
            if (previousPhoto is { } previous && !IsPlayable(previous))
                previousPhoto = null;
            if (forwardPhoto is { } forward && !IsPlayable(forward))
                forwardPhoto = null;
            if (!useCachedPhotos && current is not null && !playback.Contains(current))
                ClearPhotos();
            else if (!useCachedPhotos && showingCachedPhoto)
            {
                showingCachedPhoto = false;
                UpdatePhotoInfo();
            }
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
            var currentItem = current;
            DisplayPhoto? result = useCachedPhotos
                ? await Task.Run(() => photoCache.Load(snapshot, currentItem, cachedPhotosSeen, token), token) : null;
            try
            {
                if (result is null && playback.HasCandidates(snapshot.PhotoRatioFilter))
                    result = await playback.PrepareNextAsync(snapshot, width, height, currentItem, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException or RcloneException or GraphRequestException
                    or SignInRequiredException or Microsoft.Identity.Client.MsalException)
            {
                useCachedPhotos = true;
                photoError = PhotoPlayback.Describe(error);
                storage.Log("Lettura foto OneDrive", error);
                result = await Task.Run(() => photoCache.Load(snapshot, currentItem, cachedPhotosSeen, token), token);
                if (result is null)
                    throw;
            }

            if (result is { FromCache: false } downloaded)
                await Task.Run(() => photoCache.Remember(snapshot, downloaded, token), token);
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
        var photo = forwardPhoto ?? next!;
        if (forwardPhoto is not null)
            forwardPhoto = null;
        else
            next = null;
        showNextRequested = false;
        if (!IsPlayable(photo))
            return;
        previousPhoto = CaptureCurrent();
        Display(photo, now);
    }

    private DisplayPhoto? CaptureCurrent() => current is not null && FrontPhoto.Source is BitmapSource bitmap
        ? new DisplayPhoto(current, bitmap, showingCachedPhoto) : null;

    private bool IsPlayable(DisplayPhoto photo) => playback.Contains(photo.Item) || photo.FromCache && useCachedPhotos;

    private void Display(DisplayPhoto photo, DateTime now)
    {
        BackPhoto.Source = FrontPhoto.Source;
        FrontPhoto.Source = photo.Bitmap;
        current = photo.Item;
        showingCachedPhoto = photo.FromCache;
        UpdatePhotoInfo();
        UpdateCommands();
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
            UpdateCommands();
        };
        FrontPhoto.BeginAnimation(OpacityProperty, animation);
    }

    private void ClearPhotos()
    {
        current = null;
        next = null;
        previousPhoto = forwardPhoto = null;
        showNextRequested = showingCachedPhoto = false;
        UpdatePhotoInfo();
        FrontPhoto.BeginAnimation(OpacityProperty, null);
        FrontPhoto.Source = null;
        BackPhoto.Source = null;
        fading = false;
        UpdateFrameStatus();
    }

    internal void SetPaused(bool pause)
    {
        paused = pause;
        PauseButton.Content = paused ? "Riprendi presentazione" : "Pausa presentazione";
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
        bool ratioChanged = previous.PhotoRatioFilter != updated.PhotoRatioFilter;
        bool nightChanged = (previous.NightEnabled, previous.NightBlackScreen, previous.NightStart, previous.NightEnd) !=
            (updated.NightEnabled, updated.NightBlackScreen, updated.NightStart, updated.NightEnd);

        CancelWork();
        settings = updated;
        if (previous.BrightnessMode != updated.BrightnessMode || previous.ManualBrightness != updated.ManualBrightness)
        {
            brightnessRequested = true;
            brightnessTarget = null;
            nextBrightnessReading = DateTime.MinValue;
        }
        if (sourceChanged)
            playback.Reset();
        if (sourceChanged || ratioChanged || previous.CachePhotos && !updated.CachePhotos)
        {
            ResetCachedPhotos();
            ClearPhotos();
        }
        if (nightChanged)
            nightOverridden = false;
        UpdatePhotoInfo();
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
        ResetCachedPhotos();
        ClearPhotos();
        RefreshSoon();
    }

    internal async Task SignInAsync(CancellationToken token)
    {
        await signIn.ConnectAsync(settings, token);
        CancelWork();
        playback.Reset();
        ResetCachedPhotos();
        ClearPhotos();
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
            ResetCachedPhotos();
            ClearPhotos();
        }
        finally
        {
            RefreshSoon();
        }
    }

    private void ResetCachedPhotos()
    {
        useCachedPhotos = false;
        cachedPhotosSeen.Clear();
        photoCache.Clear();
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
                playback.HasCandidates(settings.PhotoRatioFilter)
                    ? "Cerco una foto tra le cartelle abilitate…"
                    : "Nessuna foto rispetta le opzioni di formato.");
        UpdateCommands();
    }

    private void UpdateHomeStatus()
    {
        BrowserHost.Visibility = dashboard.Ready ? Visibility.Visible : Visibility.Hidden;
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

        CloseCommands();
        CommandPopup.IsOpen = false;
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
            UpdateCommands();
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
        if (current is not null)
            ShowHome();
    }

    private void Commands_Click(object sender, RoutedEventArgs args)
    {
        CommandMenu.Visibility = CommandMenu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        commandsUntil = DateTime.UtcNow.AddSeconds(8);
        RecordInteraction();
        UpdateCommands();
        PositionCommands();
    }

    private void CloseCommands() => CommandMenu.Visibility = Visibility.Collapsed;

    private void PositionCommands()
    {
        if (!CommandPopup.IsOpen)
            return;
        double offset = CommandPopup.HorizontalOffset;
        CommandPopup.HorizontalOffset = offset + 1;
        CommandPopup.HorizontalOffset = offset;
    }

    private void UpdateCommands()
    {
        CommandPopup.IsOpen = IsLoaded && IsVisible && !closed && !restoringWindow && mode != Mode.Desktop &&
            WindowState != WindowState.Minimized && SurfaceRoot.ActualWidth > 0 && SurfaceRoot.ActualHeight > 0 &&
            settingsWindow is null && NightBlack.Visibility != Visibility.Visible;
        SwitchModeButton.Content = mode == Mode.Home ? "Cornice fotografica" : "Home Assistant";
        SlideControls.Visibility = mode == Mode.Frame ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = previousPhoto is not null && !fading;
        NextButton.IsEnabled = current is not null && !fading;
    }

    private void SwitchMode_Click(object sender, RoutedEventArgs args)
    {
        if (mode == Mode.Home)
            ShowFrame();
        else
            ShowHome();
    }

    private void Pause_Click(object sender, RoutedEventArgs args)
    {
        SetPaused(!paused);
        CloseCommands();
    }

    private void Previous_Click(object sender, RoutedEventArgs args)
    {
        if (fading || previousPhoto is not { } previous || !IsPlayable(previous))
            return;
        previousPhoto = null;
        forwardPhoto = CaptureCurrent();
        showNextRequested = false;
        CloseCommands();
        Display(previous, DateTime.UtcNow);
    }

    private void Next_Click(object sender, RoutedEventArgs args)
    {
        showNextRequested = true;
        preloadDue = DateTime.MinValue;
        CloseCommands();
    }

    private void RetryPhotos_Click(object sender, RoutedEventArgs args)
    {
        preloadDue = DateTime.MinValue;
        CancelWork();
        RefreshSoon();
    }

    private void UpdatePhotoInfo()
    {
        var label = new List<string>();
        if (current is not null && mode == Mode.Frame)
        {
            if (settings.ShowPhotoName)
                label.Add(current.Name);
            if (settings.ShowPhotoDate && current.PhotoDate is { } date)
                label.Add(date.LocalDateTime.ToString("d"));
            if (showingCachedPhoto)
                label.Add("Foto salvata per l’uso offline");
        }
        PhotoInfo.Text = string.Join(" · ", label);
        PhotoInfoPanel.Visibility = label.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNightMode()
    {
        bool scheduled = settings.IsNight(TimeOnly.FromDateTime(DateTime.Now));
        if (!scheduled)
            nightOverridden = false;
        bool active = scheduled && !nightOverridden && settingsWindow is null && mode != Mode.Desktop;
        if (active && !nightWasActive && mode == Mode.Home)
            ShowFrame();
        nightWasActive = active;
        var black = active && settings.NightBlackScreen && mode == Mode.Frame ? Visibility.Visible : Visibility.Collapsed;
        if (NightBlack.Visibility != black)
        {
            NightBlack.Visibility = black;
            if (black == Visibility.Visible)
                NightBlack.Focus();
            UpdateCommands();
        }
        NightDim.Visibility = active && mode == Mode.Frame && !settings.NightBlackScreen
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Wake_Click(object sender, RoutedEventArgs args)
    {
        nightOverridden = true;
        UpdateNightMode();
    }
    private void Minimize_Click(object sender, RoutedEventArgs args) => LeaveForDesktop(showShell: false);
    private void Exit_Click(object sender, RoutedEventArgs args) => Close();
    private void Windows_Click(object sender, RoutedEventArgs args) => LeaveForDesktop(showShell: true);
    private void Settings_Click(object sender, RoutedEventArgs args) => ShowSettings();
    private async void RetryHome_Click(object sender, RoutedEventArgs args) => await dashboard.RetryAsync();

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        CommandPopup.IsOpen = false;
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
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
        int width, int height, uint flags);
}
