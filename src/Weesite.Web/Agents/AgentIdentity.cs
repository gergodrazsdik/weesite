using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>
/// How the MCP endpoints introduce themselves to a client when it connects: the site's name and
/// tagline from the site settings, a link to the site, and instructions that fit the endpoint. A
/// client shows these in its server list; the name a user gives the connection is the client's.
/// </summary>
internal static class AgentIdentity
{
    private static readonly string Version =
        typeof(AgentIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentIdentity).Assembly.GetName().Version?.ToString(3)
        ?? "1.0.0";

    /// <summary>Sets a request's server name, title, description and instructions.</summary>
    /// <param name="server">The request's server options.</param>
    /// <param name="context">The request.</param>
    /// <param name="editor">Whether it's the editor endpoint.</param>
    public static void Describe(McpServerOptions server, HttpContext context, bool editor)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(context);
        var site = context.RequestServices.GetRequiredService<ContentCatalogProvider>().Current.Site;
        var origin = context.RequestServices.GetRequiredService<SiteUrls>().Origin(context.Request);
        server.ServerInfo = new Implementation
        {
            Name = editor ? "weesite-editor" : "weesite",
            Title = editor ? $"{site.Name} (editor)" : site.Name,
            Description = site.Tagline,
            Version = Version,
            WebsiteUrl = origin,
        };
        server.ServerInstructions = editor
            ? $"You work on {site.Name} ({origin}) for the editor who connected you, with their rights. "
                + "Find posts and pages with list_items, read one with get_item. create_item saves a new draft; "
                + "update_item saves a whole new file at the version you read. set_status publishes, schedules, "
                + "unpublishes or archives, if the editor allowed this app to publish; otherwise the editor publishes. "
                + "list_media and upload_media handle images and files; every image needs alt text. "
                + "markdown_guide lists the Markdown the site supports; preview_item shows a text as the site would."
            : $"The public posts and pages of {site.Name} ({origin}). Search with search_content, list with "
                + "list_content, and read one in full as Markdown with get_content.";
    }
}
