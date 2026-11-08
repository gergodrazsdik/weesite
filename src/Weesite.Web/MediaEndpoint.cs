using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Weesite.Content.Sources;

namespace Weesite.Web;

/// <summary>
/// Serves <c>/media/{hash}-{name}</c> from the media store. Names are content-hashed, so
/// responses are cached for a year as immutable. Every response says <c>nosniff</c>; only raster
/// images are shown inline, everything else (SVG included: it can carry script) downloads.
/// </summary>
internal static partial class MediaEndpoint
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif",
    };

    /// <summary>Maps the endpoint.</summary>
    /// <param name="app">The app.</param>
    public static void MapMedia(this IEndpointRouteBuilder app) =>
        app.MapGet("/media/{name}", ServeAsync);

    private static async Task<IResult> ServeAsync(string name, HttpContext context, IMediaStore store)
    {
        if (!MediaName().IsMatch(name))
        {
            return Results.NotFound();
        }

        var file = await store.OpenAsync(name, context.RequestAborted);
        if (file is null)
        {
            return Results.NotFound();
        }

        var contentType = ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";
        var headers = context.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        if (!InlineTypes.Contains(contentType))
        {
            var download = name[(name.IndexOf('-', StringComparison.Ordinal) + 1)..];
            headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileNameStar = download }.ToString();
        }

        return Results.Stream(
            file.Content,
            contentType,
            lastModified: file.LastModified,
            entityTag: new EntityTagHeaderValue(file.ETag),
            enableRangeProcessing: true);
    }

    // A lowercase hex hash (8 to 64 digits), a hyphen, and a name of lowercase letters, digits,
    // dots, hyphens and underscores: no slashes, so no path tricks.
    [GeneratedRegex("^[0-9a-f]{8,64}-[a-z0-9][a-z0-9._-]{0,200}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex MediaName();
}
