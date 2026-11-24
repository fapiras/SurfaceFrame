using System.Diagnostics;
using System.Text.Json;

namespace SurfaceFrame;

internal sealed class RcloneOneDrive(string storageRoot)
{
    private const string RemoteName = "surfaceframe";
    private const int CatalogLimit = 32 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string ConfigPath => Path.Combine(storageRoot, "auth", "rclone-onedrive.conf");
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "rclone", "rclone.exe");

    internal async Task ConnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            string prefix = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            using var staging = new TemporaryConfig(ConfigPath);
            using var initial = await ConfigStepAsync(staging.Path, token, "config", "create", RemoteName, "onedrive",
                "tenant", "consumers", "auth_url", prefix + "authorize", "token_url", prefix + "token",
                "access_scopes", "Files.Read offline_access", "--non-interactive");
            JsonDocument current = initial;
            try
            {
                // The bundled version provides machine-readable OAuth and drive-selection steps.
                for (int step = 0; step < 8; step++)
                {
                    var response = current.RootElement;
                    if (ReadString(response, "Error").Length > 0)
                        break;
                    string state = ReadString(response, "State");
                    if (state.Length == 0)
                    {
                        if (!File.ReadLines(staging.Path).Any(line => line.Trim() == "drive_type = personal"))
                            break;
                        token.ThrowIfCancellationRequested();
                        File.Move(staging.Path, ConfigPath, overwrite: true);
                        return;
                    }

                    var option = response.GetProperty("Option");
                    string answer = ReadString(option, "Name") switch
                    {
                        "config_is_local" or "config_drive_ok" => "true",
                        "config_type" => "onedrive",
                        "config_driveid" => FindPersonalDrive(option),
                        _ => ""
                    };
                    if (answer.Length == 0)
                        break;
                    var next = await ConfigStepAsync(staging.Path, token, "config", "update", RemoteName,
                        "--continue", "--state", state, "--result", answer);
                    if (!ReferenceEquals(current, initial))
                        current.Dispose();
                    current = next;
                }
            }
            finally
            {
                if (!ReferenceEquals(current, initial))
                    current.Dispose();
            }
            throw new RcloneException("Collegamento non riuscito. Scegli un account Microsoft personale con OneDrive attivo.");
        }
        catch (RcloneException error) when (error.Message.StartsWith("Lettura", StringComparison.Ordinal))
        {
            throw new RcloneException("Accesso Microsoft non completato. Riprova con il tuo account personale.");
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task DisconnectAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            File.Delete(ConfigPath);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<IReadOnlyList<PhotoItem>> ListAsync(FolderSource source, CancellationToken token)
    {
        if (source.IsShareLink)
            throw new RcloneException("Usa il percorso di una cartella nel tuo OneDrive, per esempio /Foto/Cornice.");

        string folder = string.Join("/", source.Address.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var arguments = new List<string> { "lsjson", RemoteName + ":" + folder, "--files-only" };
        if (source.IncludeSubfolders)
            arguments.Add("--recursive");
        using var data = await ReadAsync(CatalogLimit, token, arguments.ToArray());
        using var catalog = await JsonDocument.ParseAsync(data, cancellationToken: token);
        var photos = new List<PhotoItem>();
        foreach (var entry in catalog.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            string path = ReadString(entry, "Path");
            string name = ReadString(entry, "Name");
            if (path.Length == 0 || !FolderSource.IsValid("/" + path) || !OneDrive.IsPhoto(name))
                continue;
            string remotePath = folder.Length == 0 ? path : folder + "/" + path;
            string id = ReadString(entry, "ID");
            long bytes = entry.GetProperty("Size").GetInt64();
            string revision = ReadString(entry, "ModTime") + "/" + bytes;
            photos.Add(new PhotoItem("rclone", id.Length == 0 ? remotePath : id, name, revision,
                bytes, 0, 0, 0, RclonePath: remotePath));
        }
        return photos;
    }

    internal Task<MemoryStream> GetOriginalAsync(PhotoItem item, CancellationToken token)
    {
        if (item.Bytes > OneDrive.MaxOriginalBytes)
            throw new InvalidDataException("Foto oltre il limite di 48 MiB.");
        return ReadAsync(OneDrive.MaxOriginalBytes, token, "cat", RemoteName + ":" + item.RclonePath);
    }

    private async Task<MemoryStream> ReadAsync(int limit, CancellationToken token, params string[] arguments)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!File.Exists(ConfigPath))
                throw new RcloneException("Apri Impostazioni → OneDrive e premi Collega OneDrive.");

            return await RunAsync(ConfigPath, limit, token, arguments);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? "" : "";

    internal static string FindPersonalDrive(JsonElement option) =>
        option.GetProperty("Examples").EnumerateArray()
            .Where(example => ReadString(example, "Help").EndsWith(" (personal)", StringComparison.Ordinal))
            .Select(example => ReadString(example, "Value")).FirstOrDefault() ?? "";

    private static async Task<JsonDocument> ConfigStepAsync(string config, CancellationToken token,
        params string[] arguments)
    {
        using var data = await RunAsync(config, 1024 * 1024, token, arguments);
        return await JsonDocument.ParseAsync(data, cancellationToken: token);
    }

    private static async Task<MemoryStream> RunAsync(string config, int limit, CancellationToken token,
        params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(Executable))
            throw new RcloneException("Il componente OneDrive è assente. Reinstalla Surface Frame dallo ZIP completo.");

        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Use the app's private remote and ignore rclone overrides from the environment.
        foreach (string variable in start.Environment.Keys.Where(key =>
            key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        foreach (string argument in arguments.Concat(new[] { "--config", config, "--retries", "1" }))
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new RcloneException("Impossibile avviare rclone.");
        using var cancelProcess = token.Register(() => Stop(process));
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var output = new MemoryStream();
        try
        {
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token)) > 0)
            {
                if (output.Length + count > limit)
                    throw new InvalidDataException("Il contenuto OneDrive supera il limite di lettura.");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(token);
            await stderr;
            token.ThrowIfCancellationRequested();
            if (process.ExitCode is 3 or 4)
                throw new RcloneMissingException();
            if (process.ExitCode != 0)
                throw new RcloneException("Lettura OneDrive non riuscita. Controlla la connessione o collega di nuovo l’account.");
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            Stop(process);
            throw;
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process can exit before Kill reaches it.
        }
    }

    private sealed class TemporaryConfig(string target) : IDisposable
    {
        internal string Path { get; } = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        public void Dispose() => File.Delete(Path);
    }
}

internal class RcloneException(string message) : Exception(message);
internal sealed class RcloneMissingException() : RcloneException("Percorso OneDrive non trovato.");
