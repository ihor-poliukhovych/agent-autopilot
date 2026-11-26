using System.Reflection;
using Microsoft.AspNetCore.StaticFiles;

namespace DevAutopilot.Web;

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

            context.Response.ContentType = type;
            context.Response.Headers.CacheControl = "no-cache";
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
