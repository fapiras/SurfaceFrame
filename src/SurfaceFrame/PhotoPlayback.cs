using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Buffers.Binary;
using SkiaSharp;
using MediaPixelFormats = System.Windows.Media.PixelFormats;
using System.Net;

namespace SurfaceFrame;

internal sealed record DisplayPhoto(PhotoItem Item, BitmapSource Bitmap, bool FromCache = false);
internal sealed record PhotoCompatibility(int Compatible, int Pending);

internal sealed class PhotoPlayback(LocalStorage storage, MicrosoftSignIn signIn, OneDrive oneDrive, RcloneOneDrive rclone)
{
    private readonly Dictionary<Guid, IReadOnlyList<PhotoItem>> sourcePhotos = [];
    private readonly Dictionary<Guid, string> sourceMessages = [];
    private readonly Dictionary<string, double> verifiedRatios = new(StringComparer.Ordinal);
    private readonly PhotoRotation rotation = new();
    private IReadOnlyList<PhotoItem> catalog = [];
    private HashSet<string> revisions = new(StringComparer.Ordinal);
    internal DateTimeOffset? RetryAt { get; private set; }
    internal string Status { get; private set; } = "Configura una cartella OneDrive.";
    internal DateTimeOffset? LastRefresh { get; private set; }
    internal int CatalogCount => catalog.Count;
    internal bool Contains(PhotoItem item) => revisions.Contains(item.CacheKey);
    internal bool HasCandidates(double minimumRatio) =>
        catalog.Any(item => Aspect(item, minimumRatio) is not { } ratio || Matches(ratio, minimumRatio));
    internal bool RefreshFailed { get; private set; }

    internal void Reset()
    {
        sourcePhotos.Clear();
        sourceMessages.Clear();
        verifiedRatios.Clear();
        ReplaceCatalog(shuffle: false);
        RetryAt = null;
        LastRefresh = null;
        Status = "Aggiornamento del catalogo OneDrive…";
    }

    internal PhotoCompatibility Compatibility(Guid sourceId, double minimumRatio)
    {
        if (!sourcePhotos.TryGetValue(sourceId, out var photos))
            return new(0, 0);
        int compatible = 0;
        int pending = 0;
        foreach (var item in photos.DistinctBy(photo => photo.Key))
        {
            double? ratio = Aspect(item, minimumRatio);
            if (ratio is null)
                pending++;
            else if (Matches(ratio.Value, minimumRatio))
                compatible++;
        }
        return new(compatible, pending);
    }

    internal string? SourceStatus(Guid id) => sourceMessages.GetValueOrDefault(id);

