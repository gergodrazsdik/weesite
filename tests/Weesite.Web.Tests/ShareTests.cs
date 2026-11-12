namespace Weesite.Web.Tests;

/// <summary>The Share button on posts (T-0075), as the server renders it.</summary>
internal sealed class ShareTests
{
    [Test]
    public async Task A_post_renders_a_hidden_share_button_and_its_closed_dialog()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var page = await client.GetStringAsync(new Uri("/about", UriKind.Relative));

        // Hidden until share.js runs, so there is no button without JavaScript.
        await Assert.That(post).Contains("<button type=\"button\" class=\"btn share-open\" aria-haspopup=\"dialog\" hidden>");
        await Assert.That(post).Contains("<dialog class=\"share-dialog\" aria-labelledby=\"share-heading\">");
        await Assert.That(post).DoesNotContain("<dialog class=\"share-dialog\" aria-labelledby=\"share-heading\" open");
        await Assert.That(post).Contains("href=\"https://www.linkedin.com/sharing/share-offsite/?url=http%3A%2F%2Flocalhost%2Fblog%2Fhello-world\" target=\"_blank\" rel=\"noopener noreferrer\"");
        await Assert.That(post).Contains("href=\"mailto:?subject=Hello%2C%20world&amp;body=Hello%2C%20world%0Ahttp%3A%2F%2Flocalhost%2Fblog%2Fhello-world\"");
        await Assert.That(post).Contains("data-url=\"http://localhost/blog/hello-world\"");
        await Assert.That(post).Contains("/share.");
        await Assert.That(page).DoesNotContain("<ws-share");
    }
}
