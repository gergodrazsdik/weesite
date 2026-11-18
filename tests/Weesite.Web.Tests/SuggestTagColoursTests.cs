using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Suggested tag colours (T-0142).</summary>
internal sealed class SuggestTagColoursTests
{
    [Test]
    public async Task A_group_keeps_the_colour_most_of_its_tags_have_and_new_groups_get_free_ones()
    {
        var site = SiteSettings.Empty with
        {
            Tags = new Dictionary<string, TagDetails>(StringComparer.Ordinal)
            {
                ["azure"] = new() { Colour = "violet" },
                ["opentofu"] = new() { Colour = "violet" },
                ["ci"] = new() { Colour = "red" },
                ["web"] = new() { Colour = "violet", Description = "Web pages." },
            },
        };

        var groups = TagColourSuggestions.Suggest([["azure", "ci", "opentofu"], ["css", "web"], ["travel"]], site);

        // The largest group keeps violet; the next can't, so it gets the first free colour.
        await Assert.That(groups.Select(g => g.Colour)).IsEquivalentTo(["violet", "blue", "amber"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(groups[0].Tags.Single(t => t.Tag == "ci")).IsEqualTo(new SuggestedTag("ci", "red", "violet"));
    }

    [Test]
    public async Task The_page_changes_nothing_until_the_ticked_colours_are_applied()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var settingsFile = Path.Combine(copy.Root, "settings", "site.json");
        var before = await File.ReadAllTextAsync(settingsFile);

        var tagsPage = await admin.GetStringAsync(new Uri("/admin/tags", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/tags/suggest", UriKind.Relative));
        var fields = Forms.Values(page, "tag-colours", "apply");
        var ticked = fields.Where(f => f.Key.EndsWith(".Apply", StringComparison.Ordinal)).ToList();
        var rowOf = (KeyValuePair<string, string> f) => f.Key[..f.Key.LastIndexOf('.', StringComparison.Ordinal)];
        var applied = rowOf(ticked[0]);
        var tagOf = (string row) => fields.Single(f => f.Key == row + ".Tag").Value;
        var colourOf = (string row) => fields.Single(f => f.Key == $"Form.Groups[{fields.Single(g => g.Key == row + ".Group").Value}].Colour").Value;
        await Assert.That(await File.ReadAllTextAsync(settingsFile)).IsEqualTo(before);

        // Only the first ticked row stays ticked.
        using var post = new FormUrlEncodedContent(fields.Where(f => !f.Key.EndsWith(".Apply", StringComparison.Ordinal) || rowOf(f) == applied));
        using var response = await admin.PostAsync(new Uri("/admin/tags/suggest", UriKind.Relative), post);
        var stored = factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Site;
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(tagsPage).Contains("href=\"/admin/tags/suggest\"");
        await Assert.That(page).Contains("<h1>Suggested tag colours</h1>");
        await Assert.That(ticked.Count).IsGreaterThan(1);
        await Assert.That(response.Headers.Location?.AbsolutePath).IsEqualTo("/admin/tags");
        await Assert.That(stored.ColourOf(tagOf(applied))).IsEqualTo(colourOf(applied));
        foreach (var other in ticked.Skip(1).Select(rowOf))
        {
            await Assert.That(stored.ColourOf(tagOf(other))).IsNotEqualTo(colourOf(other));
        }

        // Descriptions stay.
        await Assert.That(stored.DescriptionOf("web")).IsEqualTo("web pages: markup, CSS and accessibility");
        await Assert.That(audit.Select(e => $"{e.Action} {e.Target}")).Contains("tags-changed site.json (suggested colours)");
    }

    [Test]
    public async Task Applying_nothing_writes_nothing_and_authors_cant_see_the_page()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var author = Client(factory, EditorRole.Author);
        var settingsFile = Path.Combine(copy.Root, "settings", "site.json");
        var written = File.GetLastWriteTimeUtc(settingsFile);

        var page = await admin.GetStringAsync(new Uri("/admin/tags/suggest", UriKind.Relative));
        using var post = new FormUrlEncodedContent(Forms.Values(page, "tag-colours", "apply").Where(f => !f.Key.EndsWith(".Apply", StringComparison.Ordinal)));
        using var response = await admin.PostAsync(new Uri("/admin/tags/suggest", UriKind.Relative), post);
        var after = await admin.GetStringAsync(new Uri("/admin/tags", UriKind.Relative));
        using var forbidden = await author.GetAsync(new Uri("/admin/tags/suggest", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(after).Contains("Nothing had changed, so no new version was saved.");
        await Assert.That(File.GetLastWriteTimeUtc(settingsFile)).IsEqualTo(written);
        await Assert.That(forbidden.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
    }

    [Test]
    public async Task A_group_can_have_another_colour_shown_first_then_applied()
    {
        // T-0144: the maintainer wanted to choose a group's colour, not only take the suggestion.
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var settingsFile = Path.Combine(copy.Root, "settings", "site.json");
        var written = File.GetLastWriteTimeUtc(settingsFile);
        var page = await admin.GetStringAsync(new Uri("/admin/tags/suggest", UriKind.Relative));
        var suggested = Forms.Values(page, "tag-colours", "update").Single(f => f.Key == "Form.Groups[0].Colour").Value;
        var other = TagDetails.Colours.First(c => c != suggested);
        List<KeyValuePair<string, string>> Chosen(string html, string button) =>
            [.. Forms.Values(html, "tag-colours", button).Select(f => f.Key == "Form.Groups[0].Colour" ? new(f.Key, other) : f)];

        // Shown, not saved: the group's rows now say the chosen colour, and the first is ticked.
        using var update = new FormUrlEncodedContent(Chosen(page, "update"));
        using var shown = await admin.PostAsync(new Uri("/admin/tags/suggest", UriKind.Relative), update);
        var html = await shown.Content.ReadAsStringAsync();
        var first = Forms.Values(html, "tag-colours", "apply");
        var firstTag = first.Single(f => f.Key == "Form.Rows[0].Tag").Value;
        await Assert.That(shown.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(File.GetLastWriteTimeUtc(settingsFile)).IsEqualTo(written);
        await Assert.That(first.Single(f => f.Key == "Form.Groups[0].Colour").Value).IsEqualTo(other);
        await Assert.That(first.Any(f => f.Key == "Form.Rows[0].Apply")).IsTrue();
        await Assert.That(html).Contains($"to {other}</td>");

        // Applied: the first group's ticked tags get the chosen colour.
        using var apply = new FormUrlEncodedContent(first);
        using var applied = await admin.PostAsync(new Uri("/admin/tags/suggest", UriKind.Relative), apply);
        var stored = factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Site;

        await Assert.That(applied.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(stored.ColourOf(firstTag)).IsEqualTo(other);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }
}
