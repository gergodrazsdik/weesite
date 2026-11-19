using System.Globalization;
using System.Net;
using System.Text;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Visitors;

/// <summary>
/// The contact form's hourly digest (ADR 0011): every message since the last one, to the address in
/// "Messages go to". The visitors' words go only into the body, escaped in the HTML; the headers
/// hold the site's own subject and address. A message without a proof of work says so.
/// </summary>
/// <param name="messages">The messages.</param>
/// <param name="content">The site: its name and the recipient.</param>
/// <param name="urls">The site's address, for the link to the Messages page.</param>
internal sealed class MessagesDigest(IMessageStore messages, ContentCatalogProvider content, SiteUrls urls) : IDigest
{
    private string? _origin;

    /// <inheritdoc />
    public string Name => "messages";

    /// <summary>
    /// Gets or sets the site's origin as the last message's request had it, for the link when
    /// <c>Site:BaseUrl</c> isn't set.
    /// </summary>
    public string? Origin
    {
        get => Volatile.Read(ref _origin);
        set => Volatile.Write(ref _origin, value);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DigestMail>> BuildAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var site = content.Current.Site;
        if (site.ContactForm?.Recipient is not { } recipient || !ContactFormSettings.IsAddress(recipient))
        {
            return [];
        }

        var arrived = await messages.ReceivedAsync(after, until, cancellationToken);
        return arrived.Count == 0 ? [] : [Build(site.Name, recipient, arrived, (urls.BaseUrl ?? Origin) is { } origin ? origin + "/admin/messages" : null)];
    }

    /// <summary>Builds the mail.</summary>
    /// <param name="siteName">The site's name.</param>
    /// <param name="recipient">Where it goes.</param>
    /// <param name="arrived">The messages, oldest first.</param>
    /// <param name="link">The Messages page's address, or null when it isn't known.</param>
    /// <returns>The mail.</returns>
    internal static DigestMail Build(string siteName, string recipient, IReadOnlyList<ContactMessage> arrived, string? link)
    {
        var subject = arrived.Count == 1
            ? $"1 new message on {siteName}"
            : string.Create(CultureInfo.InvariantCulture, $"{arrived.Count} new messages on {siteName}");
        var text = new StringBuilder();
        var html = new StringBuilder();
        var intro = arrived.Count == 1
            ? $"A message came through the contact form on {siteName}."
            : string.Create(CultureInfo.InvariantCulture, $"{arrived.Count} messages came through the contact form on {siteName}.");
        text.Append(intro).Append("\n\n");
        html.Append("<p>").Append(Encode(intro)).Append("</p>\n");
        foreach (var message in arrived)
        {
            var when = message.Received.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            text.Append("From: ").Append(message.Name).Append(" (").Append(message.Email).Append(")\n")
                .Append("Received: ").Append(when).Append('\n');
            html.Append("<hr>\n<p><strong>").Append(Encode(message.Name)).Append("</strong> (<a href=\"mailto:")
                .Append(Encode(message.Email)).Append("\">").Append(Encode(message.Email)).Append("</a>)<br>\n")
                .Append(Encode(when)).Append("</p>\n");
            if (message.Unverified)
            {
                const string Unverified = "Unverified: sent without the browser check, so more likely to be spam.";
                text.Append(Unverified).Append('\n');
                html.Append("<p><em>").Append(Encode(Unverified)).Append("</em></p>\n");
            }

            text.Append('\n').Append(message.Text).Append("\n\n---\n\n");
            html.Append("<p style=\"white-space: pre-wrap\">").Append(Encode(message.Text)).Append("</p>\n");
        }

        var footer = link is null
            ? "Read, keep or delete them under Messages in the site's admin."
            : $"Read, keep or delete them under Messages in the site's admin: {link}";
        text.Append(footer).Append('\n');
        html.Append("<hr>\n<p>").Append(Encode("Read, keep or delete them under Messages in the site's admin"));
        if (link is not null)
        {
            html.Append(": <a href=\"").Append(Encode(link)).Append("\">").Append(Encode(link)).Append("</a>");
        }

        html.Append(".</p>\n");
        return new DigestMail(recipient, subject, text.ToString(), html.ToString());
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
