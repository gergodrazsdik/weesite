using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The site settings editor (T-0049).</summary>
internal sealed partial class SettingsTests
{
    private static readonly (string, string)[] Valid =
    [
        ("Name", "Renamed Site"), ("Tagline", "A new tagline"), ("Language", "en-GB"), ("ContactEmail", "hello@example.org"),
        ("DateFormat", "d MMMM yyyy"), ("HomeShow", "latest-posts"), ("Links[0].Label", "Code"), ("Links[0].Url", "https://example.org/code"),
    ];

    [Test]
    public async Task An_empty_index_isnt_called_a_missing_model()
    {
        using var copy = new SiteCopy();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(copy.Root, "content"), "*.md", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative)));

        // F-0091 L24.
        await Assert.That(page).Contains("Nothing is indexed yet: there are no published posts or pages to search.");
        await Assert.That(page).DoesNotContain("there's no embedding model");
    }

    [Test]
    [Arguments("yyyy-MM-dd", true)]
    [Arguments("d MMMM yyyy", true)]
    [Arguments("yyyy. MMMM d.", true)]
    [Arguments("dddd, d MMMM yyyy", true)]
    [Arguments("'the' d MMMM yyyy", true)]
    [Arguments("d \\o\\f MMMM yyyy", true)]
    [Arguments("garbage", false)]
    [Arguments("MMMM yyyy", false)]
    [Arguments("yyyy-MM-dd tt", false)]
    [Arguments("'unclosed d MMMM yyyy", false)]
    public async Task Date_formats_need_a_year_a_month_and_a_day_and_no_stray_letters(string format, bool valid) =>
        await Assert.That(SettingsForm.IsDateFormat(format)).IsEqualTo(valid);

    [Test]
    public async Task The_first_save_on_a_site_without_settings_creates_them()
    {
        // F-0055: prod starts without site.json; the empty ETag field came back as "", which
        // didn't equal "no file", so every save said someone else had saved.
        using var copy = new SiteCopy();
        File.Delete(Path.Combine(copy.Root, "settings", "site.json"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, Valid);

        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).Contains("Renamed Site");
    }

    [Test]
    public async Task Settings_save_with_a_quoted_etag_as_blob_storage_gives()
    {
        // Blob Storage ETags carry quotes ("0x8DE…"); the file store's don't.
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var existing = services.Last(d => d.ServiceType == typeof(IContentStore));
            services.Remove(existing);
            services.AddSingleton<IContentStore>(provider => new QuotedETags((IContentStore)(existing.ImplementationInstance ?? existing.ImplementationFactory!(provider))));
        }));
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, Valid);

        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
    }

    [Test]
    public async Task Admins_change_the_settings_and_the_rest_of_site_json_stays()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, Valid);
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var post = await visitor.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(page).Contains("value=\"Example Site\"");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(home).Contains("Renamed Site");
        await Assert.That(home).Contains("<a href=\"https://example.org/code\" rel=\"me noopener\" target=\"_blank\">Code");
        await Assert.That(home).Contains("hello@example.org");
        await Assert.That(home).Contains("lang=\"en-GB\"");
        await Assert.That(post).Contains("January 2026</time>");
        await Assert.That(json).Contains("\"example-author\"");
        await Assert.That(json).Contains("\"menu\"");
        await Assert.That(json).Contains("\"dateFormat\": \"d MMMM yyyy\"");
    }

    [Test]
    public async Task The_privacy_and_terms_pages_are_chosen_in_settings_and_linked_from_the_footer()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        // The sample site sets both (T-0096, T-0097).
        var before = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var privacy = await visitor.GetStringAsync(new Uri("/privacy", UriKind.Relative));
        var terms = await visitor.GetStringAsync(new Uri("/terms", UriKind.Relative));

        // Choosing none removes the link.
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, [.. Valid, ("PrivacyPage", ""), ("TermsPage", "about")]);
        var after = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(before).Contains("<a href=\"/privacy\">Privacy</a>");
        await Assert.That(before).Contains("<a href=\"/terms\">Terms</a>");
        await Assert.That(privacy).Contains("Privacy policy");
        await Assert.That(terms).Contains("CC BY 4.0");
        await Assert.That(page).Contains("<option value=\"privacy\" selected");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(after).DoesNotContain(">Privacy</a>");
        await Assert.That(after).Contains("<a href=\"/about\">Terms</a>");
        await Assert.That(json).DoesNotContain("privacyPage");
        await Assert.That(json).Contains("\"termsPage\": \"about\"");
    }

    [Test]
    public async Task The_footer_takes_its_copyright_line_and_credit_from_the_settings()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();
        var year = DateTime.UtcNow.Year;

        // The sample site: its own line, and the credit linking the project.
        var before = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));

        // A custom line, the credit turned off (an unchecked box isn't posted).
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var custom = await Save(admin, page, [.. Valid, ("Copyright", "Copyright (c) 2026-{year} Example authors")]);
        var hidden = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        // Back to the default line, with the credit.
        page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var reset = await Save(admin, page, [.. Valid, ("Copyright", ""), ("ShowBuiltWith", "true")]);
        var after = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(WebUtility.HtmlDecode(before)).Contains($"<p>© 2025–{year} Example Author</p>");
        await Assert.That(before).Contains("<p class=\"built-with\">built with <a href=\"https://github.com/gergodrazsdik/weesite\" target=\"_blank\" rel=\"noopener\">Weesite");
        await Assert.That(before[before.IndexOf("class=\"built-with\"", StringComparison.Ordinal)..]).Contains("Weesite<span class=\"visually-hidden\"> (opens in a new tab)</span></a></p>");
        await Assert.That(custom.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(hidden).Contains($"<p>Copyright (c) 2026-{year} Example authors</p>");
        await Assert.That(hidden).DoesNotContain("built-with");
        await Assert.That(json).Contains("\"hideBuiltWith\": true");
        await Assert.That(WebUtility.HtmlDecode(after)).Contains($"<p>© {year} Renamed Site</p>");
        await Assert.That(after).Contains("class=\"built-with\"");
    }

    [Test]
    [Arguments("Name", "", "Enter the site's name")]
    [Arguments("Language", "xx-not-a-language", "The language must be a code")]
    [Arguments("ContactEmail", "not an address", "isn't an email address")]
    [Arguments("DateFormat", "%", "date format needs a year (y), a month (M) and a day (d)")]
    [Arguments("DateFormat", "garbage", "date format needs a year")]
    [Arguments("DateFormat", "MMMM yyyy", "date format needs a year")]
    [Arguments("DateFormat", "yyyy-MM-dd HH:mm", "date format needs a year")]
    [Arguments("HomeShow", "page", "Choose a published page")]
    [Arguments("PrivacyPage", "no-such-page", "Choose a published page for the privacy policy")]
    [Arguments("TermsPage", "no-such-page", "Choose a published page for the terms")]
    [Arguments("Copyright", "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789x", "copyright line can be at most 160")]
    [Arguments("Links[0].Url", "ftp://example.org/", "needs an address starting with https://")]
    public async Task Invalid_settings_are_refused_with_a_reason(string field, string value, string message)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var response = await Save(admin, page, [.. Valid.Where(f => f.Item1 != field), (field, value)]);

        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(html)).Contains(message);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);
        await FieldErrors.AssertMarkedAsync(html, SettingsFieldIds[field]);
    }

    // The element each posted field is shown in (F-0081).
    private static readonly Dictionary<string, string> SettingsFieldIds = new()
    {
        ["Name"] = "site-name",
        ["Language"] = "language",
        ["ContactEmail"] = "contact",
        ["DateFormat"] = "date-format",
        ["HomeShow"] = "home-page",
        ["PrivacyPage"] = "privacy-page",
        ["TermsPage"] = "terms-page",
        ["Copyright"] = "copyright",
        ["Links[0].Url"] = "link-url-0",
    };

    [Test]
    public async Task A_save_over_someone_elses_is_refused()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var first = await Save(admin, page, Valid);
        using var second = await Save(admin, page, [.. Valid.Where(f => f.Item1 != "Name"), ("Name", "Second")]);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(WebUtility.HtmlDecode(await second.Content.ReadAsStringAsync())).Contains("Someone saved the settings after you opened them");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).Contains("Renamed Site");
    }

    [Test]
    public async Task Authors_cant_change_the_settings()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/settings", UriKind.Relative));

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    private static async Task<HttpResponseMessage> Save(HttpClient client, string page, params (string Name, string Value)[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "settings",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Form.ETag"] = WebUtility.HtmlDecode(ETag().Match(page).Groups[1].Value),
        };
        foreach (var (name, value) in fields)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/settings", UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"Form.ETag\" value=\"([^\"]*)\"")]
    private static partial Regex ETag();

    private sealed class QuotedETags(IContentStore inner) : IContentStore
    {
        public Task<StoredFile?> ReadAsync(string path, CancellationToken cancellationToken) => inner.ReadAsync(path, cancellationToken);

        public Task<string?> WriteAsync(string path, string text, string? ifMatch, CancellationToken cancellationToken) => inner.WriteAsync(path, text, ifMatch, cancellationToken);

        public Task<string?> BumpVersionAsync(CancellationToken cancellationToken) => inner.BumpVersionAsync(cancellationToken);

        public Task<IReadOnlyList<Revision>> ListRevisionsAsync(string path, CancellationToken cancellationToken) => inner.ListRevisionsAsync(path, cancellationToken);

        public Task<string?> ReadRevisionAsync(string path, string versionId, CancellationToken cancellationToken) => inner.ReadRevisionAsync(path, versionId, cancellationToken);

        public Task<bool> DeleteAsync(string path, string ifMatch, CancellationToken cancellationToken) => inner.DeleteAsync(path, ifMatch, cancellationToken);

        public async Task<StoredFile?> ReadSettingsFileAsync(string name, CancellationToken cancellationToken) =>
            await inner.ReadSettingsFileAsync(name, cancellationToken) is { } file ? file with { ETag = $"\"{file.ETag}\"" } : null;

        public async Task<string?> WriteSettingsFileAsync(string name, string text, string? ifMatch, CancellationToken cancellationToken) =>
            await inner.WriteSettingsFileAsync(name, text, ifMatch?.Trim('"'), cancellationToken) is { } etag ? $"\"{etag}\"" : null;
    }
}
