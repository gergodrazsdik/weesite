using System.Globalization;
using System.Net;
using System.Text;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Visitors;

/// <summary>
/// The contact form's hourly digest (ADR 0011): every message since the last one, to the address in
/// "Messages go to". The visitors' words go only into the body, escaped in the HTML; the headers
/// hold the site's own subject and address. A message without a proof of work says so. The link to
/// the Messages page is on <c>Site:BaseUrl</c>, and left out without it: never on the address a
/// visitor's request named. One mail lists at most <see cref="MaxListed"/> messages.
/// </summary>
/// <param name="messages">The messages.</param>
/// <param name="content">The site: its name and the recipient.</param>
/// <param name="urls">The site's address, for the link to the Messages page.</param>
internal sealed class MessagesDigest(IMessageStore messages, ContentCatalogProvider content, SiteUrls urls) : IDigest
{
    /// <summary>The most messages one mail lists; it counts the rest.</summary>
    public const int MaxListed = 50;

    /// <inheritdoc />
    public string Name => "messages";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DigestMail>> BuildAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var site = content.Current.Site;
        if (site.ContactForm?.Recipient is not { } recipient || !ContactFormSettings.IsAddress(recipient))
        {
            return [];
        }

        var arrived = await messages.ReceivedAsync(after, until, cancellationToken);
        return arrived.Count == 0 ? [] : [Build(site, recipient, arrived, urls.BaseUrl is { } origin ? origin + "/admin/messages" : null)];
    }

    /// <summary>Builds the mail.</summary>
    /// <param name="site">The site's settings: its name, and the time zone and clock times are shown in (T-0157).</param>
    /// <param name="recipient">Where it goes.</param>
    /// <param name="arrived">The messages, oldest first.</param>
    /// <param name="link">The Messages page's address, or null when it isn't known.</param>
    /// <returns>The mail.</returns>
    internal static DigestMail Build(SiteSettings site, string recipient, IReadOnlyList<ContactMessage> arrived, string? link)
    {
        var siteName = site.Name;
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
        foreach (var message in arrived.Take(MaxListed))
        {
            var when = site.FormatTime(message.Received);
            text.Append("From: ").Append(message.Name).Append(" (").Append(message.Email).Append(")\n")
                .Append("Received: ").Append(when).Append('\n');
            html.Append("<hr>\n<p><strong>").Append(Encode(message.Name)).Append("</strong> (<a href=\"")
                .Append(Encode(MailtoOf(message.Email))).Append("\">").Append(Encode(message.Email)).Append("</a>)<br>\n")
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

        if (arrived.Count > MaxListed)
        {
            var more = string.Create(CultureInfo.InvariantCulture, $"{arrived.Count - MaxListed} more messages aren't listed here.");
            text.Append(more).Append("\n\n");
            html.Append("<hr>\n<p>").Append(Encode(more)).Append("</p>\n");
        }

        var footer = arrived.Count == 1 ? "Read, keep or delete it under Messages in the site's admin" : "Read, keep or delete them under Messages in the site's admin";
        text.Append(link is null ? footer + "." : $"{footer}: {link}").Append('\n');
        html.Append("<hr>\n<p>").Append(Encode(footer));
        if (link is not null)
        {
            html.Append(": <a href=\"").Append(Encode(link)).Append("\">").Append(Encode(link)).Append("</a>");
        }

        html.Append(".</p>\n");
        return new DigestMail(recipient, subject, text.ToString(), html.ToString());
    }

    /// <summary>
    /// A <c>mailto:</c> link to an address, its local part and domain percent-encoded (RFC 6068),
    /// so characters an address may hold, such as <c>?</c>, <c>&amp;</c> or <c>%</c>, stay part of
    /// the address and never add fields to the mail.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The link.</returns>
    public static string MailtoOf(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var at = address.LastIndexOf('@', StringComparison.Ordinal);
        return at < 0
            ? "mailto:" + Uri.EscapeDataString(address)
            : "mailto:" + Uri.EscapeDataString(address[..at]) + "@" + Uri.EscapeDataString(address[(at + 1)..]);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
