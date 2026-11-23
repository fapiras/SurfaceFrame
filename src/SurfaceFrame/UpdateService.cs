using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SurfaceFrame;

internal sealed record UpdateChannel(string Repository, string PublicKeyPem);
internal sealed record UpdateManifest(string Version, string Sha256);
internal sealed record AvailableUpdate(Version Version, string Notes, string AssetBase,
    byte[] ManifestBytes, byte[] Signature);

internal sealed class UpdateService(LocalStorage storage)
{
    internal const string PackageName = "SurfaceFrame-win-x64.zip";
    private const long MaxPackageSize = 250L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, MaxAutomaticRedirections = 5 })
        { Timeout = TimeSpan.FromMinutes(2) };

    internal static Version CurrentVersion { get; } =
        Version.Parse(typeof(UpdateService).Assembly.GetName().Version!.ToString(3));

    internal string? LastResult
    {
        get
        {
            try { return File.ReadAllText(Path.Combine(storage.Root, "update-result.txt")); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    internal async Task<AvailableUpdate?> CheckAsync(CancellationToken token)
    {
        var channel = ReadChannel();
        using var release = await FetchAsync($"https://api.github.com/repos/{channel.Repository}/releases/latest",
            512 * 1024, token);
        using var document = await JsonDocument.ParseAsync(release, cancellationToken: token);
        var entry = document.RootElement;
        var tag = entry.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !TryVersion(tag[1..], out var version))
            throw new InvalidDataException("La release GitHub deve avere un tag nel formato v1.2.3.");
        if (version <= CurrentVersion)
            return null;

        string prefix = $"https://github.com/{channel.Repository}/releases/download/{tag}/";
        using var manifestFile = await FetchAsync(prefix + "surfaceframe-update.json", 4096, token);
        using var signatureFile = await FetchAsync(prefix + "surfaceframe-update.sig", 2048, token);
        byte[] manifestBytes = manifestFile.ToArray();
        byte[] signature = signatureFile.ToArray();
        VerifyManifest(channel.PublicKeyPem, manifestBytes, signature, version);
        string notes = entry.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? "" : "";
        return new(version, notes[..Math.Min(notes.Length, 2000)].Trim(), prefix, manifestBytes, signature);
    }

    internal async Task PrepareAsync(AvailableUpdate update, CancellationToken token)
    {
        _ = InstalledExecutable();
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(storage.Root, "updates", id);
        Directory.CreateDirectory(stage);
        try
        {
            var manifest = VerifyManifest(ReadChannel().PublicKeyPem, update.ManifestBytes, update.Signature, update.Version);
            string package = Path.Combine(stage, PackageName);
            await using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await DownloadAsync(update.AssetBase + PackageName, output, MaxPackageSize, token);
            await using (var zip = File.OpenRead(package))
            {
                byte[] digest = await SHA256.HashDataAsync(zip, token);
                CheckHash(manifest, digest);
            }
            File.WriteAllBytes(Path.Combine(stage, "surfaceframe-update.json"), update.ManifestBytes);
            File.WriteAllBytes(Path.Combine(stage, "surfaceframe-update.sig"), update.Signature);
            token.ThrowIfCancellationRequested();
            // This worker runs from the old installation and survives the main process.
            using var parent = Process.GetCurrentProcess();
            using var worker = Start(InstalledExecutable(), "--apply-update", id, update.Version.ToString(),
                Environment.ProcessId.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString());
        }
        catch
        {
            TryRemoveStage(stage);
            throw;
        }
    }

    internal async Task ApplyAsync(string id, Version version, int parentId, long parentStarted)
    {
        if (!Guid.TryParseExact(id, "N", out _) || version <= CurrentVersion)
            throw new InvalidDataException("Richiesta di aggiornamento non valida.");

        string stage = Path.Combine(storage.Root, "updates", id);
        string previous = InstalledExecutable();
        string destination = Path.Combine(storage.Root, "app", $"{version}-{id}");
        string next = Path.Combine(destination, "SurfaceFrame.exe");
        Process? launched = null;
        bool oldExited = false;
        try
        {
            await WaitForParentAsync(parentId, parentStarted);
            oldExited = true;
            string package = Path.Combine(stage, PackageName);
            var channel = ReadChannel();
            var manifest = VerifyManifest(channel.PublicKeyPem,
                File.ReadAllBytes(Path.Combine(stage, "surfaceframe-update.json")),
                File.ReadAllBytes(Path.Combine(stage, "surfaceframe-update.sig")), version);
            // Keep the verified archive handle open until extraction completes.
            using (var input = File.OpenRead(package))
            {
                CheckHash(manifest, SHA256.HashData(input));
                input.Position = 0;
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);
                long bytes = 0;
                foreach (var file in archive.Entries)
                {
                    if (file.Length > 650L * 1024 * 1024 - bytes)
                        throw new InvalidDataException("Pacchetto troppo grande dopo l’estrazione.");
                    bytes += file.Length;
                }
                archive.ExtractToDirectory(destination);
            }
            if (!File.Exists(next) || !File.Exists(Path.Combine(destination, "SurfaceFrame.dll")) ||
                !File.Exists(Path.Combine(destination, "coreclr.dll")))
                throw new InvalidDataException("Pacchetto Windows incompleto.");
            var fileVersion = FileVersionInfo.GetVersionInfo(next);
            if (new Version(fileVersion.FileMajorPart, fileVersion.FileMinorPart, fileVersion.FileBuildPart) != version)
                throw new InvalidDataException("La versione dell’eseguibile non corrisponde al rilascio.");
            // Carry the trust configuration forward. The update ZIP contains only the app.
            File.WriteAllText(Path.Combine(destination, "update-channel.json"), JsonSerializer.Serialize(channel, JsonOptions));
            launched = Start(next, "--update-probe", id);
            await ConfirmStartupAsync(stage, launched);

            bool autoStart = storage.Load().AutoStart;
            try
            {
                LocalStorage.ConfigureAutoStart(autoStart, next);
                SetStartShortcut(next);
            }
            catch
            {
                LocalStorage.ConfigureAutoStart(autoStart, previous);
                SetStartShortcut(previous);
                throw;
            }
            Report($"Surface Frame aggiornato alla versione {version}.");
        }
        catch (Exception error)
        {
            storage.Log("Aggiornamento non completato", error);
            if (oldExited)
            {
                if (launched is { HasExited: false })
                {
                    launched.Kill();
                    await launched.WaitForExitAsync();
                }
                using var restored = Start(previous);
                Report($"Aggiornamento a {version} non riuscito. Riavviata la versione {CurrentVersion}.");
            }
            else
            {
                Report("Aggiornamento interrotto. Chiudi le finestre di Surface Frame e riprova.");
            }
        }
        finally
        {
            launched?.Dispose();
            TryRemoveStage(stage);
        }
    }

    internal void SignalStartup(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            return;
        string folder = Path.Combine(storage.Root, "updates", id);
        if (Directory.Exists(folder))
            File.WriteAllText(Path.Combine(folder, "ready"), Environment.ProcessId.ToString());
    }

    internal static UpdateManifest VerifyManifest(string pem, byte[] bytes, byte[] signature, Version expectedVersion)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("La firma dell’aggiornamento non è valida.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, JsonOptions);
        if (manifest is null || !TryVersion(manifest.Version, out var version) || version != expectedVersion ||
            manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Manifesto dell’aggiornamento non valido.");
        return manifest;
    }

    internal static void CheckHash(UpdateManifest manifest, byte[] digest)
    {
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(manifest.Sha256), digest))
            throw new InvalidDataException("L’integrità del pacchetto non coincide con il manifesto firmato.");
    }

    internal static bool TryVersion(string? text, out Version version)
    {
        version = new Version();
        if (!Version.TryParse(text, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString() != text)
            return false;
        version = parsed;
        return true;
    }

    private UpdateChannel ReadChannel()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "update-channel.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("Il canale degli aggiornamenti non è configurato per questa installazione.");
        var channel = JsonSerializer.Deserialize<UpdateChannel>(File.ReadAllText(path), JsonOptions);
        if (channel?.Repository is null || !Regex.IsMatch(channel.Repository, @"\A[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+\z") ||
            channel.Repository.EndsWith("/.") || channel.Repository.EndsWith("/..") ||
            string.IsNullOrWhiteSpace(channel.PublicKeyPem))
            throw new InvalidDataException("Configurazione del canale aggiornamenti non valida.");
        return channel;
    }

    private string InstalledExecutable()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Eseguibile non individuato.");
        string? installedParent = Directory.GetParent(Path.GetDirectoryName(executable)!)?.FullName;
        if (!string.Equals(installedParent, Path.Combine(storage.Root, "app"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "SurfaceFrame.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installa Surface Frame con Avvia-Surface-Frame.cmd prima di aggiornarlo.");
        return executable;
    }

    private static async Task<MemoryStream> FetchAsync(string address, long limit, CancellationToken token)
    {
        var buffer = new MemoryStream();
        try
        {
            await DownloadAsync(address, buffer, limit, token);
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static async Task DownloadAsync(string address, Stream output, long limit, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd($"SurfaceFrame/{CurrentVersion}");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Release o file mancanti nel canale GitHub configurato.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Il file di aggiornamento supera le dimensioni consentite.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            total += count;
            if (total > limit)
                throw new InvalidDataException("Il file di aggiornamento supera le dimensioni consentite.");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
    }

    private static async Task WaitForParentAsync(int pid, long ticks)
    {
        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != ticks)
                    return;
            }
            catch (InvalidOperationException) when (parent.HasExited) { return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }

    private static async Task ConfirmStartupAsync(string stage, Process process)
    {
        string marker = Path.Combine(stage, "ready");
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (process.HasExited)
                break;
            if (File.Exists(marker) && File.ReadAllText(marker) == process.Id.ToString())
            {
                await Task.Delay(1500);
                if (!process.HasExited)
                    return;
                break;
            }
            await Task.Delay(250);
        }
        throw new InvalidOperationException("La nuova versione non ha confermato l’avvio.");
    }

    private static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Impossibile avviare Surface Frame.");
    }

    private static void SetStartShortcut(string executable)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string path = Path.Combine(programs, "Surface Frame.lnk");
        if (!File.Exists(path))
            return;
        Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Collegamento Start non disponibile.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = executable;
        shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
        shortcut.Save();
    }

    private void Report(string message)
    {
        try { File.WriteAllText(Path.Combine(storage.Root, "update-result.txt"), message); }
        catch (IOException error) { storage.Log(message, error); }
        catch (UnauthorizedAccessException error) { storage.Log(message, error); }
    }

    private void TryRemoveStage(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException error) { storage.Log("Pulizia temporanei di aggiornamento", error); }
        catch (UnauthorizedAccessException error) { storage.Log("Pulizia temporanei di aggiornamento", error); }
    }
}
