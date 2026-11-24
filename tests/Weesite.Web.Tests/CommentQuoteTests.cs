using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Reply's quote header is in one language, its words and its date (U-16).</summary>
internal sealed class CommentQuoteTests
{
    [Test]
    [Arguments("hu", "en", "hu-HU", "{0} írta ({1}):")]
    [Arguments(null, "hu", "hu-HU", "{0} írta ({1}):")]
    [Arguments("", "en-GB", "en-GB", "{0} wrote on {1}:")]
    [Arguments("en", "hu", "en-US", "{0} wrote on {1}:")]
    [Arguments("de", "hu", "hu-HU", "{0} írta ({1}):")]
    [Arguments("de", "fr", "en-US", "{0} wrote on {1}:")]
    public async Task The_header_is_in_the_posts_language_when_the_site_has_its_text(string? post, string site, string culture, string header)
    {
        var chosen = CommentQuote.Culture(post, site);

        await Assert.That(chosen.Name).IsEqualTo(culture);
        await Assert.That(CommentQuote.Header(chosen)).IsEqualTo(header);
    }
}