    internal async Task RefreshAsync(FrameSettings settings, CancellationToken token)
    {
        RetryAt = null;
        RefreshFailed = false;
        var blocked = settings.Sources.Where(source =>
            source.Enabled && source.IsShareLink && (settings.UsesRclone || !settings.AllowSharedLinkScope)).ToArray();
        var active = settings.Sources.Where(source =>
            source.Enabled && (!source.IsShareLink || !settings.UsesRclone && settings.AllowSharedLinkScope)).ToArray();
        var activeIds = active.Select(source => source.Id).ToHashSet();
        foreach (Guid id in sourcePhotos.Keys.Concat(sourceMessages.Keys).Distinct().ToArray())
        {
            if (activeIds.Contains(id))
                continue;
            sourcePhotos.Remove(id);
            sourceMessages.Remove(id);
        }

        string consentRequired = settings.UsesRclone
            ? "Per OneDrive personale usa il percorso della cartella, per esempio /Foto/Cornice."
            : "Abilita esplicitamente il permesso per i link condivisi.";
        foreach (var source in blocked)
            sourceMessages[source.Id] = consentRequired;
        if (active.Length == 0)
        {
            ReplaceCatalog(settings.Shuffle);
            Status = blocked.Length > 0 ? consentRequired : "Configura una cartella OneDrive.";
            return;
        }

        string accessToken;
        try
        {
            accessToken = settings.UsesRclone ? "" : await signIn.GetTokenAsync(settings, token);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            RefreshFailed = true;
            Status = Describe(error);
            foreach (var source in active)
                sourceMessages[source.Id] = Status;
            storage.Log("Autenticazione OneDrive", error);
            ReplaceCatalog(settings.Shuffle);
            return;
        }

        int failures = 0;
        string? lastFailure = null;
        foreach (var source in active)
        {
            if (RetryAt > DateTimeOffset.UtcNow)
            {
                sourceMessages[source.Id] = "Aggiornamento rinviato da OneDrive.";
                failures++;
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                var items = settings.UsesRclone
                    ? await rclone.ListAsync(source, timeout.Token)
                    : await oneDrive.ListAsync(source, accessToken, timeout.Token);
                token.ThrowIfCancellationRequested();
                sourcePhotos[source.Id] = items;
                sourceMessages[source.Id] = "Aggiornata";
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                failures++;
                lastFailure = Describe(error);
                sourceMessages[source.Id] = lastFailure;
                if (error is RcloneMissingException)
                    sourcePhotos.Remove(source.Id);
                if (error is GraphRequestException graphError)
                {
                    if (graphError.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                        sourcePhotos.Remove(source.Id);
                    if (graphError.Status == HttpStatusCode.TooManyRequests)
                        RetryAt = graphError.RetryAt ?? DateTimeOffset.UtcNow.AddMinutes(1);
                }
                storage.Log("Aggiornamento cartella OneDrive", error);
            }
        }

        ReplaceCatalog(settings.Shuffle);
        RefreshFailed = failures > 0;
        if (failures < active.Length)
            LastRefresh = DateTimeOffset.Now;
        Status = failures switch
        {
            0 => $"OneDrive connesso · {catalog.Count} file immagine",
            _ when failures == active.Length => lastFailure ?? "Aggiornamento OneDrive rinviato.",
            _ => $"OneDrive · {active.Length - failures}/{active.Length} cartelle aggiornate. {lastFailure}"
        };
    }

    internal async Task<DisplayPhoto?> PrepareNextAsync(FrameSettings settings, int pixelWidth,
        int pixelHeight, PhotoItem? previous, CancellationToken cancellationToken)
    {
        if (!HasCandidates(settings.PhotoRatioFilter))
            return null;

        string accessToken = settings.UsesRclone ? "" : await signIn.GetTokenAsync(settings, cancellationToken);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        // Limit a preloading pass so very large albums remain responsive while scanning unknown orientations.
        int limit = Math.Min(catalog.Count, 20);
        for (int index = 0; index < limit; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = rotation.Next();
            if (item is null || !attempted.Add(item.Key))
                break;
            if (previous?.CacheKey == item.CacheKey || Aspect(item, settings.PhotoRatioFilter) is { } ratio &&
                !Matches(ratio, settings.PhotoRatioFilter))
                continue;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(22));
            try
            {
                var frame = await DecodeAsync(item, accessToken, settings.PhotoRatioFilter,
                    pixelWidth, pixelHeight, timeout.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (Contains(item))
                    verifiedRatios[item.CacheKey] = frame.Ratio;
                if (frame.Bitmap is not null && Contains(item))
                    return new DisplayPhoto(item, frame.Bitmap);
            }
            catch (Exception error) when (error is RcloneMissingException or
                GraphRequestException { Status: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
            {
                storage.Log("Foto non accessibile", error);
            }
            catch (Exception error) when (error is InvalidDataException
                or NotSupportedException)
            {
                if (Contains(item))
                    verifiedRatios[item.CacheKey] = 0;
                storage.Log("Foto non compatibile", error);
            }
        }
        return null;
    }

    private async Task<DecodedPhoto> DecodeAsync(PhotoItem item, string token, double ratio, int width, int height,
        CancellationToken cancellationToken)
    {
        if (item.RclonePath is null && item.Bytes >= OneDrive.ThumbnailThreshold &&
            (item.IsHeic || item.AspectFromMetadata() is not null || item.AspectFromThumbnail(ratio) is not null))
        {
            try
            {
                using var thumbnail = await oneDrive.GetThumbnailAsync(item, token, cancellationToken);
                if (thumbnail is not null)
                {
                    var frame = await Task.Run(() => PhotoDecoder.Decode(thumbnail, ratio, width, height,
                        alreadyOriented: true, cancellationToken), cancellationToken);
                    if (frame.Bitmap is not null)
                        return frame;
                }
            }
            catch (InvalidDataException error)
            {
                storage.Log("Anteprima OneDrive scartata", error);
            }
        }

        using var original = item.RclonePath is null
            ? await oneDrive.GetOriginalAsync(item, token, cancellationToken)
            : await rclone.GetOriginalAsync(item, cancellationToken);
        return await Task.Run(() => PhotoDecoder.Decode(original, ratio, width, height,
            alreadyOriented: false, cancellationToken), cancellationToken);
    }

    private double? Aspect(PhotoItem item, double threshold) => verifiedRatios.TryGetValue(item.CacheKey, out double ratio)
        ? ratio : item.AspectFromThumbnail(threshold) ?? item.AspectFromMetadata();

    internal static bool Matches(double ratio, double threshold) => threshold == 0 ? ratio > 0 : ratio > 1 && ratio >= threshold;

    private void ReplaceCatalog(bool shuffle)
    {
        catalog = sourcePhotos.Values.SelectMany(items => items).DistinctBy(item => item.Key).ToArray();
        revisions = catalog.Select(item => item.CacheKey).ToHashSet(StringComparer.Ordinal);
        foreach (string obsolete in verifiedRatios.Keys.Where(key => !revisions.Contains(key)).ToArray())
            verifiedRatios.Remove(obsolete);
        rotation.Update(catalog, shuffle);
    }

    internal static string Describe(Exception error) => error switch
    {
        SignInRequiredException signInError => signInError.Message,
        RcloneException rcloneError => rcloneError.Message,
        GraphRequestException graphError => graphError.DisplayMessage,
        InvalidDataException invalid => invalid.Message,
        Microsoft.Identity.Client.MsalException msal =>
            $"Accesso Microsoft non riuscito ({msal.ErrorCode}). Controlla account e registrazione dell’app.",
        HttpRequestException or OperationCanceledException => "OneDrive non raggiungibile. Riprovo automaticamente.",
        _ => "Impossibile leggere OneDrive. Consulta la diagnostica."
    };
}

internal sealed record DecodedPhoto(double Ratio, BitmapSource? Bitmap);

internal static class PhotoDecoder
{
    internal static DecodedPhoto Decode(Stream input, double minimumRatio, int screenWidth, int screenHeight,
        bool alreadyOriented, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var embeddedOrigin = alreadyOriented ? SKEncodedOrigin.TopLeft : ReadContainerOrigin(input);
        using var stream = new SKManagedStream(input, disposeManagedStream: false);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Formato della foto non riconosciuto.");
        var info = codec.Info;
        var orientation = alreadyOriented ? SKEncodedOrigin.TopLeft :
            codec.EncodedOrigin == SKEncodedOrigin.TopLeft ? embeddedOrigin : codec.EncodedOrigin;
        bool swap = orientation is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? info.Height : info.Width;
        int height = swap ? info.Width : info.Height;
        double ratio = (double)width / height;
        if (!PhotoPlayback.Matches(ratio, minimumRatio))
            return new DecodedPhoto(ratio, null);
        if ((long)width * height > 60_000_000)
            throw new InvalidDataException("Foto oltre 60 megapixel dopo il tentativo di usare un’anteprima.");

        int targetEdge = Math.Clamp((int)Math.Ceiling(Math.Max(screenWidth, screenHeight * ratio)), 1, 4096);
        float scale = Math.Min(1f, (float)targetEdge / Math.Max(width, height));
        var sampled = codec.GetScaledDimensions(scale);
        var decodeInfo = new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var image = new SKBitmap(decodeInfo);
        if (codec.GetPixels(decodeInfo, image.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("Foto incompleta o illeggibile.");
        token.ThrowIfCancellationRequested();

        int w = Math.Max(1, (int)Math.Round(info.Width * scale));
        int h = Math.Max(1, (int)Math.Round(info.Height * scale));
        using var rotated = new SKBitmap(swap ? h : w, swap ? w : h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(orientation switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity
        });
        using var source = SKImage.FromBitmap(image);
        canvas.DrawImage(source, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        var result = BitmapSource.Create(rotated.Width, rotated.Height, 96, 96, MediaPixelFormats.Pbgra32,
            null, rotated.GetPixels(), checked(rotated.RowBytes * rotated.Height), rotated.RowBytes);
        result.Freeze();
        return new DecodedPhoto(ratio, result);
    }

    // Skia does not expose EXIF orientation for every PNG or WebP variant.
    private static SKEncodedOrigin ReadContainerOrigin(Stream input)
    {
        if (!input.CanSeek || input.Length - input.Position < 12)
            return SKEncodedOrigin.TopLeft;
        long start = input.Position;
        try
        {
            Span<byte> header = stackalloc byte[12];
            input.ReadExactly(header);
            bool png = header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            bool webp = header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8);
            if (!png && !webp)
                return SKEncodedOrigin.TopLeft;

            input.Position = start + (png ? 8 : 12);
            while (input.Length - input.Position >= 8)
            {
                input.ReadExactly(header[..8]);
                uint length = png ? BinaryPrimitives.ReadUInt32BigEndian(header) : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                var tag = png ? header[4..8] : header[..4];
                long next = input.Position + length + (png ? 4 : length % 2);
                if (next > input.Length || tag.SequenceEqual("IEND"u8))
                    break;
                if (tag.SequenceEqual(png ? "eXIf"u8 : "EXIF"u8) && length is >= 14 and <= 1024 * 1024)
                {
                    byte[] exif = new byte[length];
                    input.ReadExactly(exif);
                    return ReadTiffOrigin(exif);
                }
                input.Position = next;
            }
            return SKEncodedOrigin.TopLeft;
        }
        finally { input.Position = start; }
    }

    private static SKEncodedOrigin ReadTiffOrigin(ReadOnlySpan<byte> exif)
    {
        if (exif.StartsWith("Exif\0\0"u8))
            exif = exif[6..];
        if (exif.Length < 8)
            return SKEncodedOrigin.TopLeft;
        bool little = exif[..2].SequenceEqual("II"u8);
        if (!little && !exif[..2].SequenceEqual("MM"u8))
            return SKEncodedOrigin.TopLeft;

        static ushort Read16(ReadOnlySpan<byte> value, bool little) =>
            little ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);
        static uint Read32(ReadOnlySpan<byte> value, bool little) =>
            little ? BinaryPrimitives.ReadUInt32LittleEndian(value) : BinaryPrimitives.ReadUInt32BigEndian(value);

        uint offset = Read32(exif[4..], little);
        if (Read16(exif[2..], little) != 42 || offset < 8 || offset > exif.Length - 2)
            return SKEncodedOrigin.TopLeft;
        exif = exif[(int)offset..];
        int count = Math.Min(Read16(exif, little), (exif.Length - 2) / 12);
        for (int i = 0; i < count; ++i)
        {
            var field = exif.Slice(2 + 12 * i, 12);
            if (Read16(field, little) != 274 || Read16(field[2..], little) != 3 || Read32(field[4..], little) != 1)
                continue;
            ushort orientation = Read16(field[8..], little);
            if (orientation is >= 1 and <= 8)
                return (SKEncodedOrigin)orientation;
        }
        return SKEncodedOrigin.TopLeft;
    }
}
