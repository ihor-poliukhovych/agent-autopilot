using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace Autopilot.Web;

/// <summary>Serves the pages that are compiled into the assembly (<c>wwwroot/</c>), so the tool is one self-contained package.</summary>
public static class EmbeddedSite
{
    private const string Prefix = "wwwroot/";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void Use(WebApplication app)
    {
        var assembly = typeof(EmbeddedSite).Assembly;
        // The build writes the folder separator of the build machine into the names; the URLs use '/'.
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.Ordinal);
        var etags = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        app.Use(async (context, next) =>
        {
            if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) || context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Path.Value ?? "/";
            var name = Prefix + (path == "/" ? "index.html" : path.TrimStart('/'));
            if (path.Contains("..", StringComparison.Ordinal) || !names.TryGetValue(name, out var resource))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (!ContentTypes.TryGetContentType(name, out var type))
            {
                type = "application/octet-stream";
            }

            // The files cannot change while the program runs, so one hash per file is enough. The browser keeps
            // its copy and asks "still the same?" (a 304 with no body) instead of downloading again.
            var etag = etags.GetOrAdd(resource!, r =>
            {
                using var s = assembly.GetManifestResourceStream(r)!;
                return "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s))[..16] + "\"";
            });
            context.Response.ContentType = type;
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = etag;
            if (context.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            await using var stream = assembly.GetManifestResourceStream(resource!)!;
            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.ContentLength = stream.Length;
                return;
            }

            await stream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        });
    }
}
