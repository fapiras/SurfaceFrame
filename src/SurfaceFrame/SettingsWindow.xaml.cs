using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SurfaceFrame;

public partial class SettingsWindow : Window
{
    private readonly MainWindow frame;
    private readonly ObservableCollection<SourceRow> rows;
    private readonly DispatcherTimer timer;
    private readonly CancellationTokenSource lifetime = new();
    private bool authenticating;
    private AvailableUpdate? availableUpdate;

    internal SettingsWindow(MainWindow frame, FrameSettings settings)
    {
        InitializeComponent();
        this.frame = frame;
        UpdateStatusText.Text = $"Versione {UpdateService.CurrentVersion}. {frame.Updates.LastResult}".TrimEnd();
        ClientId.Text = settings.MicrosoftClientId;
        SharedLinkConsent.IsChecked = settings.AllowSharedLinkScope;
        RefreshMinutes.Text = settings.CatalogMinutes.ToString(CultureInfo.CurrentCulture);
        SlideSeconds.Text = settings.SlideSeconds.ToString(CultureInfo.CurrentCulture);
        FadeMilliseconds.Text = settings.FadeMilliseconds.ToString(CultureInfo.CurrentCulture);
        MinimumRatio.Text = settings.MinimumAspectRatio.ToString(CultureInfo.CurrentCulture);
        Shuffle.IsChecked = settings.Shuffle;
        FillScreen.IsChecked = settings.FillScreen;
        Pause.IsChecked = frame.Paused;
        DashboardUrl.Text = settings.DashboardUrl;
        IdleSeconds.Text = settings.InactivitySeconds.ToString(CultureInfo.CurrentCulture);
        AutoStart.IsChecked = settings.AutoStart;
        StartInFrame.IsChecked = settings.StartInFrame;
        rows = new ObservableCollection<SourceRow>(settings.Sources.Select(source => new SourceRow(source)));
        SourceTable.ItemsSource = rows;
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        OneDriveStatusText.Text = frame.OneDriveStatus;
        DashboardStatusText.Text = frame.DashboardStatus;
        DiagnosticText.Text = string.Join(Environment.NewLine,
            new[] { frame.StartupError, frame.OneDriveStatus, frame.DashboardStatus, frame.PhotoError }
                .Where(message => !string.IsNullOrWhiteSpace(message)));

        foreach (var row in rows)
        {
            var count = frame.Compatibility(row.Id, ParseRatio() ?? 1);
            string? status = frame.SourceStatus(row.Id);
            row.Summary = !row.Enabled ? "Disattivata" : status switch
            {
                "Aggiornata" when count.Pending > 0 => $"{count.Compatible} compatibili · {count.Pending} da verificare",
                "Aggiornata" => $"{count.Compatible} compatibili",
                null => "Da aggiornare",
                _ => status
            };
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs args)
    {
        string address = FolderAddress.Text.Trim();
        if (!FolderSource.IsValid(address))
        {
            ResultText.Text = "Inserisci un link di OneDrive personale o un percorso come /Foto/Cornice.";
            return;
        }
        if (rows.Any(row => row.Address.Equals(address, StringComparison.Ordinal)))
        {
            ResultText.Text = "La cartella è già presente.";
            return;
        }
        var row = new SourceRow(new FolderSource { Address = address });
        rows.Add(row);
        SourceTable.SelectedItem = row;
        SourceTable.ScrollIntoView(row);
        FolderAddress.Clear();
        ResultText.Text = "";
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs args)
    {
        if (SourceTable.SelectedItem is SourceRow row)
            rows.Remove(row);
    }

    private bool SaveForm()
    {
        if (authenticating)
            return false;
        try
        {
            SourceTable.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
            SourceTable.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
            var settings = new FrameSettings
            {
                MicrosoftClientId = ClientId.Text.Trim(),
                AllowSharedLinkScope = SharedLinkConsent.IsChecked == true,
                Sources = rows.Select(row => new FolderSource
                {
                    Id = row.Id, Address = row.Address, Enabled = row.Enabled, IncludeSubfolders = row.Recursive
                }).ToList(),
                CatalogMinutes = ReadInteger(RefreshMinutes.Text, "aggiornamento OneDrive"),
                SlideSeconds = ReadInteger(SlideSeconds.Text, "intervallo foto"),
                FadeMilliseconds = ReadInteger(FadeMilliseconds.Text, "dissolvenza"),
                MinimumAspectRatio = ParseRatio() ?? throw new ArgumentException("Inserisci il rapporto minimo delle foto."),
                Shuffle = Shuffle.IsChecked == true,
                FillScreen = FillScreen.IsChecked == true,
                DashboardUrl = DashboardUrl.Text.Trim(),
                InactivitySeconds = ReadInteger(IdleSeconds.Text, "inattività Home Assistant"),
                AutoStart = AutoStart.IsChecked == true,
                StartInFrame = StartInFrame.IsChecked == true
            };
            frame.SaveSettings(settings);
            frame.SetPaused(Pause.IsChecked == true);
            ResultText.Text = "Impostazioni salvate.";
            return true;
        }
        catch (ArgumentException error)
        {
            ResultText.Text = error.Message;
            return false;
        }
        catch (Exception)
        {
            ResultText.Text = "Impossibile salvare. Verifica l’accesso alla configurazione e all’avvio automatico.";
            return false;
        }
    }

    private double? ParseRatio()
    {
        var culture = CultureInfo.CurrentCulture;
        return double.TryParse(MinimumRatio.Text, NumberStyles.AllowDecimalPoint, culture, out double ratio) ||
            double.TryParse(MinimumRatio.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out ratio)
            ? ratio : null;
    }

    private static int ReadInteger(string value, string name) => int.TryParse(value, out int number)
        ? number : throw new ArgumentException($"Inserisci un numero intero per {name}.");

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (SaveForm())
            Close();
    }

