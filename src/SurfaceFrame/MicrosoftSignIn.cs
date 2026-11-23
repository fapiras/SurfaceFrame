using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace SurfaceFrame;

internal sealed class MicrosoftSignIn(string storageDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPublicClientApplication? application;
    private MsalCacheHelper? cache;
    private string? activeClientId;

    internal Task<string> ConnectAsync(FrameSettings settings, CancellationToken cancellationToken) =>
        GetTokenAsync(settings, interactive: true, cancellationToken);

    internal Task<string> GetTokenAsync(FrameSettings settings, CancellationToken cancellationToken) =>
        GetTokenAsync(settings, interactive: false, cancellationToken);

    private async Task<string> GetTokenAsync(FrameSettings settings, bool interactive, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(settings.MicrosoftClientId, out _))
            throw new SignInRequiredException("Inserisci l’ID applicazione Microsoft nelle impostazioni.");
        string[] scopes = settings.NeedsSharedScope && settings.AllowSharedLinkScope
            ? ["Files.ReadWrite"] : ["Files.Read"];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = await GetApplicationAsync(settings.MicrosoftClientId).ConfigureAwait(false);
            var accounts = (await client.GetAccountsAsync().ConfigureAwait(false)).ToArray();
            if (!interactive && accounts.Length == 1)
            {
                try
                {
                    var token = await client.AcquireTokenSilent(scopes, accounts[0])
                        .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    return token.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // The UI offers explicit sign-in when Microsoft requires interaction.
                }
            }

            if (!interactive)
                throw new SignInRequiredException("Apri Impostazioni e seleziona «Accedi a Microsoft».");

            var result = await client.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            foreach (var account in accounts.Where(account => account.HomeAccountId.Identifier !=
                result.Account.HomeAccountId.Identifier))
                await client.RemoveAsync(account).ConfigureAwait(false);
            return result.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task DisconnectAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(clientId, out _))
            return;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = await GetApplicationAsync(clientId).ConfigureAwait(false);
            foreach (var account in await client.GetAccountsAsync().ConfigureAwait(false))
                await client.RemoveAsync(account).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IPublicClientApplication> GetApplicationAsync(string clientId)
    {
        if (application is not null && clientId.Equals(activeClientId, StringComparison.OrdinalIgnoreCase))
            return application;

        string authDirectory = Path.Combine(storageDirectory, "auth");
        Directory.CreateDirectory(authDirectory);
        var nextApplication = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost")
            .Build();
        var properties = new StorageCreationPropertiesBuilder($"{Guid.Parse(clientId):N}.bin", authDirectory).Build();
        var nextCache = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
        nextCache.VerifyPersistence();
        nextCache.RegisterCache(nextApplication.UserTokenCache);

        if (application is not null)
            cache?.UnregisterCache(application.UserTokenCache);
        application = nextApplication;
        cache = nextCache;
        activeClientId = clientId;
        return nextApplication;
    }
}

internal sealed class SignInRequiredException(string message) : Exception(message);
