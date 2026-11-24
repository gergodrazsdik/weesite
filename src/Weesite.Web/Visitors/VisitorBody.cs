using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Weesite.Web.Visitors;

/// <summary>A JSON body as <see cref="VisitorBody.ReadJsonAsync{T}"/> read it.</summary>
/// <typeparam name="T">What it holds.</typeparam>
/// <param name="Value">What it held; null when it wasn't JSON of that shape, or was too large.</param>
/// <param name="TooLarge">Whether it was larger than <see cref="VisitorBody.MaxBytes"/>.</param>
internal readonly record struct JsonBody<T>(T? Value, bool TooLarge)
    where T : class;

/// <summary>
/// The size of what visitors send (ADR 0011): every request under <c>/visitor</c> and the contact
/// form's post. Each holds a few short fields (a comment is at most 1,000 characters), so a body
/// over <see cref="MaxBytes"/> is refused with 413 before it's read: by its <c>Content-Length</c>, or,
/// without one, by the server as it reads, and by <see cref="ReadJsonAsync{T}"/> in any case.
/// </summary>
internal static class VisitorBody
{
    /// <summary>The largest body a visitor may send: 32 KB.</summary>
    public const int MaxBytes = 32 * 1024;

    /// <summary>Adds the limit, after the rate limiter, so a refused request still counts.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IApplicationBuilder UseVisitorBodyLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (Applies(context.Request))
            {
                if (context.Request.ContentLength > MaxBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync("The request is too large.\n", context.RequestAborted);
                    return;
                }

                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                {
                    size.MaxRequestBodySize = MaxBytes;
                }
            }

            await next(context);
        });

    /// <summary>Whether a request is something a visitor sends: anything under <c>/visitor</c>, or the contact form's post.</summary>
    /// <param name="request">The request.</param>
    /// <returns>True when the limit applies.</returns>
    public static bool Applies(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Path.StartsWithSegments("/visitor", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPost(request.Method) && RateLimits.IsPath(request.Path, "/contact"));
    }

    /// <summary>
    /// Reads a JSON body of at most <see cref="MaxBytes"/>, whatever the server allows: no more is
    /// read, and nothing is parsed, when it's larger.
    /// </summary>
    /// <typeparam name="T">What it holds.</typeparam>
    /// <param name="context">The request.</param>
    /// <param name="options">The JSON options.</param>
    /// <returns>What it held, or that it was too large; a null value when it wasn't JSON of that shape.</returns>
    public static async Task<JsonBody<T>> ReadJsonAsync<T>(HttpContext context, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.ContentLength > MaxBytes)
        {
            return new JsonBody<T>(null, TooLarge: true);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);
        try
        {
            var length = 0;
            try
            {
                int read;
                while (length <= MaxBytes && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, MaxBytes + 1 - length), context.RequestAborted)) > 0)
                {
                    length += read;
                }
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return new JsonBody<T>(null, TooLarge: true);
            }

            if (length > MaxBytes)
            {
                return new JsonBody<T>(null, TooLarge: true);
            }

            try
            {
                return new JsonBody<T>(JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), options), TooLarge: false);
            }
            catch (JsonException)
            {
                return new JsonBody<T>(null, TooLarge: false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
