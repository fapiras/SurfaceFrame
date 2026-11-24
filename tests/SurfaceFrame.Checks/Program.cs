using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SurfaceFrame.Verification;

namespace SurfaceFrame;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        CoreChecks.RunAsync().GetAwaiter().GetResult();
        CheckUpdates();
        CheckOneDriveOptions();
        CheckImage(120, 80, 1, ".jpg", true);
        CheckImage(80, 120, 6, ".jpg", true);
        CheckImage(120, 80, 8, ".jpg", false);
        CheckImage(80, 80, 1, ".jpg", false);
        CheckImage(120, 80, 1, ".png", true);
        CheckImage(80, 120, 6, ".png", true);
        CheckImage(120, 80, 1, ".webp", true);
        CheckImage(120, 80, 8, ".webp", false);
        CheckImage(120, 80, 1, ".jpg", false, 1.6);
        Console.WriteLine("OK: decodifica JPG, PNG, WebP e orientamento EXIF, soglia e immagini quadrate.");
        return 0;
    }

    private static void CheckOneDriveOptions()
    {
        if (!new FrameSettings().UsesRclone ||
            new FrameSettings { MicrosoftClientId = Guid.NewGuid().ToString() }.UsesRclone ||
            !new FrameSettings { MicrosoftClientId = Guid.NewGuid().ToString(), UseRclone = true }.UsesRclone)
            throw new InvalidOperationException("Modalità OneDrive predefinita o migrazione impostazioni errata.");
        using var options = JsonDocument.Parse("""
            {"Examples":[{"Value":"azienda","Help":"OneDrive (business)"},{"Value":"foto","Help":"OneDrive (personal)"}]}
            """);
        using var businessOnly = JsonDocument.Parse("""
            {"Examples":[{"Value":"azienda","Help":"OneDrive (business)"}]}
            """);
        if (RcloneOneDrive.FindPersonalDrive(options.RootElement) != "foto" ||
            RcloneOneDrive.FindPersonalDrive(businessOnly.RootElement) != "")
            throw new InvalidOperationException("Selezione del OneDrive personale errata.");
        Console.WriteLine("OK: selezione OneDrive personale e compatibilità delle impostazioni.");
    }

    private static void CheckImage(int width, int height, ushort orientation, string extension,
        bool expectLandscape, double threshold = 1)
    {
        using var data = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{width}x{height}-{orientation}{extension}"));
        var result = PhotoDecoder.Decode(data, threshold, 2736, 1824, false, CancellationToken.None);
        bool visible = result.Bitmap is not null;
        if (visible != expectLandscape || visible &&
            (result.Bitmap!.PixelWidth <= result.Bitmap.PixelHeight || !result.Bitmap.IsFrozen))
            throw new InvalidOperationException($"Filtro errato per {extension} {width}x{height} EXIF {orientation}.");
    }

    private static void CheckUpdates()
    {
        byte[] data = [1, 2, 3, 4];
        byte[] digest = SHA256.HashData(data);
        var version = new Version(1, 0, 3);
        byte[] bytes = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"sha256\":\"{Convert.ToHexString(digest)}\"}}");
        using var rsa = RSA.Create(2048);
        string pem = rsa.ExportSubjectPublicKeyInfoPem();
        byte[] signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var manifest = UpdateService.VerifyManifest(pem, bytes, signature, version);
        UpdateService.CheckHash(manifest, digest);
        Reject(() => UpdateService.VerifyManifest(pem, bytes, signature, new Version(1, 0, 4)));
        Reject(() => UpdateService.CheckHash(manifest, SHA256.HashData([4, 3, 2, 1])));
        bytes[0] ^= 1;
        Reject(() => UpdateService.VerifyManifest(pem, bytes, signature, version));
        if (UpdateService.TryVersion("v1.0.3", out _) || UpdateService.TryVersion("1.0.3.0", out _))
            throw new InvalidOperationException("Versione di aggiornamento non valida accettata.");
        Console.WriteLine("OK: firma, integrità del pacchetto e corrispondenza della versione.");
    }

    private static void Reject(Action test)
    {
        try { test(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Un aggiornamento non valido è stato accettato.");
    }
}
