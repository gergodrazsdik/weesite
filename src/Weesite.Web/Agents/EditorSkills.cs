using ModelContextProtocol.Extensions.Skills;
using ModelContextProtocol.Server;

namespace Weesite.Web.Agents;

/// <summary>
/// MCP skills on the editor endpoint (T-0199): the skills extension (SEP-2640,
/// <c>io.modelcontextprotocol/skills</c>) serves <c>Skills/weesite-editor</c>, a SKILL.md that
/// teaches an app the editing workflow, to clients that load skills. Only <c>/mcp/editor</c>
/// serves it: the public <c>/mcp</c> drops the extension's handlers, capability and files for
/// every request, as it drops the editor tools, because the SDK's skills requests don't pass
/// through resource filters (csharp-sdk issue #1941).
/// </summary>
internal static class EditorSkills
{
    /// <summary>The folder of the skills, under the content root: each subfolder is one skill.</summary>
    public const string Folder = "Skills";

    /// <summary>The URI scheme of skill files.</summary>
    public const string Scheme = "skill://";

    /// <summary>Serves each skill folder under <see cref="Folder"/> with its files as resources.</summary>
    /// <param name="builder">The MCP server.</param>
    /// <param name="contentRoot">The app's content root: the project in development and tests, the app's folder when published.</param>
    /// <returns>The builder.</returns>
    public static IMcpServerBuilder WithEditorSkills(this IMcpServerBuilder builder, string contentRoot)
    {
        // The SDK marks its skills extension experimental (MCPEXP005); Weesite serves it as one (T-0199).
#pragma warning disable MCPEXP005
        return builder.WithSkillsFromSubdirectories(Path.Combine(contentRoot, Folder));
#pragma warning restore MCPEXP005
    }

    /// <summary>Keeps the skills for the editor endpoint and removes every trace of them from the public one.</summary>
    /// <param name="server">The request's server options, made for this request alone.</param>
    /// <param name="editor">Whether the request is for <c>/mcp/editor</c>.</param>
    public static void KeepFor(McpServerOptions server, bool editor)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (editor)
        {
            return;
        }

        // The skills extension's methods are custom request handlers, an experimental SDK API
        // (MCPEXP002), as is the extension's identifier (MCPEXP005).
#pragma warning disable MCPEXP002, MCPEXP005
        foreach (var handler in server.RequestHandlers?.Where(h => h.Method.StartsWith("skills/", StringComparison.Ordinal)).ToArray() ?? [])
        {
            server.RequestHandlers!.Remove(handler);
        }

        server.Capabilities?.Extensions?.Remove(SkillsProtocol.ExtensionId);
#pragma warning restore MCPEXP002, MCPEXP005

        foreach (var resource in server.ResourceCollection?.Where(r => IsSkillFile(r.ProtocolResourceTemplate.UriTemplate)).ToArray() ?? [])
        {
            server.ResourceCollection!.Remove(resource);
        }

        // Without resources the public endpoint advertises none, as before the skills.
        if (server.ResourceCollection is { Count: 0 })
        {
            server.ResourceCollection = null;
            server.Capabilities?.Resources = null;
        }
    }

    private static bool IsSkillFile(string uri) => uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
}
