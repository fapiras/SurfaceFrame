using System.Net;
using System.Net.Http;
using System.Text;

namespace SurfaceFrame.Verification;

public static class CoreChecks
{
    public static async Task<int> RunAsync()
    {
        Check(FolderSource.IsValid("/Foto 2026/Caffè"), "percorso proprietario");
        Check(!FolderSource.IsValid("/Foto/../altro"), "percorso con traversamento");
        Check(FolderSource.IsValid("https://1drv.ms/f/s!example?e=test"), "link OneDrive");
        Check(!FolderSource.IsValid("https://1drv.ms.example.org/f/example"), "dominio impostore");
        Check(FrameSettings.ParseDashboardUrl("http://192.0.2.1:8123/plancia/casa") is not null,
            "URL Home Assistant locale");
        Check(FrameSettings.ParseDashboardUrl("https://name:secret@example.org") is null, "credenziali in URL");
        var defaults = new FrameSettings();
        Check(defaults.SlideSeconds == 30 && defaults.InactivitySeconds == 120 && !defaults.AutoStart, "predefiniti");
        Check(OneDrive.EncodeShareLink("https://1drv.ms/f/example") == "u!aHR0cHM6Ly8xZHJ2Lm1zL2YvZXhhbXBsZQ",
            "codifica condivisione");
        Check(new PhotoItem("A", "B", "Foto.jpg", "rev", 1, 1600, 800, 1).AspectFromMetadata() == 2,
            "proporzioni Graph note");
        Check(new PhotoItem("A", "B", "Foto.jpg", "rev", 1, 1600, 800, 6).AspectFromMetadata() is null,
            "metadati ruotati da verificare");
        CheckRotation();
        await CheckGraphAsync();
        Console.WriteLine("OK: impostazioni, metadati, 40 cicli casuali, aggiunta/rimozione, Graph paginato e streaming simulati.");
        return 0;
    }

    private static void CheckRotation()
    {
        var items = Enumerable.Range(1, 8).Select(n =>
            new PhotoItem("D", n.ToString(), $"{n:00}.jpg", "rev", 10, 20, 10, 1)).ToArray();
        var rotation = new PhotoRotation();
        rotation.Update(items, randomOrder: true);
        string? previous = null;
        for (int cycle = 0; cycle < 40; cycle++)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < items.Length; i++)
            {
                string next = rotation.Next()!.Key;
                Check(next != previous && seen.Add(next), "ciclo casuale senza ripetizioni");
                previous = next;
                rotation.Update(items, randomOrder: true);
            }
        }

        var ordered = new PhotoRotation();
        ordered.Update(items, randomOrder: false);
        Check(ordered.Next() == items[0], "ordine alfabetico");
        var added = new PhotoItem("D", "0", "00.webp", "rev", 10, 20, 10, 1);
        ordered.Update(items.Where(i => i != items[1]).Append(added).ToArray(), randomOrder: false);
        Check(ordered.Next() == added && ordered.Next() == items[2], "aggiunta, rimozione e ciclo in corso");
        ordered.Update([], randomOrder: false);
        Check(ordered.Next() is null, "catalogo svuotato");
    }

    private static async Task CheckGraphAsync()
    {
        const string root = """{"id":"root","folder":{},"parentReference":{"driveId":"D"}}""";
        const string content = """{"id":"p1","name":"Photo.jpg","eTag":"r1","size":3,"file":{},"image":{"width":2000,"height":1000},"photo":{"orientation":1}}""";
        var requests = new List<string>();
        using var graph = new HttpClient(new FakeHandler(request =>
        {
            Check(request.Headers.Authorization?.Parameter == "fake", "token Graph");
            string address = request.RequestUri!.AbsolutePath;
            requests.Add(address);
            if (address == "/v1.0/me/drive/root:/Foto%20A:/")
                throw new Exception("Separatore inatteso nel percorso.");
            if (address == "/v1.0/me/drive/root:/Foto%20A:")
                return Json(root);
            if (address.StartsWith("/v1.0/shares/u!", StringComparison.Ordinal))
            {
                Check(request.Headers.GetValues("Prefer").Single() == "redeemSharingLink", "riscatto esplicito");
                return Json(root);
            }
            if (address == "/v1.0/drives/D/items/root/children")
            {
                if (request.RequestUri.Query == "?page=2")
                    return Json("""{"value":[{"id":"p2","name":"two.webp","file":{}}]}""");
                return Json($$$"""{"value":[{{{content}}},{"id":"sub","name":"Sub","folder":{}}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/D/items/root/children?page=2"}""");
            }
            if (address == "/v1.0/drives/D/items/sub/children")
                return Json("""{"value":[{"id":"p3","name":"three.png","file":{}},{"id":"t","name":"notes.txt","file":{}}]}""");
            if (address == "/v1.0/drives/D/items/p1/content")
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://media.example.org/photo") } };
            throw new InvalidOperationException("Endpoint Graph imprevisto " + address);
        }));
        using var media = new HttpClient(new FakeHandler(request =>
        {
            Check(request.Headers.Authorization is null, "URL preautenticato senza bearer");
            Check(request.RequestUri!.Host == "media.example.org", "destinazione CDN");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) };
        }));
        var drive = new OneDrive(graph, media);
        var owner = new FolderSource { Address = "/Foto A", IncludeSubfolders = true };
        var photos = await drive.ListAsync(owner, "fake", CancellationToken.None);
        Check(photos.Select(item => item.ItemId).SequenceEqual(["p1", "p2", "p3"]), "paginazione e sottocartella");
        Check(photos[0].AspectFromMetadata() == 2, "facets orientamento e dimensioni");
        using var data = await drive.GetOriginalAsync(photos[0], "fake", CancellationToken.None);
        Check(data.Position == 0 && data.ToArray().SequenceEqual(new byte[] { 4, 5, 6 }), "contenuto in RAM");
        requests.Clear();
        var link = new FolderSource { Address = "https://1drv.ms/f/example", IncludeSubfolders = false };
        var directPhotos = await drive.ListAsync(link, "fake", CancellationToken.None);
        Check(directPhotos.Count == 2 && requests.Count == 3, "link e opzione sottocartelle");

        using var throttled = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) }
        }));
        try
        {
            await new OneDrive(throttled, media).ListAsync(owner, "fake", CancellationToken.None);
            throw new InvalidOperationException("Manca la propagazione di HTTP 429.");
        }
        catch (GraphRequestException error)
        {
            Check(error.Status == HttpStatusCode.TooManyRequests && error.RetryAt > DateTimeOffset.UtcNow.AddSeconds(50),
                "rispetto di Retry-After");
        }
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Prova non riuscita: " + name);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }
}
