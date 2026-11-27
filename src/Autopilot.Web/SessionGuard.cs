using System.Security.Cryptography;
using System.Text;

namespace Autopilot.Web;

/// <summary>
/// The access rules of the UI. A one-time key in the first URL becomes a session cookie; the API needs that
/// cookie; every change (POST, PUT, DELETE) also needs a custom header and an Origin that matches the Host.
/// Only the host names localhost, 127.0.0.1 and [::1] are served, which stops DNS rebinding.
/// </summary>
public sealed class SessionGuard
{
    public const string CookieName = "ap_session";

    /// <summary>Cookies are shared by every port of a host, so each UI gets its own name; two UIs on one machine would sign each other out.</summary>
    public static string CookieNameFor(HostString host) => host.Port is { } port ? $"{CookieName}_{port}" : CookieName;
    public const string RequestHeader = "X-Autopilot-Request";

    private readonly byte[] _key;
    private readonly string _session = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private int _keyUsed;

    public SessionGuard(string? key = null)
    {
        Key = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _key = Encoding.UTF8.GetBytes(Key);
    }

    /// <summary>The one-time key printed in the start URL.</summary>
    public string Key { get; }

    /// <summary>Uses the key. True only for the first correct key.</summary>
    public bool TryRedeem(string? offered)
    {
        if (offered is null || !CryptographicOperations.FixedTimeEquals(_key, Encoding.UTF8.GetBytes(offered)))
        {
            return false;
        }

        return Interlocked.Exchange(ref _keyUsed, 1) == 0;
    }

    public string SessionToken => _session;

    public bool HasSession(HttpRequest request) =>
        request.Cookies.TryGetValue(CookieNameFor(request.Host), out var value) && value is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_session), Encoding.UTF8.GetBytes(value));

    public static bool IsLocalHost(HostString host) =>
        host.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    public static bool IsChange(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    /// <summary>True when the Origin header names this very host. A missing Origin is refused.</summary>
    public static bool OriginMatches(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, "http", StringComparison.Ordinal)
            && string.Equals(uri.Authority, request.Host.ToUriComponent(), StringComparison.OrdinalIgnoreCase);
    }

    public void Use(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            if (!IsLocalHost(context.Request.Host))
            {
                await Deny(context, StatusCodes.Status400BadRequest, "Only localhost is served.").ConfigureAwait(false);
                return;
            }

            if (context.Request.Path == "/" && context.Request.Query.TryGetValue("key", out var offered))
            {
                if (TryRedeem(offered.ToString()))
                {
                    context.Response.Cookies.Append(CookieNameFor(context.Request.Host), _session, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        IsEssential = true,
                    });
                    context.Response.Redirect("/");
                    return;
                }

                await Deny(context, StatusCodes.Status403Forbidden,
                    "This link was already used or is wrong. Start 'autopilot ui' again for a new link.").ConfigureAwait(false);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!HasSession(context.Request))
                {
                    await Deny(context, StatusCodes.Status401Unauthorized, "Open the link printed by 'autopilot ui' to sign in.").ConfigureAwait(false);
                    return;
                }

                if (IsChange(context.Request.Method)
                    && (!context.Request.Headers.ContainsKey(RequestHeader) || !OriginMatches(context.Request)))
                {
                    await Deny(context, StatusCodes.Status403Forbidden, "The request did not come from the UI page.").ConfigureAwait(false);
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static async Task Deny(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = message }).ConfigureAwait(false);
    }
}
