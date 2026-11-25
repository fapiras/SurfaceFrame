using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SurfaceFrame;

public enum DisplayBrightnessMode { Windows, Manual, Adaptive }

public sealed class FrameSettings
{
    public string MicrosoftClientId { get; set; } = "";
    public bool? UseRclone { get; set; }
    public bool UsesRclone => UseRclone ?? (MicrosoftClientId.Length == 0);
    public bool AllowSharedLinkScope { get; set; }
    public List<FolderSource> Sources { get; set; } = [];
    public string DashboardUrl { get; set; } = "";
    public int SlideSeconds { get; set; } = 30;
    public int InactivitySeconds { get; set; } = 120;
    public int CatalogMinutes { get; set; } = 15;
    public int FadeMilliseconds { get; set; } = 700;
    public double MinimumAspectRatio { get; set; } = 1;
    public bool Shuffle { get; set; } = true;
    public bool FillScreen { get; set; } = true;
    public bool IncludeVerticalPhotos { get; set; }
    public bool ShowPhotoName { get; set; }
    public bool ShowPhotoDate { get; set; }
    public bool CachePhotos { get; set; } = true;
    public DisplayBrightnessMode BrightnessMode { get; set; }
    public int ManualBrightness { get; set; } = 50;
    public bool NightEnabled { get; set; }
    public bool NightBlackScreen { get; set; }
    public TimeOnly NightStart { get; set; } = new(23, 0);
    public TimeOnly NightEnd { get; set; } = new(7, 0);

    internal double PhotoRatioFilter => IncludeVerticalPhotos ? 0 : MinimumAspectRatio;

    internal bool IsNight(TimeOnly time) => NightEnabled && (NightStart < NightEnd
        ? time >= NightStart && time < NightEnd
        : time >= NightStart || time < NightEnd);

    public bool AutoStart { get; set; }
    public bool StartInFrame { get; set; } = true;

    public Uri? DashboardUri => ParseDashboardUrl(DashboardUrl);
    public bool NeedsSharedScope => Sources.Any(source => source.Enabled && source.IsShareLink);

    public static Uri? ParseDashboardUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;

    public void Validate()
    {
        if (!UsesRclone && MicrosoftClientId.Length > 0 && !Guid.TryParse(MicrosoftClientId, out _))
            throw new ArgumentException("Inserisci l’ID applicazione Microsoft (un GUID).");
        if (DashboardUrl.Length > 0 && DashboardUri is null)
            throw new ArgumentException("L’indirizzo Home Assistant deve iniziare con http:// o https://.");
        if (SlideSeconds is < 3 or > 3600 || InactivitySeconds is < 5 or > 3600 ||
            CatalogMinutes is < 1 or > 1440 || FadeMilliseconds is < 0 or > 2000 ||
            !double.IsFinite(MinimumAspectRatio) || MinimumAspectRatio is < 1 or > 10)
            throw new ArgumentException("Controlla gli intervalli e il rapporto minimo delle foto.");
        if (!Enum.IsDefined(BrightnessMode) || ManualBrightness is < 5 or > 100)
            throw new ArgumentException("Controlla il valore della luminosità.");
        if (NightEnabled && NightStart == NightEnd)
            throw new ArgumentException("Gli orari di inizio e fine della modalità notte devono essere diversi.");
        if (Sources.Any(source => source is null || !FolderSource.IsValid(source.Address)))
            throw new ArgumentException("Usa un percorso come /Foto/Cornice o un link di OneDrive personale.");
        if (Sources.Select(source => source.Id).Distinct().Count() != Sources.Count)
            throw new ArgumentException("Le cartelle devono avere identificativi distinti.");
    }
}

public sealed class FolderSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Address { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;

    internal bool IsShareLink => !Address.StartsWith('/');

    internal static bool IsValid(string address)
    {
        if (address.StartsWith('/') && !address.StartsWith("//", StringComparison.Ordinal))
            return !address.Contains('\\') && !address.Any(char.IsControl)
                && address.Split('/', StringSplitOptions.RemoveEmptyEntries).All(part => part is not "." and not "..");

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host is "1drv.ms" or "onedrive.live.com";
    }
}

internal sealed class LocalStorage
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };
    private readonly object logLock = new();
    internal string Root { get; }
    internal LocalStorage(string? root = null) => Root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceFrame");
    private string SettingsPath => Path.Combine(Root, "settings.json");
    internal string LogPath => Path.Combine(Root, "logs", "surface-frame.log");
    internal string? StartupError { get; private set; }

    internal FrameSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new();

            var settings = JsonSerializer.Deserialize<FrameSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? throw new JsonException("Configurazione vuota");
            settings.Validate();
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NullReferenceException)
        {
            Log("Configurazione non valida", error);
            StartupError = "Configurazione illeggibile. Verifica settings.json oppure risalva dalle impostazioni.";
            return new();
        }
    }

    internal void Save(FrameSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Root);
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, SettingsPath, overwrite: true);
        StartupError = null;
    }

    internal static void ConfigureAutoStart(bool enabled, string? target = null)
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            string executable = target ?? Environment.ProcessPath ?? throw new IOException("Eseguibile non disponibile.");
            run.SetValue("SurfaceFrame", $"\"{executable}\" --autostart", RegistryValueKind.String);
        }
        else
        {
            run.DeleteValue("SurfaceFrame", throwOnMissingValue: false);
        }
    }

    internal void Log(string message, Exception? error = null)
    {
        lock (logLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);

                string kind = error is null ? "" : $" ({error.GetType().Name}, 0x{error.HResult:X8})";
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{kind}{Environment.NewLine}");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(failure.GetType().Name);
            }
        }
    }
}
