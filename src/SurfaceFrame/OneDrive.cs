using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SurfaceFrame;

internal sealed record PhotoItem(string DriveId, string ItemId, string Name, string Revision,
    long Bytes, int Width, int Height, int Orientation, int ThumbnailWidth = 0, int ThumbnailHeight = 0,
    string? RclonePath = null)
{
    internal string Key => $"{DriveId}/{ItemId}";
    internal bool IsHeic => Path.GetExtension(Name).ToLowerInvariant() is ".heic" or ".heif";
    internal string CacheKey => $"{Key}/{Revision}";

    internal double? AspectFromMetadata()
    {
        if (Width <= 0 || Height <= 0 || Orientation is < 1 or > 8)
            return null;

        // A swapped EXIF orientation does not establish whether Graph's dimensions
        // are already normalized. The pipeline uses the original or the uncropped thumbnail.
        return Orientation is >= 5 ? null : (double)Width / Height;
    }

    internal double? AspectFromThumbnail(double threshold)
    {
        if (ThumbnailWidth < 1 || ThumbnailHeight < 1 ||
            Math.Abs(ThumbnailWidth - ThumbnailHeight) < 3 ||
            Math.Abs(ThumbnailWidth - ThumbnailHeight * threshold) < 3)
            return null;
        return (double)ThumbnailWidth / ThumbnailHeight;
    }
}

