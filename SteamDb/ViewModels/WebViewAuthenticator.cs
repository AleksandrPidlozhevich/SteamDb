using Avalonia.Controls;
using SteamDb.Services;
using System;
using System.Threading.Tasks;

namespace SteamDb.ViewModels;

/// <summary>
/// <see cref="IWebAuthenticator"/> backed by Avalonia's <see cref="WebAuthenticationBroker"/>: it
/// shows the login page in an embedded WebView and resolves when navigation reaches the redirect
/// URL. The session is persistent (cookies + cache kept) so the window opens faster and a repeat
/// connect is near-instant — the user stays signed in and is redirected straight through.
/// </summary>
public sealed class WebViewAuthenticator : IWebAuthenticator
{
    private readonly Func<TopLevel?> _topLevel;

    public WebViewAuthenticator(Func<TopLevel?> topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<string?> AuthenticateAsync(Uri requestUri, Uri redirectUri)
    {
        var topLevel = _topLevel()
                       ?? throw new InvalidOperationException("No window available for web authentication.");

        try
        {
            // NonPersistent = false: reuse the WebView2 profile/cache across opens for speed and
            // keep the user signed in, so a repeat connect redirects straight through.
            var result = await WebAuthenticationBroker.AuthenticateAsync(
                topLevel,
                new WebAuthenticatorOptions(requestUri, redirectUri) { NonPersistent = false });

            // The code/token lives in the redirect URL (query for GOG, fragment for Xbox); the
            // store's parser pulls it out.
            return result.CallbackUri?.ToString();
        }
        catch (OperationCanceledException)
        {
            // The user closed the login window before it reached the redirect — not an error.
            return null;
        }
    }
}