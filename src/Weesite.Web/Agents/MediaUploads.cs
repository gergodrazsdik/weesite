using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>An upload address for one file (T-0137).</summary>
/// <param name="Url">Where to send the file.</param>
/// <param name="Method">The HTTP method: <c>PUT</c>.</param>
/// <param name="Authorization">The value of the <c>Authorization</c> header to send with it.</param>
/// <param name="ExpiresAt">When the address stops working (UTC, ISO 8601).</param>
/// <param name="MaxBytes">The largest file it takes.</param>
/// <param name="Curl">A curl command that sends the file, with a placeholder for its local path.</param>
internal sealed record EditorUploadAddress(string Url, string Method, string Authorization, string ExpiresAt, long MaxBytes, string Curl);

/// <summary>
/// Upload addresses for the editor MCP endpoint (T-0137, ADR 0007 amendment of 2026-11-17): an app
/// asks for one with <c>create_media_upload</c> and sends the file with a plain HTTP <c>PUT</c>,
/// so the bytes don't pass through the model as base64. The token goes in the
/// <c>Authorization</c> header, not the address, so it stays out of request logs and traces. It
/// works once, for <see cref="Lifetime"/>, for the file name it was made for; revoking the app or
/// removing the editor ends it too. The file gets the same checks as every other upload.
/// </summary>
internal sealed class MediaUploads(
    IOAuthStore store,
    MediaLibrary media,
    ContentCatalogProvider content,
    IAuditLog audit,
    IEditorDirectory editors,
    IMemoryCache cache,
    SiteUrls urls,
    TimeProvider time)
{
    /// <summary>The path files are sent to.</summary>
    public const string Path = "/mcp/editor/upload";

    /// <summary>The <c>Authorization</c> scheme of upload tokens.</summary>
    public const string Scheme = "Upload";

    /// <summary>How many unused addresses one app may hold at once.</summary>
    public const int MaxOpen = 5;

    /// <summary>The largest file an address takes: the editor's limit, as the bytes come as they are.</summary>
    public const long MaxBytes = MediaLibrary.MaxBytes;

    /// <summary>How long an address works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Makes an upload address for the app and editor of the request.</summary>
    /// <param name="user">The editor, signed in with the app's access token.</param>
    /// <param name="request">The request, for the site's address.</param>
    /// <param name="fileName">The file name to store the upload under.</param>
    /// <param name="alt">An image's description.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The address.</returns>
    public async Task<EditorUploadAddress> CreateAsync(ClaimsPrincipal user, HttpRequest request, string fileName, string? alt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        fileName = (fileName ?? "").Trim();
        var extension = System.IO.Path.GetExtension(fileName);
        if (fileName.Length is 0 or > 200 || !MediaLibrary.AllowedExtensions.Contains(extension))
        {
            throw new McpException($"Give a file name with one of these extensions: {string.Join(", ", MediaLibrary.AllowedExtensions.Order(StringComparer.Ordinal))}.");
        }

        alt = string.IsNullOrWhiteSpace(alt) ? null : alt.Trim();
        if (ImageProcessor.IsProcessable(fileName) && alt is null)
        {
            throw new McpException("Describe the image (alt): what does it show?");
        }

        var grantId = user.FindFirstValue(EditorBearerHandler.GrantClaim) ?? throw new McpException("Not signed in.");
        var now = time.GetUtcNow();
        if (await store.CountUploadsAsync(grantId, now, cancellationToken) >= MaxOpen)
        {
            throw new McpException($"This app has {MaxOpen} upload addresses it hasn't used yet: use one, or wait until they expire ({Lifetime.TotalMinutes:0} minutes after each was made).");
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now + Lifetime;
        var client = user.FindFirstValue(EditorBearerHandler.ClientClaim) ?? "an app";
        await store.SaveUploadAsync(new MediaUploadTicket(Hash(token), grantId, EditorClaims.Email(user), client, fileName, alt, expiresAt), cancellationToken);
        await audit.WriteAsync(new AuditEntry(now, EditorClaims.Email(user), "mcp-upload-address", $"{fileName} (via {client})"), CancellationToken.None);

        var url = urls.Absolute(request, Path);
        var authorization = $"{Scheme} {token}";
        return new EditorUploadAddress(
            url,
            "PUT",
            authorization,
            expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            MaxBytes,
            $"curl --fail-with-body -T <local file> -H \"Authorization: {authorization}\" {url}");
    }

    /// <summary>Maps the upload endpoint.</summary>
    /// <param name="app">The app.</param>
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut(Path, (HttpContext context, MediaUploads uploads) => uploads.ReceiveAsync(context))
            .WithMetadata(new RequestSizeLimitAttribute(MaxBytes + 1))
            .DisableAntiforgery();

    /// <summary>Takes a file sent to an upload address.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The stored file as <c>upload_media</c> describes it, or an error with a reason.</returns>
    internal async Task<IResult> ReceiveAsync(HttpContext context)
    {
        var request = context.Request;
        var cancellationToken = context.RequestAborted;

        // A cookie never counts: only the token, which only an app an editor allowed can get.
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase) || header[(Scheme.Length + 1)..].Trim() is not { Length: 43 } token)
        {
            return Error(StatusCodes.Status401Unauthorized, $"Send the Authorization header from create_media_upload: \"{Scheme} <token>\".");
        }

        // Checked before the address is used up, so a wrong request can be sent again correctly.
        if (request.ContentLength is not { } length)
        {
            return Error(StatusCodes.Status411LengthRequired, "Send the file's length (Content-Length), as curl -T does.");
        }

        if (length is 0 or > MaxBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, $"The file must be between 1 byte and {MaxBytes / (1024 * 1024)} MB. Upload larger files in /admin, under Media.");
        }

        if (await store.TakeUploadAsync(Hash(token), cancellationToken) is not { } ticket)
        {
            return Error(StatusCodes.Status401Unauthorized, "This upload address is unknown or was used already. Ask for a new one with create_media_upload.");
        }

        if (ticket.ExpiresAt <= time.GetUtcNow())
        {
            return Error(StatusCodes.Status401Unauthorized, "This upload address expired. Ask for a new one with create_media_upload.");
        }

        // The app's access and the editor's role as they are now, not as they were.
        if (await store.FindGrantAsync(ticket.GrantId, cancellationToken) is not { } grant
            || !string.Equals(grant.Email, ticket.Email, StringComparison.OrdinalIgnoreCase)
            || (await SignIn.FindStatusAsync(cache, editors, ticket.Email, cancellationToken)).Role is not { } role)
        {
            return Error(StatusCodes.Status401Unauthorized, "The app's access ended. Connect it again, then ask for a new upload address.");
        }

        var data = new byte[length];
        try
        {
            await request.Body.ReadExactlyAsync(data, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return Error(StatusCodes.Status400BadRequest, "The file ended before its Content-Length. Ask for a new upload address and send it again.");
        }

        var outcome = await media.UploadAsync(ticket.FileName, data, cancellationToken, ticket.Alt, replaceDescription: role == EditorRole.Admin);
        if (outcome is not MediaUploaded uploaded)
        {
            return Error(StatusCodes.Status422UnprocessableEntity, ((MediaRejected)outcome).Reason);
        }

        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), ticket.Email, "mcp-media-uploaded", $"{uploaded.Item.Name} (via {ticket.ClientName})"), CancellationToken.None);
        var link = MediaLinks.Describe(uploaded.Item, content.Current.Media, ticket.Alt);
        return Results.Json(new EditorMedia(link.Name, link.Path, link.IsImage, link.Width, link.Height, link.Markdown), statusCode: StatusCodes.Status201Created);
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
