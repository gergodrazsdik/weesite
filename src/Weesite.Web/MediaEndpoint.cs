using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Weesite.Content.Media;
using Weesite.Content.Sources;
using Weesite.Web.Seo;

namespace Weesite.Web;

/// <summary>
/// Serves <c>/media/{hash}-{name}</c> from the media store. Names are content-hashed, so
/// responses are cached for a year as immutable. Every response says <c>nosniff</c>; only raster
/// images are shown inline, everything else (SVG included: it can carry script) downloads, and
/// says <c>noindex</c>, so an attachment isn't a search result of its own (F-0260). Images stay
/// indexable, for image search.
/// </summary>
internal static partial class MediaEndpoint
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = WithDataTypes(new());

    private static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif",
    };

    /// <summary>Maps the endpoint.</summary>
    /// <param name="app">The app.</param>
    public static void MapMedia(this IEndpointRouteBuilder app) =>
        app.MapGetAndHead("/media/{name}", ServeAsync);

    private static async Task<IResult> ServeAsync(string name, HttpContext context, IMediaStore store)
    {
        if (!MediaIndex.IsMediaName(name))
        {
            return Results.NotFound();
        }

        var file = await store.OpenAsync(name, context.RequestAborted);
        if (file is null)
        {
            return Results.NotFound();
        }

        var contentType = ContentTypeOf(name);
        var headers = context.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        if (!InlineTypes.Contains(contentType))
        {
            var download = name[(name.IndexOf('-', StringComparison.Ordinal) + 1)..];
            headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("attachment") { FileNameStar = download }.ToString();
            RobotsTag.NoIndex(context.Response);
        }

        return Results.Stream(
            file.Content,
            contentType,
            lastModified: file.LastModified,
            entityTag: new EntityTagHeaderValue(file.ETag),
            enableRangeProcessing: true);
    }

    /// <summary>Gets the content type a stored file is served with.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The type its extension says, or <c>application/octet-stream</c>.</returns>
    internal static string ContentTypeOf(string name) =>
        ContentTypes.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    // The data files (T-0152) with their registered types (RFC 8259, RFC 9512, IANA's
    // application/toml), whatever the framework's table holds.
    private static FileExtensionContentTypeProvider WithDataTypes(FileExtensionContentTypeProvider provider)
    {
        provider.Mappings[".json"] = "application/json";
        provider.Mappings[".yaml"] = "application/yaml";
        provider.Mappings[".yml"] = "application/yaml";
        provider.Mappings[".toml"] = "application/toml";
        return provider;
    }

    /// <summary>Whether a name is one the media endpoint serves (the backup import uses it too).</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it's a valid media name.</returns>
    public static bool IsValidName(string name) => MediaIndex.IsMediaName(name);
}
