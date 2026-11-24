using System.Net;
using System.Text;
using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>Body limits on the public MCP endpoint and the authorization server (T-0177).</summary>
internal sealed class ProtocolBodyTests
{
    [Test]
    [Arguments("/mcp", ProtocolBody.McpMaxBytes)]
    [Arguments("/oauth/register", ProtocolBody.OAuthMaxBytes)]
    [Arguments("/oauth/token", ProtocolBody.OAuthMaxBytes)]
    public async Task A_body_over_the_limit_is_refused_before_its_read(string path, int limit)
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        using var tooLarge = await PostAsync(client, path, limit + 1);
        using var fits = await PostAsync(client, path, 100);

        await Assert.That(tooLarge.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(fits.StatusCode).IsNotEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task The_editor_endpoint_keeps_its_own_limits()
    {
        // Editors send whole files to /mcp/editor: the public endpoint's limit isn't for it.
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        using var editor = await PostAsync(client, "/mcp/editor", ProtocolBody.McpMaxBytes + 1);

        await Assert.That(editor.StatusCode).IsNotEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(ProtocolBody.LimitFor(new Microsoft.AspNetCore.Http.DefaultHttpContext { Request = { Path = "/mcp/editor" } }.Request)).IsNull();
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, int bytes)
    {
        using var content = new StringContent("{\"x\":\"" + new string('a', Math.Max(0, bytes - 8)) + "\"}", Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }
}
