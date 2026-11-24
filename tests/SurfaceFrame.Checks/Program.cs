using System.IO;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
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
        CheckDashboardInactive();
        CheckNightSchedule();
        CheckImage(120, 80, 8, ".jpg", true, 0);
        CheckImage(80, 80, 1, ".jpg", true, 0);
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
        CheckPersonalDriveEndpointAsync().GetAwaiter().GetResult();
        Console.WriteLine("OK: lettura diretta di OneDrive personale e compatibilità delle impostazioni.");
    }

    private static async Task CheckPersonalDriveEndpointAsync()
    {
        using var graph = new HttpClient(new TestGraph("""{"id":"test-drive","driveType":"personal"}"""));
        if (await RcloneOneDrive.ResolvePersonalDriveAsync(graph, "test-token", CancellationToken.None) != "test-drive")
            throw new InvalidOperationException("Identificativo OneDrive perso.");
        using var business = new HttpClient(new TestGraph("""{"id":"other-drive","driveType":"business"}"""));
        try
        {
            await RcloneOneDrive.ResolvePersonalDriveAsync(business, "test-token", CancellationToken.None);
        }
        catch (RcloneException)
        {
            return;
        }
        throw new InvalidOperationException("OneDrive aziendale accettato.");
    }

    private sealed class TestGraph(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsoluteUri != "https://graph.microsoft.com/v1.0/me/drive?$select=id,driveType" ||
                request.Headers.Authorization?.ToString() != "Bearer test-token")
                throw new InvalidOperationException("Richiesta al drive Microsoft errata.");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private static void CheckDashboardInactive()
    {
        var host = new System.Windows.Controls.Grid();
        using var dashboard = new Dashboard(host, new LocalStorage());
        dashboard.Configure(new Uri("http://127.0.0.1:8123/"));
        dashboard.TimerTick();
        if (dashboard.CanRetry || !dashboard.RetryAsync().IsCompletedSuccessfully || host.Children.Count != 0)
            throw new InvalidOperationException("WebView2 è stato richiesto con Home Assistant chiuso.");
        dashboard.SetActive(true);
        if (!dashboard.CanRetry)
            throw new InvalidOperationException("WebView2 non disponibile in modalità Home Assistant.");
        dashboard.SetActive(false);
        if (dashboard.CanRetry)
            throw new InvalidOperationException("WebView2 richiesto al ritorno alla cornice.");
        Console.WriteLine("OK: Home Assistant non inizializza WebView2 in modalità cornice.");
    }

    private static void CheckNightSchedule()
    {
        var settings = new FrameSettings { NightEnabled = true };
        if (!settings.IsNight(new TimeOnly(23, 0)) || !settings.IsNight(new TimeOnly(0, 0)) ||
            settings.IsNight(new TimeOnly(7, 0)) || settings.IsNight(new TimeOnly(12, 0)))
            throw new InvalidOperationException("Orari notte dopo mezzanotte errati.");
        settings.NightStart = new TimeOnly(10, 0);
        settings.NightEnd = new TimeOnly(11, 0);
        if (!settings.IsNight(new TimeOnly(10, 30)) || settings.IsNight(new TimeOnly(0, 0)))
            throw new InvalidOperationException("Orari notte nello stesso giorno errati.");
        settings.NightEnabled = false;
        if (settings.IsNight(new TimeOnly(10, 30)))
            throw new InvalidOperationException("La modalità notte disattivata risulta attiva.");
    }

    private static void CheckImage(int width, int height, ushort orientation, string extension,
        bool expectLandscape, double threshold = 1)
    {
        using var data = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{width}x{height}-{orientation}{extension}"));
        var result = PhotoDecoder.Decode(data, threshold, 2736, 1824, false, CancellationToken.None);
        bool visible = result.Bitmap is not null;
        if (visible != expectLandscape || visible &&
            (threshold != 0 && result.Bitmap!.PixelWidth <= result.Bitmap.PixelHeight || !result.Bitmap!.IsFrozen))
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
