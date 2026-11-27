using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

// The files extension needs the SDK's alternate results (ResultOrAlternate, CallToolWithAlternateFilters),
// marked for evaluation (MCPEXP002); this file is the experiment (T-0200) and nothing else uses them.
#pragma warning disable MCPEXP002

namespace Weesite.Web.Agents;

/// <summary>
/// The experimental MCP file-uploads extension (<c>io.modelcontextprotocol/files</c>, T-0200) on
/// the editor endpoint: an app whose client declares the extension gets <c>add_media_file</c>,
/// whose <c>file</c> argument its client fills from a file the person chose, so the bytes never
/// pass through the model. A small file comes inline; a larger one goes out of band: the call
/// answers <c>upload_required</c> with a target under the MCP address, the client sends the bytes
/// there with its own MCP token and calls again with the <c>requestState</c>, and only then is
/// the file checked and stored, as <c>upload_media</c> stores one. The draft of the extension is
/// at <see href="https://github.com/modelcontextprotocol/experimental-ext-file-uploads"/>.
/// </summary>
/// <remarks>
/// Bytes wait in this replica's memory between the transfer and the retry (staging), for at most
/// <see cref="StateLifetime"/>: a retry that reaches another replica finds nothing and fails
/// without storing anything. The <c>requestState</c> is protected with the app's Data Protection
/// keys and bound to the tool, the arguments and the grant; it's used once, through an upload
/// ticket in the shared OAuth store, which also counts towards an app's open uploads.
/// </remarks>
internal sealed class MediaFileUploads(
    IOAuthStore store,
    MediaLibrary media,
    ContentCatalogProvider content,
    IAuditLog audit,
    IDataProtectionProvider protection,
    IHttpContextAccessor http,
    TimeProvider time)
{
    /// <summary>The extension's identifier.</summary>
    public const string ExtensionId = "io.modelcontextprotocol/files";

    /// <summary>The tool's name.</summary>
    public const string ToolName = "add_media_file";

    /// <summary>The path targets are under, relative to the editor endpoint (<c>/mcp/editor</c>), as the extension's <c>path</c> targets are.</summary>
    public const string TargetPath = "/upload/files";

    /// <summary>The largest inline file, decoded: base64 in JSON is held several times over, so 1 MB.</summary>
    public const int MaxInlineBytes = 1024 * 1024;

    /// <summary>The most bytes this replica holds for transfers not yet finished.</summary>
    public const long MaxStagedBytes = 64L * 1024 * 1024;

    /// <summary>How long a target takes a transfer, as an upload address does.</summary>
    public static readonly TimeSpan TargetLifetime = MediaUploads.Lifetime;

    /// <summary>How long a <c>requestState</c> works: a transfer started as its target expires, and the retry.</summary>
    public static readonly TimeSpan StateLifetime = MediaUploads.Lifetime + TimeSpan.FromMinutes(5);

    private const string StatePurpose = "Weesite.Mcp.FileUploads.RequestState.v1";

    private readonly ConcurrentDictionary<string, Staged> _staged = new(StringComparer.Ordinal);

    /// <summary>The extension's settings the editor endpoint advertises.</summary>
    /// <returns>A new settings object: out-of-band uploads, and inline files up to <see cref="MaxInlineBytes"/>.</returns>
    public static JsonObject Settings() => new() { ["upload"] = new JsonObject(), ["maxInlineBytes"] = MaxInlineBytes };

    /// <summary>Whether a request's client capabilities declare the extension, with the setting asked for if given.</summary>
    /// <param name="capabilities">The request's client capabilities.</param>
    /// <param name="setting">A setting that must be declared too, such as <c>upload</c>, or null.</param>
    /// <returns>True when declared, in a revision this server knows.</returns>
    public static bool Declares(ClientCapabilities? capabilities, string? setting = null)
    {
        if (capabilities?.Extensions is not { } extensions || !extensions.TryGetValue(ExtensionId, out var value))
        {
            return false;
        }

        var settings = JsonSerializer.SerializeToElement(value, McpJsonUtilities.DefaultOptions);
        return settings.ValueKind == JsonValueKind.Object
            && (!settings.TryGetProperty("revision", out var revision) || revision.ValueKind == JsonValueKind.Null)
            && (setting is null || settings.TryGetProperty(setting, out _));
    }

    /// <summary>The tool, with its <c>x-mcp-file</c> schema; the call-tool filter answers its calls.</summary>
    /// <returns>The tool.</returns>
    public static McpServerTool Tool() => new FileTool();

    /// <summary>Answers a call to <c>add_media_file</c>: stores an inline file, asks for an upload, or stores an uploaded one.</summary>
    /// <param name="context">The call.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The tool's result, or an upload request.</returns>
    public async ValueTask<ResultOrAlternate<CallToolResult>> CallAsync(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = User();
        var call = context.Params!;
        var arguments = call.Arguments ?? new Dictionary<string, JsonElement>();
        if (!arguments.TryGetValue("file", out var argument) || FileInput.Parse(argument) is not { } file)
        {
            return Error("file is the file your client picked: an object with id, mimeType and size, and the name with its extension.");
        }

        var alt = arguments.TryGetValue("alt", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var fileName = (file.Name ?? "").Trim();
        if (Refusal(fileName, alt, file.Size) is { } refusal)
        {
            return Error(refusal);
        }

        if (call.RequestState is { } state)
        {
            return await RetryAsync(user, call, file, fileName, alt, state, cancellationToken);
        }

        if (file.HasData)
        {
            return await InlineAsync(user, file, fileName, alt, cancellationToken);
        }

        if (file.Size == 0)
        {
            return Error("The file is empty.");
        }

        // A non-empty file without data goes out of band, which the client has to support.
        var capabilities = context.JsonRpcRequest.Context?.ClientCapabilities ?? context.Server.ClientCapabilities;
        if (!Declares(capabilities, "upload"))
        {
            throw new MissingRequiredClientCapabilityException(
                new ClientCapabilities { Extensions = new Dictionary<string, object> { [ExtensionId] = new JsonObject { ["upload"] = new JsonObject() } } },
                "Sending a file out of band needs the client to declare the files extension with upload.");
        }

        return await RequestUploadAsync(user, call, file, fileName, alt, cancellationToken);
    }

    /// <summary>Maps the targets: <c>PUT /mcp/editor/upload/files/{id}</c>, with the app's MCP token.</summary>
    /// <param name="app">The app.</param>
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut(AuthorizationRequests.ResourcePath + TargetPath + "/{id}", (string id, HttpContext context, MediaFileUploads uploads) => uploads.ReceiveAsync(id, context))
            .RequireAuthorization(EditorBearerHandler.Policy)
            .WithMetadata(new RequestSizeLimitAttribute(MediaLibrary.MaxBytes + 1))
            .DisableAntiforgery();

    /// <summary>Takes the bytes sent to a target: once, within its lifetime, from the app it was made for, exactly the size the call declared.</summary>
    /// <param name="id">The target's ID.</param>
    /// <param name="context">The request.</param>
    /// <returns>204, or an error with a reason.</returns>
    internal async Task<IResult> ReceiveAsync(string id, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Sweep();
        if (!_staged.TryGetValue(id, out var staged))
        {
            return Failed(StatusCodes.Status404NotFound, "There's no such upload target. Call add_media_file again.");
        }

        // The credential has to be the one the target was made for ("include" targets).
        if (context.User.FindFirstValue(EditorBearerHandler.GrantClaim) != staged.GrantId)
        {
            return Failed(StatusCodes.Status403Forbidden, "This upload target belongs to another app.");
        }

        if (time.GetUtcNow() >= staged.TargetExpiresAt)
        {
            return Failed(StatusCodes.Status410Gone, "This upload target expired. Call add_media_file again.");
        }

        if (context.Request.ContentLength is not { } length)
        {
            return Failed(StatusCodes.Status411LengthRequired, "Send the file's length (Content-Length).");
        }

        if (length != staged.Size)
        {
            return Failed(length > staged.Size ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, $"Send exactly the {staged.Size} bytes the call declared.");
        }

        var data = new byte[length];
        try
        {
            await context.Request.Body.ReadExactlyAsync(data, context.RequestAborted);
        }
        catch (EndOfStreamException)
        {
            return Failed(StatusCodes.Status400BadRequest, "The file ended before its Content-Length.");
        }

        // One write: a target can't be written again, so the bytes confirmed are the bytes sent.
        return staged.TrySet(data)
            ? Results.NoContent()
            : Failed(StatusCodes.Status409Conflict, "This upload target has its file already.");
    }

    // The checks that need no bytes, as create_media_upload makes them.
    private static string? Refusal(string fileName, string? alt, long size)
    {
        var extension = Path.GetExtension(fileName);
        if (fileName.Length > 200)
        {
            return "The file name is too long: give one of at most 200 characters.";
        }

        if (fileName.Length == 0 || !MediaLibrary.AllowedExtensions.Contains(extension))
        {
            return $"Give the file's name with one of these extensions: {string.Join(", ", MediaLibrary.AllowedExtensions.Order(StringComparer.Ordinal))}.";
        }

        if (ImageProcessor.IsProcessable(fileName) && alt is null)
        {
            return "Describe the image (alt): what does it show?";
        }

        return size > MediaLibrary.MaxBytes ? $"The file is larger than {MediaLibrary.MaxBytes / (1024 * 1024)} MB. Upload larger files in /admin, under Media." : null;
    }

    private async Task<ResultOrAlternate<CallToolResult>> InlineAsync(ClaimsPrincipal user, FileInput file, string fileName, string? alt, CancellationToken cancellationToken)
    {
        if (file.Data is not { } data)
        {
            return Error("The file's data isn't base64.");
        }

        if (data.Length > ((MaxInlineBytes + 2) / 3 * 4) + 4)
        {
            return Error($"Inline files are at most {MaxInlineBytes / 1024} KB: send larger ones out of band.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            return Error("The file's data isn't base64.");
        }

        return bytes.Length > MaxInlineBytes ? Error($"Inline files are at most {MaxInlineBytes / 1024} KB: send larger ones out of band.")
            : bytes.Length != file.Size ? Error($"The file has {bytes.Length} bytes, not the {file.Size} its size says.")
            : !file.DigestMatches(bytes) ? Error("The file doesn't match its digest.")
            : await StoreAsync(user, fileName, alt, bytes, cancellationToken);
    }

    private async Task<ResultOrAlternate<CallToolResult>> RequestUploadAsync(ClaimsPrincipal user, CallToolRequestParams call, FileInput file, string fileName, string? alt, CancellationToken cancellationToken)
    {
        var grantId = user.FindFirstValue(EditorBearerHandler.GrantClaim)!;
        var now = time.GetUtcNow();
        if (await store.CountUploadsAsync(grantId, now, cancellationToken) >= MediaUploads.MaxOpen)
        {
            return Error($"This app has {MediaUploads.MaxOpen} uploads it hasn't finished: finish one, or wait until they expire ({StateLifetime.TotalMinutes:0} minutes after each was asked for).");
        }

        Sweep();
        if (_staged.Values.Where(s => !s.Used).Sum(s => s.Size) + file.Size > MaxStagedBytes)
        {
            return Error("The site is taking other uploads: try again in a few minutes.");
        }

        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var targetId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var targetExpiresAt = now + TargetLifetime;
        var stateExpiresAt = now + StateLifetime;
        var client = user.FindFirstValue(EditorBearerHandler.ClientClaim) ?? "an app";
        var email = EditorClaims.Email(user);

        // The ticket makes the requestState usable once, on any replica; nobody learns its token.
        await store.SaveUploadAsync(new MediaUploadTicket(TicketHash(nonce), grantId, email, client, fileName, alt, stateExpiresAt), cancellationToken);
        _staged[targetId] = new Staged(grantId, file.Size, targetExpiresAt, stateExpiresAt);
        await audit.WriteAsync(new AuditEntry(now, email, "mcp-upload-address", $"{fileName} (via {client}, files extension)"), CancellationToken.None);

        var state = new State(ToolName, Fingerprint(call.Arguments), grantId, email, file.Id, targetId, nonce, stateExpiresAt);
        var protector = protection.CreateProtector(StatePurpose);
        var upload = new UploadRequiredResult
        {
            Uploads = [new UploadTarget(file.Id, $"{TargetPath}/{targetId}", "PUT", "include", Timestamp(targetExpiresAt))],
            RequestState = protector.Protect(JsonSerializer.Serialize(state, FileUploadsJson.Default.State)),
        };
        return ResultOrAlternate<CallToolResult>.FromAlternate(upload, FileUploadsJson.Default.UploadRequiredResult);
    }

    private async Task<ResultOrAlternate<CallToolResult>> RetryAsync(ClaimsPrincipal user, CallToolRequestParams call, FileInput file, string fileName, string? alt, string requestState, CancellationToken cancellationToken)
    {
        // 1 and 2: the state is ours, current, and for this tool, these arguments and this app.
        State? state;
        try
        {
            state = JsonSerializer.Deserialize(protection.CreateProtector(StatePurpose).Unprotect(requestState), FileUploadsJson.Default.State);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            state = null;
        }

        if (state is null || state.ExpiresAt <= time.GetUtcNow())
        {
            throw new McpProtocolException("The requestState is unknown or expired. Call add_media_file again without it.", McpErrorCode.InvalidParams);
        }

        if (state.Tool != call.Name || state.Arguments != Fingerprint(call.Arguments) || state.FileId != file.Id
            || state.GrantId != user.FindFirstValue(EditorBearerHandler.GrantClaim) || !string.Equals(state.Email, EditorClaims.Email(user), StringComparison.OrdinalIgnoreCase))
        {
            throw new McpProtocolException("The requestState was made for another call. Call add_media_file again without it.", McpErrorCode.InvalidParams);
        }

        // A state this replica saw used fails as used; on another replica the bytes aren't there.
        if (_staged.TryGetValue(state.TargetId, out var seen) && seen.Used)
        {
            throw new McpProtocolException("This requestState was used already. Call add_media_file again without it.", McpErrorCode.InvalidParams);
        }

        // 3: the bytes arrived at the target, whole, and match the digest if one was given.
        if (!_staged.TryGetValue(state.TargetId, out var staged) || staged.Data is not { } data)
        {
            return Error("The file hasn't arrived at its upload target, so nothing was stored. Call add_media_file again to start over.");
        }

        if (data.Length != file.Size || !file.DigestMatches(data))
        {
            return Error("The file that arrived doesn't match its size or digest, so nothing was stored.");
        }

        // 4: used once, on any replica.
        if (await store.TakeUploadAsync(TicketHash(state.Nonce), cancellationToken) is null)
        {
            throw new McpProtocolException("This requestState was used already. Call add_media_file again without it.", McpErrorCode.InvalidParams);
        }

        // 5: stored as upload_media stores a file, with the same checks of its bytes.
        staged.Use();
        return await StoreAsync(user, fileName, alt, data, cancellationToken);
    }

    private async Task<ResultOrAlternate<CallToolResult>> StoreAsync(ClaimsPrincipal user, string fileName, string? alt, byte[] data, CancellationToken cancellationToken)
    {
        var outcome = await media.UploadAsync(fileName, data, cancellationToken, alt, replaceDescription: user.IsInRole(nameof(EditorRole.Admin)));
        if (outcome is not MediaUploaded uploaded)
        {
            return Error(((MediaRejected)outcome).Reason);
        }

        var client = user.FindFirstValue(EditorBearerHandler.ClientClaim) ?? "an app";
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), "mcp-media-uploaded", $"{uploaded.Item.Name} (via {client})"), CancellationToken.None);
        var link = MediaLinks.Describe(uploaded.Item, content.Current.Media, alt);
        var stored = new EditorMedia(link.Name, link.Path, link.IsImage, link.Width, link.Height, link.Markdown);
        var json = JsonSerializer.SerializeToElement(stored, McpJsonUtilities.DefaultOptions);
        return new CallToolResult { Content = [new TextContentBlock { Text = json.GetRawText() }], StructuredContent = json };
    }

    // The editor the token belongs to, as the editor tools require.
    private ClaimsPrincipal User() =>
        http.HttpContext?.User is { Identity.IsAuthenticated: true } user && user.HasClaim(c => c.Type == EditorBearerHandler.GrantClaim)
            ? user
            : throw new McpException("Not signed in.");

    // Staged bytes no requestState can use any more.
    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (id, staged) in _staged)
        {
            if (staged.StateExpiresAt <= now)
            {
                _staged.TryRemove(id, out _);
            }
        }
    }

    // The arguments in a form that compares as JSON values do: members sorted, numbers as numbers.
    private static string Fingerprint(IDictionary<string, JsonElement>? arguments)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        foreach (var (name, value) in (arguments ?? new Dictionary<string, JsonElement>()).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(JsonSerializer.Serialize(name)).Append(':');
            Canonical(value, builder);
            builder.Append(',');
        }

        builder.Append('}');
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Canonical(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Canonical(property.Value, builder);
                    builder.Append(',');
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in value.EnumerateArray())
                {
                    Canonical(item, builder);
                    builder.Append(',');
                }

                builder.Append(']');
                break;
            case JsonValueKind.Number:
                builder.Append(value.TryGetDecimal(out var number) ? number.ToString("G29", CultureInfo.InvariantCulture) : value.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            default:
                builder.Append(value.GetRawText());
                break;
        }
    }

    // A ticket of its own kind: no upload address token (create_media_upload's) hashes to it.
    private static string TicketHash(string nonce) => OAuthTokens.Hash("files-extension:" + nonce);

    private static string Timestamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static IResult Failed(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    /// <summary>What a <c>requestState</c> carries, protected.</summary>
    /// <param name="Tool">The tool it was issued for.</param>
    /// <param name="Arguments">The arguments' fingerprint.</param>
    /// <param name="GrantId">The app's grant.</param>
    /// <param name="Email">The editor.</param>
    /// <param name="FileId">The file argument's ID.</param>
    /// <param name="TargetId">The target the bytes go to.</param>
    /// <param name="Nonce">The ticket's token, which makes it usable once.</param>
    /// <param name="ExpiresAt">When it stops working.</param>
    internal sealed record State(string Tool, string Arguments, string GrantId, string Email, string FileId, string TargetId, string Nonce, DateTimeOffset ExpiresAt);

    // A target's bytes, waiting for the retry: written once.
    private sealed class Staged(string grantId, long size, DateTimeOffset targetExpiresAt, DateTimeOffset stateExpiresAt)
    {
        private byte[]? _data;

        public string GrantId { get; } = grantId;

        public long Size { get; } = size;

        public DateTimeOffset TargetExpiresAt { get; } = targetExpiresAt;

        public DateTimeOffset StateExpiresAt { get; } = stateExpiresAt;

        public byte[]? Data => Volatile.Read(ref _data);

        // Set once its requestState is used: a retry with it again is refused as used.
        public bool Used { get; private set; }

        public bool TrySet(byte[] data) => !Used && Interlocked.CompareExchange(ref _data, data, null) is null;

        public void Use()
        {
            Used = true;
            Volatile.Write(ref _data, null);
        }
    }

    // The FileInput the extension defines, as the client sent it.
    private sealed record FileInput(string Id, string? Name, long Size, string? Digest, bool HasData, string? Data)
    {
        public static FileInput? Parse(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: >= 1 and <= 128 } fileId
                || !value.TryGetProperty("mimeType", out var type) || type.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("size", out var size) || Integer(size) is not { } bytes || bytes < 0)
            {
                return null;
            }

            var name = value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            string? digest = null;
            if (value.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (!d.TryGetProperty("algorithm", out var algorithm) || algorithm.GetString() != "sha-256" || !d.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                digest = v.GetString();
            }

            // A data member of null counts as data that can't be decoded.
            var hasData = value.TryGetProperty("data", out var data);
            return new FileInput(fileId, name, bytes, digest, hasData, hasData && data.ValueKind == JsonValueKind.String ? data.GetString() : null);
        }

        // An integer however it's spelt (35, 35.0, 3.5e1), as JSON values compare.
        private static long? Integer(JsonElement value) =>
            value.ValueKind != JsonValueKind.Number ? null
            : value.TryGetInt64(out var whole) ? whole
            : value.TryGetDecimal(out var number) && number == decimal.Truncate(number) && number <= long.MaxValue ? (long)number
            : null;

        public bool DigestMatches(byte[] bytes) =>
            Digest is null || string.Equals(Digest, Base64Url.EncodeToString(SHA256.HashData(bytes)), StringComparison.Ordinal);
    }

    // The tool as tools/list shows it. MediaFileUploadFilter answers its calls, before this would.
    private sealed class FileTool : McpServerTool
    {
        public override Tool ProtocolTool { get; } = new()
        {
            Name = ToolName,
            Title = "Add a file to Media",
            Description = "Add an image (JPEG, PNG, WebP) or a file (PDF, SVG, text, Markdown, CSV, JSON, YAML, TOML, ZIP) of up to 20 MB to Media from a file your client sends itself, so its bytes don't pass through you: put the file the person chose in file, as your client lets you. Images lose their metadata (camera, location) and get smaller WebP copies; an image needs alt, a description of what it shows. Returns the stored file and the Markdown to put in a post, as upload_media does.",
            InputSchema = JsonElement.Parse(Schema()),
            Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = false, IdempotentHint = false, OpenWorldHint = false },
        };

        public override IReadOnlyList<object> Metadata { get; } = [];

        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Error("add_media_file isn't available here."));

        private static string Schema()
        {
            var accept = new JsonArray([.. MediaLibrary.AllowedExtensions.Order(StringComparer.Ordinal).Select(e => (JsonNode)e)]);
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["file"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "The file, filled in by your client: its name, with the extension, is the name it's stored under.",
                        ["x-mcp-file"] = new JsonObject { ["accept"] = accept, ["maxSize"] = MediaLibrary.MaxBytes },
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["mimeType"] = new JsonObject { ["type"] = "string" },
                            ["size"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                            ["digest"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject { ["algorithm"] = new JsonObject { ["const"] = "sha-256" }, ["value"] = new JsonObject { ["type"] = "string" } },
                                ["required"] = new JsonArray("algorithm", "value"),
                            },
                            ["data"] = new JsonObject { ["type"] = "string" },
                        },
                        ["required"] = new JsonArray("id", "mimeType", "size"),
                    },
                    ["alt"] = new JsonObject { ["type"] = "string", ["description"] = "For images: what it shows, for people who can't see it." },
                },
                ["required"] = new JsonArray("file"),
            }.ToJsonString();
        }
    }
}