    private void Refresh_Click(object sender, RoutedEventArgs args)
    {
        if (SaveForm())
        {
            frame.RefreshSoon();
            ResultText.Text = "Aggiornamento richiesto.";
        }
    }

    private void OpenEntra_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://entra.microsoft.com/") { UseShellExecute = true });
        }
        catch (Exception)
        {
            ResultText.Text = "Apri entra.microsoft.com nel browser per registrare Surface Frame.";
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs args)
    {
        if (!SaveForm())
            return;
        await AuthenticateAsync(frame.SignInAsync, "Completa l’accesso Microsoft nel browser.", "Account Microsoft connesso.");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs args)
    {
        await AuthenticateAsync(frame.DisconnectAsync, "Disconnessione…", "Account Microsoft disconnesso.");
    }

    private async Task AuthenticateAsync(Func<CancellationToken, Task> action, string pending, string complete)
    {
        if (authenticating)
            return;
        authenticating = true;
        ResultText.Text = pending;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await action(timeout.Token);
            if (IsLoaded)
                ResultText.Text = complete;
        }
        catch (Exception error)
        {
            if (IsLoaded)
                ResultText.Text = error is OperationCanceledException ? "Accesso interrotto o scaduto." : PhotoPlayback.Describe(error);
        }
        finally
        {
            authenticating = false;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        CheckUpdateButton.IsEnabled = false;
        ApplyUpdateButton.Visibility = ReleaseNotes.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Controllo aggiornamenti in corso…";
        try
        {
            availableUpdate = await frame.Updates.CheckAsync(lifetime.Token);
            if (availableUpdate is not { } update)
            {
                UpdateStatusText.Text = $"Versione {UpdateService.CurrentVersion}. Nessun aggiornamento disponibile.";
                return;
            }
            UpdateStatusText.Text = $"Versione installata {UpdateService.CurrentVersion}. Disponibile {update.Version}.";
            ReleaseNotes.Text = update.Notes;
            ReleaseNotes.Visibility = update.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception error)
        {
            if (IsLoaded)
                UpdateStatusText.Text = $"Controllo non riuscito. {error.Message}";
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (availableUpdate is not { } update || !SaveForm())
            return;

        CheckUpdateButton.IsEnabled = ApplyUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = $"Download e verifica della versione {update.Version}…";
        try
        {
            await frame.Updates.PrepareAsync(update, lifetime.Token);
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            if (IsLoaded)
                UpdateStatusText.Text = $"Aggiornamento non avviato. {error.Message}";
        }
        finally { CheckUpdateButton.IsEnabled = ApplyUpdateButton.IsEnabled = true; }
    }

    private void Logs_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (File.Exists(frame.LogPath))
                Process.Start(new ProcessStartInfo(frame.LogPath) { UseShellExecute = true });
            else
                ResultText.Text = "Il log non contiene ancora eventi.";
        }
        catch (Exception)
        {
            ResultText.Text = "Impossibile aprire il log.";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        lifetime.Cancel();
        lifetime.Dispose();
        base.OnClosed(e);
    }

    public sealed class SourceRow : INotifyPropertyChanged
    {
        private string summary = "Da aggiornare";
        internal SourceRow(FolderSource source)
        {
            Id = source.Id;
            Address = source.Address;
            Enabled = source.Enabled;
            Recursive = source.IncludeSubfolders;
        }
        public Guid Id { get; }
        public string Address { get; }
        public bool Enabled { get; set; }
        public bool Recursive { get; set; }
        public string Summary
        {
            get => summary;
            internal set
            {
                if (summary == value)
                    return;
                summary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
