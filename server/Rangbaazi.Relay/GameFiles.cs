using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Rangbaazi.Relay;

/// <summary>
/// Optionally serves the game's web build from the same address as the relay (WEB_ROOT), so a
/// single server can host the whole web game: the page at /, the relay at /relay/. Used while the
/// AWS account can't use CloudFront yet; behind CloudFront the files come from S3 instead.
/// The Godot web export is large (a ~40 MB .wasm), so the deploy writes a gzip copy of the big
/// files next to them (name + ".gz"), served to browsers that accept gzip.
/// </summary>
internal static class GameFiles
{
    private const string OriginalPathKey = "rangbaazi.original-path";

    public static void Use(WebApplication app, string root)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(root));
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".pck"] = "application/octet-stream";

        // "/" -> "/index.html" first, so the rewrite below sees the real file name.
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });

        app.Use((HttpContext context, RequestDelegate next) =>
        {
            string? path = context.Request.Path.Value;
            if (path is { Length: > 1 }
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && !context.Request.Path.StartsWithSegments("/relay")
                && AcceptsGzip(context.Request)
                && types.TryGetContentType(path, out _)
                && files.GetFileInfo(path + ".gz").Exists)
            {
                context.Items[OriginalPathKey] = path;
                context.Request.Path = path + ".gz";
            }
            return next(context);
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
            OnPrepareResponse = file =>
            {
                HttpResponse response = file.Context.Response;
                if (file.Context.Items[OriginalPathKey] is string original)
                {
                    response.Headers.ContentEncoding = "gzip";
                    response.ContentType = types.TryGetContentType(original, out string? type) ? type : "application/octet-stream";
                    response.Headers.Vary = "Accept-Encoding";
                }
                // The page must always be fresh (it names the other files); the rest may be cached briefly.
                response.Headers.CacheControl = file.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    ? "no-cache"
                    : "public, max-age=300";
                response.Headers.XContentTypeOptions = "nosniff";
            },
        });
    }

    private static bool AcceptsGzip(HttpRequest request)
    {
        foreach (string? value in request.Headers.AcceptEncoding)
        {
            if (value is null) continue;
            foreach (string part in value.Split(','))
            {
                string coding = part.Trim();
                int semicolon = coding.IndexOf(';');
                string name = semicolon < 0 ? coding : coding[..semicolon].Trim();
                bool refused = semicolon >= 0 && coding[(semicolon + 1)..].Replace(" ", "", StringComparison.Ordinal) is "q=0" or "q=0.0" or "q=0.00" or "q=0.000";
                if (name.Equals("gzip", StringComparison.OrdinalIgnoreCase) && !refused) return true;
            }
        }
        return false;
    }

    /// <summary>For RelayApp: whether serving is possible (logs a warning when WEB_ROOT is set but missing).</summary>
    public static bool CanServe(string root) => Directory.Exists(root);

}