/// <summary>The <c>upload_required</c> result of the files extension: where to send each file, and the state to call again with.</summary>
internal sealed class UploadRequiredResult : Result
{
    /// <summary>Initializes the result with its type.</summary>
    public UploadRequiredResult() => ResultType = "upload_required";

    /// <summary>Gets one target for each file to send out of band.</summary>
    [JsonPropertyName("uploads")]
    public required IReadOnlyList<UploadTarget> Uploads { get; init; }

    /// <summary>Gets the state the client echoes on the retry, opaque to it.</summary>
    [JsonPropertyName("requestState")]
    public required string RequestState { get; init; }
}

/// <summary>A target of the files extension: a path under the MCP address, sent to with the client's MCP credential.</summary>
/// <param name="FileId">The file argument's <c>id</c>.</param>
/// <param name="Path">The path under the MCP address.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Credentials"><c>include</c>: the client attaches its MCP token.</param>
/// <param name="ExpiresAt">When to start the transfer by (UTC, RFC 3339).</param>
internal sealed record UploadTarget(
    [property: JsonPropertyName("fileId")] string FileId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("credentials")] string Credentials,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt);

/// <summary>Serialization of the files extension's results and state.</summary>
[JsonSerializable(typeof(UploadRequiredResult))]
[JsonSerializable(typeof(MediaFileUploads.State))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class FileUploadsJson : JsonSerializerContext;

/// <summary>Registers the files extension (T-0200) on the MCP server.</summary>
internal static class MediaFileUploadsRegistration
{
    /// <summary>
    /// Adds <c>add_media_file</c>, listed only to clients that declare the extension, and the
    /// filter that answers its calls, which may answer <c>upload_required</c> instead of a result.
    /// </summary>
    /// <param name="builder">The MCP server.</param>
    /// <returns>The builder.</returns>
    public static IMcpServerBuilder WithMediaFileUploads(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<MediaFileUploads>();
        builder.Services.Configure<McpServerOptions>(options => options.Filters.Request.CallToolWithAlternateFilters.Add((context, next, cancellationToken) =>
            // Only where the tool is served: the editor endpoint, which removes it nowhere else.
            context.MatchedPrimitive is McpServerTool { ProtocolTool.Name: MediaFileUploads.ToolName }
                ? context.Services!.GetRequiredService<MediaFileUploads>().CallAsync(context, cancellationToken)
                : next(context, cancellationToken)));
        return builder
            .WithTools([MediaFileUploads.Tool()])
            .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var capabilities = context.JsonRpcRequest.Context?.ClientCapabilities ?? context.Server.ClientCapabilities;
                if (!MediaFileUploads.Declares(capabilities))
                {
                    // Servers should omit x-mcp-file tools from clients without the extension.
                    result.Tools = [.. result.Tools.Where(t => t.Name != MediaFileUploads.ToolName)];
                }

                return result;
            }));
    }

    /// <summary>Advertises the extension on the editor endpoint.</summary>
    /// <param name="server">The request's server options.</param>
    /// <param name="editor">Whether the request is for <c>/mcp/editor</c>.</param>
    public static void AdvertiseFor(McpServerOptions server, bool editor)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (editor)
        {
            server.Capabilities ??= new ServerCapabilities();
            server.Capabilities.Extensions ??= new Dictionary<string, object>();
            server.Capabilities.Extensions[MediaFileUploads.ExtensionId] = MediaFileUploads.Settings();
        }
    }
}
