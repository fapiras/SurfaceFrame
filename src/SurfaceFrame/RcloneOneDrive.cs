using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SurfaceFrame;

internal sealed class RcloneOneDrive(string storageRoot, HttpClient graph)
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
            string state = ReadString(initial.RootElement, "State");
            if (state.Length == 0 || ReadString(initial.RootElement.GetProperty("Option"), "Name") != "config_is_local")
                throw new RcloneException("Impossibile avviare la procedura di accesso Microsoft.");

            using var authorization = await ConfigStepAsync(staging.Path, token, "config", "update", RemoteName,
                "--continue", "--state", state, "--result", "true");
            if (ReadString(authorization.RootElement, "Error").Length > 0)
                throw new RcloneException("Microsoft non ha completato l’autorizzazione di OneDrive.");
            using var credentials = await ConfigStepAsync(staging.Path, token, "config", "dump");
            string session = ReadString(credentials.RootElement.GetProperty(RemoteName), "token");
            if (session.Length == 0)
                throw new RcloneException("Il browser ha terminato l’accesso, ma la sessione Microsoft non è disponibile.");
            using var oauth = JsonDocument.Parse(session);
            string accessToken = ReadString(oauth.RootElement, "access_token");
            if (accessToken.Length == 0)
                throw new RcloneException("La sessione Microsoft non contiene l’autorizzazione di lettura.");

            string driveId = await ResolvePersonalDriveAsync(graph, accessToken, token);
            // A non-interactive update saves these options before asking whether to replace the existing token.
            using var update = await ConfigStepAsync(staging.Path, token, "config", "update", RemoteName,
                "drive_id", driveId, "drive_type", "personal", "--non-interactive");
            token.ThrowIfCancellationRequested();
            File.Move(staging.Path, ConfigPath, overwrite: true);
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

    internal static async Task<string> ResolvePersonalDriveAsync(HttpClient graph, string accessToken,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me/drive?$select=id,driveType");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await graph.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            int status = (int)response.StatusCode;
            string message = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "Microsoft non trova il OneDrive personale per questo account. Apri OneDrive nel browser e verifica che mostri i tuoi file.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    $"Microsoft ha confermato l’accesso, ma ha rifiutato la lettura di OneDrive (HTTP {status}).",
                _ => $"Microsoft non ha restituito il OneDrive personale (HTTP {status})."
            };
            throw new RcloneException(message);
        }
        using var body = await response.Content.ReadAsStreamAsync(token);
        using var drive = await JsonDocument.ParseAsync(body, cancellationToken: token);
        string id = ReadString(drive.RootElement, "id");
        if (id.Length == 0 || ReadString(drive.RootElement, "driveType") != "personal")
            throw new RcloneException("Microsoft non ha restituito un drive di tipo personale per questo account.");
        return id;
    }

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
