using System.Security.Cryptography;
using System.Text;

namespace Autopilot.Web;

/// <summary>
/// The access rules of the UI. By default the protection is invisible: nothing to sign in to, but only the host
/// names localhost, 127.0.0.1 and [::1] are served (this stops DNS rebinding), and every change (POST, PUT, DELETE)
/// needs a custom header and an Origin that matches the Host (this stops other web pages).
/// With <c>ui.requireKey</c> (or a bind address beyond loopback) a persistent key is also needed: the link
/// <c>/?key=...</c> sets a long-lived cookie, and rotating the key signs every browser out.
/// </summary>
public sealed class SessionGuard
{
    public const string CookieName = "ap_session";
    public const string RequestHeader = "X-Autopilot-Request";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);
    private static readonly TimeSpan KeyRecheck = TimeSpan.FromSeconds(5);

    private readonly Func<string?>? _keySource;
    private readonly bool _anyHost;
    private string? _key;
    private DateTimeOffset _keyRead = DateTimeOffset.MinValue;

    /// <param name="keySource">Gives the current key; null means no key is required. Read again every few seconds so a rotated key takes effect.</param>
    /// <param name="anyHost">True when the UI is bound beyond loopback: host names cannot be limited, the key is the protection.</param>
    public SessionGuard(Func<string?>? keySource = null, bool anyHost = false)
    {
        _keySource = keySource;
        _anyHost = anyHost;
    }

    public bool RequiresKey => _keySource is not null;

    /// <summary>Cookies are shared by every port of a host, so each UI gets its own name; two UIs on one machine would sign each other out.</summary>
    public static string CookieNameFor(HostString host) => host.Port is { } port ? $"{CookieName}_{port}" : CookieName;

    /// <summary>The cookie value for a key. It is derived, so changing the key invalidates every cookie.</summary>
    public static string SessionFor(string key) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), "autopilot-ui-session"u8));

    private string? CurrentKey()
    {
        if (_keySource is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_key is null || now - _keyRead > KeyRecheck)
        {
            _key = _keySource();
            _keyRead = now;
        }

        return _key;
    }

    public bool KeyMatches(string? offered) =>
        CurrentKey() is { } key && offered is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(offered));

    public bool HasSession(HttpRequest request) =>
        CurrentKey() is { } key
        && request.Cookies.TryGetValue(CookieNameFor(request.Host), out var value) && value is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(SessionFor(key)), Encoding.UTF8.GetBytes(value));

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
            if (!_anyHost && !IsLocalHost(context.Request.Host))
            {
                await Deny(context, StatusCodes.Status400BadRequest, "Only localhost is served.").ConfigureAwait(false);
                return;
            }

            if (RequiresKey && context.Request.Path == "/" && context.Request.Query.TryGetValue("key", out var offered))
            {
                if (KeyMatches(offered.ToString()))
                {
                    context.Response.Cookies.Append(CookieNameFor(context.Request.Host), SessionFor(CurrentKey()!), new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        IsEssential = true,
                        MaxAge = CookieLifetime,
                    });
                    context.Response.Redirect("/");
                    return;
                }

                await Deny(context, StatusCodes.Status403Forbidden,
                    "This sign-in link is wrong or out of date. Run 'autopilot ui link' for the current one.").ConfigureAwait(false);
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (RequiresKey && !HasSession(context.Request))
                {
                    await Deny(context, StatusCodes.Status401Unauthorized, "Sign in with the link from 'autopilot ui link'.").ConfigureAwait(false);
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