internal sealed class OneDrive(HttpClient graph, HttpClient media)
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0/";
    private const string ItemFields = "id,name,size,eTag,folder,file,image,photo,parentReference,remoteItem";
    internal const int MaxOriginalBytes = 48 * 1024 * 1024;
    private const long MaxThumbnailBytes = 20L * 1024 * 1024;
    internal const long ThumbnailThreshold = 1024 * 1024;

    internal async Task<IReadOnlyList<PhotoItem>> ListAsync(FolderSource source, string token,
        CancellationToken cancellationToken)
    {
        string endpoint = source.IsShareLink
            ? $"shares/{EncodeShareLink(source.Address)}/driveItem"
            : BuildOwnerEndpoint(source.Address);
        using var root = await GetJsonAsync($"{endpoint}?$select={ItemFields}", token, cancellationToken,
            redeem: source.IsShareLink).ConfigureAwait(false);
        var folder = EffectiveItem(root.RootElement);
        if (!HasObject(folder, "folder"))
            throw new InvalidDataException("La sorgente deve puntare a una cartella.");

        var folders = new Queue<(string DriveId, string ItemId)>();
        var visited = new HashSet<(string, string)>();
        var photos = new List<PhotoItem>();
        string? defaultDrive = null;
        if (!source.IsShareLink && GetString(GetObject(folder, "parentReference"), "driveId") is null)
        {
            using var drive = await GetJsonAsync("me/drive?$select=id", token, cancellationToken).ConfigureAwait(false);
            defaultDrive = GetString(drive.RootElement, "id");
        }
        folders.Enqueue(ReadIdentity(folder, defaultDrive));
        while (folders.TryDequeue(out var parent))
        {
            if (!visited.Add(parent))
                continue;

            string initial = $"{ItemEndpoint(parent.DriveId, parent.ItemId)}/children?$select={ItemFields}";
            string? page = initial + "&$expand=thumbnails";
            while (page is not null)
            {
                JsonDocument result;
                try
                {
                    result = await GetJsonAsync(page, token, cancellationToken).ConfigureAwait(false);
                }
                catch (GraphRequestException error) when (error.Status == HttpStatusCode.BadRequest &&
                    page == initial + "&$expand=thumbnails")
                {
                    page = initial;
                    continue;
                }
                using var ownedResult = result;
                foreach (var entry in result.RootElement.GetProperty("value").EnumerateArray())
                {
                    var item = EffectiveItem(entry);
                    if (HasObject(item, "folder"))
                    {
                        if (source.IncludeSubfolders)
                            folders.Enqueue(ReadIdentity(item, parent.DriveId));
                        continue;
                    }

                    string name = GetString(item, "name") ?? GetString(entry, "name") ?? "";
                    if (!HasObject(item, "file") || !IsPhoto(name))
                        continue;

                    var identity = ReadIdentity(item, parent.DriveId);
                    var image = GetObject(item, "image");
                    var photo = GetObject(item, "photo");
                    var thumbnails = GetObject(item, "thumbnails");
                    var large = thumbnails.ValueKind == JsonValueKind.Array && thumbnails.GetArrayLength() > 0
                        ? GetObject(thumbnails[0], "large") : default;
                    photos.Add(new PhotoItem(identity.DriveId, identity.ItemId, name,
                        GetString(item, "eTag") ?? "", GetNumber(item, "size"),
                        (int)GetNumber(image, "width"), (int)GetNumber(image, "height"),
                        (int)GetNumber(photo, "orientation"), (int)GetNumber(large, "width"), (int)GetNumber(large, "height")));
                }
                page = GetString(result.RootElement, "@odata.nextLink");
            }
        }
        return photos;
    }

    internal async Task<MemoryStream?> GetThumbnailAsync(PhotoItem item, string token,
        CancellationToken cancellationToken)
    {
        const string preferredSize = "c4096x4096";
        try
        {
            string endpoint = ItemEndpoint(item.DriveId, item.ItemId);
            using var json = await GetJsonAsync($"{endpoint}/thumbnails?$select={preferredSize}", token,
                cancellationToken).ConfigureAwait(false);
            foreach (var set in json.RootElement.GetProperty("value").EnumerateArray())
            {
                string? url = GetString(GetObject(set, preferredSize), "url");
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return await FetchMediaAsync(uri, MaxThumbnailBytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GraphRequestException failure) when (failure.Status is HttpStatusCode.NotFound
            or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NotImplemented)
        {
            // OneDrive may have no thumbnail for this format. The original has its own limit.
        }
        catch (HttpRequestException)
        {
            // The transient thumbnail URL can expire independently of the original.
        }
        return null;
    }

    internal async Task<MemoryStream> GetOriginalAsync(PhotoItem item, string token, CancellationToken cancellationToken)
    {
        if (item.Bytes > MaxOriginalBytes)
            throw new InvalidDataException("Originale oltre il limite in memoria.");

        string endpoint = ItemEndpoint(item.DriveId, item.ItemId) + "/content";
        if (item.IsHeic)
            endpoint += "?format=jpg";

        using var request = GraphRequest(endpoint, token);
        using var response = await graph.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            return await FetchMediaAsync(location, MaxOriginalBytes, cancellationToken).ConfigureAwait(false);

        EnsureSuccess(response);
        return await ReadInMemoryAsync(response.Content, MaxOriginalBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryStream> FetchMediaAsync(Uri uri, long limit, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Indirizzo HTTPS del contenuto non valido.");

        // Media HTTP client has no bearer token, cookies, or persistent photo cache.
        using var response = await media.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadInMemoryAsync(response.Content, limit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MemoryStream> ReadInMemoryAsync(HttpContent content, long limit,
        CancellationToken cancellationToken)
    {
        long? receivedLength = content.Headers.ContentLength;
        if (receivedLength > limit)
            throw new InvalidDataException("Foto oltre il limite di trasferimento in memoria.");

        var data = new MemoryStream(receivedLength is > 0 ? (int)receivedLength.Value : 0);
        try
        {
            using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (data.Length + count > limit)
                    throw new InvalidDataException("Foto oltre il limite di trasferimento in memoria.");
                data.Write(buffer, 0, count);
            }
            data.Position = 0;
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string endpoint, string token, CancellationToken cancellationToken,
        bool redeem = false)
    {
        using var request = GraphRequest(endpoint, token);
        if (redeem)
            request.Headers.TryAddWithoutValidation("Prefer", "redeemSharingLink");

        using var response = await graph.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage GraphRequest(string endpoint, string token)
    {
        var uri = Uri.TryCreate(endpoint, UriKind.Absolute, out var absolute) ? absolute : new Uri(GraphRoot + endpoint);
        if (!uri.AbsoluteUri.StartsWith(GraphRoot, StringComparison.Ordinal))
            throw new InvalidDataException("Indirizzo Microsoft Graph non valido.");

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new GraphRequestException(response.StatusCode, response.Headers.RetryAfter?.Delta,
                response.Headers.RetryAfter?.Date);
    }

    internal static string EncodeShareLink(string address) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(address))
            .TrimEnd('=').Replace('/', '_').Replace('+', '-');

    private static string BuildOwnerEndpoint(string address)
    {
        var segments = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "me/drive/root" :
            $"me/drive/root:/{string.Join("/", segments.Select(Uri.EscapeDataString))}:";
    }

    private static string ItemEndpoint(string driveId, string itemId) =>
        $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}";

    internal static bool IsPhoto(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".heif";

    private static JsonElement EffectiveItem(JsonElement item) =>
        HasObject(item, "remoteItem") ? item.GetProperty("remoteItem") : item;

    private static (string DriveId, string ItemId) ReadIdentity(JsonElement item, string? defaultDrive = null) =>
        (GetString(GetObject(item, "parentReference"), "driveId") ?? defaultDrive
            ?? throw new InvalidDataException("La risposta Graph non identifica il drive."),
        GetString(item, "id") ?? throw new InvalidDataException("La risposta Graph non identifica l’elemento."));

    private static bool HasObject(JsonElement element, string key) => GetObject(element, key).ValueKind == JsonValueKind.Object;

    private static JsonElement GetObject(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;

    private static string? GetString(JsonElement element, string key) =>
        GetObject(element, key) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long GetNumber(JsonElement element, string key) =>
        GetObject(element, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long number) ? number : 0;
}

internal sealed class GraphRequestException(HttpStatusCode status, TimeSpan? delta, DateTimeOffset? date) :
    Exception($"Microsoft Graph HTTP {(int)status}")
{
    internal HttpStatusCode Status { get; } = status;
    internal DateTimeOffset? RetryAt { get; } = date ?? (delta is null ? null : DateTimeOffset.UtcNow + delta.Value);

    internal string DisplayMessage => Status switch
    {
        HttpStatusCode.Unauthorized => "Sessione scaduta. Accedi di nuovo a Microsoft.",
        HttpStatusCode.Forbidden => "Accesso negato. Verifica account, permessi e condivisione della cartella.",
        HttpStatusCode.NotFound => "Cartella o link non trovato.",
        HttpStatusCode.TooManyRequests => "OneDrive ha chiesto di rallentare. Riprovo automaticamente.",
        HttpStatusCode.BadRequest => "OneDrive ha rifiutato il link o la richiesta.",
        _ => $"OneDrive ha risposto con errore HTTP {(int)Status}."
    };
}
