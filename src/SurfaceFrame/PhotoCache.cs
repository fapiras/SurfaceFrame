using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace SurfaceFrame;

internal sealed class PhotoCache(LocalStorage storage)
{
    private const int MaximumCount = 48;
    private const long MaximumBytes = 96L * 1024 * 1024;
    private readonly object sync = new();
    private string Root => Path.Combine(storage.Root, "photo-cache");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Prefix(FrameSettings settings) => Hash(JsonSerializer.Serialize(new
    {
        settings.UsesRclone, settings.MicrosoftClientId, settings.AllowSharedLinkScope, settings.PhotoRatioFilter,
        Sources = settings.Sources.Where(source => source.Enabled)
            .Select(source => new { source.Address, source.IncludeSubfolders })
            .OrderBy(source => source.Address, StringComparer.Ordinal)
    }));

    internal void Remember(FrameSettings settings, DisplayPhoto photo, CancellationToken token)
    {
        if (!settings.CachePhotos)
            return;
        lock (sync)
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                Directory.CreateDirectory(Root);
                string file = Path.Combine(Root, Prefix(settings) + "-" + Hash(photo.Item.CacheKey) + ".photo");
                string staging = file + ".tmp";
                try
                {
                    using (var writer = new BinaryWriter(File.Create(staging)))
                    {
                        writer.Write(JsonSerializer.Serialize(photo.Item));
                        var encoder = new JpegBitmapEncoder { QualityLevel = 87 };
                        encoder.Frames.Add(BitmapFrame.Create(photo.Bitmap));
                        using var jpeg = new MemoryStream();
                        encoder.Save(jpeg);
                        jpeg.Position = 0;
                        jpeg.CopyTo(writer.BaseStream);
                    }
                    File.Move(staging, file, overwrite: true);
                }
                finally { File.Delete(staging); }

                long bytes = 0;
                int count = 0;
                foreach (var entry in new DirectoryInfo(Root).EnumerateFiles("*.photo")
                    .OrderByDescending(file => file.LastWriteTimeUtc))
                {
                    bytes += entry.Length;
                    if (++count > MaximumCount || bytes > MaximumBytes)
                        entry.Delete();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException
                or InvalidOperationException)
            {
                storage.Log("Salvataggio cache foto", error);
            }
        }
    }

    internal DisplayPhoto? Load(FrameSettings settings, PhotoItem? current, HashSet<string> seen, CancellationToken token)
    {
        if (!settings.CachePhotos)
            return null;
        lock (sync)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(Root))
                    return null;
                files = Directory.EnumerateFiles(Root, Prefix(settings) + "-*.photo").Order().ToArray();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                storage.Log("Accesso cache foto", error);
                return null;
            }
            if (files.Length > 0 && files.All(seen.Contains))
                seen.Clear();
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                if (seen.Contains(file))
                    continue;
                try
                {
                    using var reader = new BinaryReader(File.OpenRead(file));
                    var item = JsonSerializer.Deserialize<PhotoItem>(reader.ReadString());
                    seen.Add(file);
                    if (item is null || item.CacheKey == current?.CacheKey)
                        continue;

                    // WPF decoders expect the JPEG stream to start at byte zero.
                    using var jpeg = new MemoryStream();
                    reader.BaseStream.CopyTo(jpeg);
                    jpeg.Position = 0;
                    var bitmap = BitmapFrame.Create(jpeg, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bitmap.Freeze();
                    return new DisplayPhoto(item, bitmap, FromCache: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                    or NotSupportedException or FormatException or InvalidOperationException)
                {
                    storage.Log("Lettura cache foto", error);
                }
            }
            return null;
        }
    }

    internal void Prune(FrameSettings settings, HashSet<string> catalog, CancellationToken token)
    {
        lock (sync)
        {
            if (token.IsCancellationRequested)
                return;
            try
            {
                if (!Directory.Exists(Root))
                    return;
                string prefix = Prefix(settings) + "-";
                var keep = catalog.Select(key => prefix + Hash(key) + ".photo").ToHashSet(StringComparer.Ordinal);
                foreach (var file in new DirectoryInfo(Root).EnumerateFiles(prefix + "*.photo"))
                    if (!keep.Contains(file.Name))
                        file.Delete();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                storage.Log("Aggiornamento cache foto", error);
            }
        }
    }

    internal void Clear()
    {
        lock (sync)
        {
            try
            {
                if (Directory.Exists(Root))
                    foreach (var file in Directory.EnumerateFiles(Root, "*.photo"))
                        File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                storage.Log("Pulizia cache foto", error);
            }
        }
    }
}
