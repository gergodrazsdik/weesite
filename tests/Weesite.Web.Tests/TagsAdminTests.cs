using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Tag colours and descriptions, and the banner (ADR 0006, T-0085).</summary>
internal sealed partial class TagsAdminTests
{
    [Test]
    public async Task Admins_give_tags_a_colour_and_a_description()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        // The pages of the list with "web" and "welcome" (30 tags a page, in order; T-0131).
        var tags = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Tags.Keys.Order(StringComparer.Ordinal).ToList();
        string PageOf(string tag) => $"/admin/tags?page={(tags.IndexOf(tag) / 30) + 1}";
        var webPage = await admin.GetStringAsync(new Uri(PageOf("web"), UriKind.Relative));
        var web = Row(Fields(webPage), "web");
        var page = await admin.GetStringAsync(new Uri(PageOf("welcome"), UriKind.Relative));
        var fields = Fields(page);
        var welcome = Row(fields, "welcome");

        // T-0094: the select and its options carry their colours.
        await Assert.That(webPage).Contains($"<select id=\"tag-colour-{web}\" class=\"colour-select teal\" name=\"Form.Tags[{web}].Colour\"");
        await Assert.That(page).Contains("<option class=\"pink\" value=\"pink\">pink</option>");

        // A save of one page keeps the other pages' tags as they are ("web" among them).
        fields[$"Form.Tags[{welcome}].Colour"] = "pink";
        fields[$"Form.Tags[{welcome}].Description"] = "the first post";
        using var saved = await Post(admin, page, fields);
        var tag = await visitor.GetStringAsync(new Uri("/blog/tags/welcome", UriKind.Relative));
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(saved.Headers.Location?.PathAndQuery).StartsWith("/admin/tags?saved=1");
        await Assert.That(tag).Contains("<span class=\"tag big pink\">#welcome</span>");
        await Assert.That(tag).Contains("<p class=\"lede\">1 post about the first post.</p>");
        await Assert.That(home).Contains("<a class=\"tag teal\" href=\"/blog/tags/web\" rel=\"tag\">#web</a>");
        await Assert.That(json).Contains("\"colour\": \"pink\"");
        await Assert.That(json).Contains("\"description\": \"web pages: markup, CSS and accessibility\"");
        await Assert.That(json).DoesNotContain("topics");
    }

    [Test]
    public async Task A_colour_that_isnt_a_choice_is_refused()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        var tags = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Tags.Keys.Order(StringComparer.Ordinal).ToList();
        var page = await admin.GetStringAsync(new Uri($"/admin/tags?page={(tags.IndexOf("web") / 30) + 1}", UriKind.Relative));
        var fields = Fields(page);
        fields[$"Form.Tags[{Row(fields, "web")}].Colour"] = "url(evil)";
        using var response = await Post(admin, page, fields);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(WebUtility.HtmlDecode(html)).Contains("has a colour that isn't one of the choices");
        await FieldErrors.AssertMarkedAsync(html, $"tag-colour-{Row(fields, "web")}");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);
    }

    [Test]
    [Arguments("none", null)]
    [Arguments("uploaded", null)]
    [Arguments("/media/no-such-image.jpg", "The banner must be empty, none, or an image from Media")]
    [Arguments("https://example.org/x.jpg", "The banner must be empty, none, or an image from Media")]
    public async Task The_banner_is_the_drawing_none_or_a_stored_image(string banner, string? error)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        if (banner == "uploaded")
        {
            // This test host's media store starts empty: upload the image first.
            banner = await Upload(admin);
        }

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        var fields = Fields(page);
        fields["Form.Banner"] = banner;
        fields["Form.HomeShow"] = "latest-posts";
        fields["_handler"] = "settings";
        using var response = await Post(admin, page, fields, "/admin/settings");
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));

        if (error is null)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(home.Contains("class=\"banner", StringComparison.Ordinal)).IsEqualTo(banner != "none");
            await Assert.That(home.Contains($"<img class=\"banner\" src=\"{banner}\"", StringComparison.Ordinal)).IsEqualTo(banner.StartsWith("/media/", StringComparison.Ordinal));
        }
        else
        {
            await Assert.That(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync())).Contains(error);
        }
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    // The form's fields as the page filled them: inputs (text and hidden) and selected options.
    private static Dictionary<string, string> Fields(string page)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["_handler"] = "tags" };
        foreach (Match input in Input().Matches(page))
        {
            var name = Regex.Match(input.Value, " name=\"([^\"]+)\"").Groups[1].Value;
            if (name.StartsWith("Form.", StringComparison.Ordinal) && !input.Value.Contains("type=\"radio\"", StringComparison.Ordinal))
            {
                fields[name] = WebUtility.HtmlDecode(Regex.Match(input.Value, " value=\"([^\"]*)\"").Groups[1].Value);
            }
        }

        foreach (Match select in Select().Matches(page))
        {
            var chosen = Regex.Match(select.Groups[2].Value, "<option (?:class=\"[^\"]*\" )?value=\"([^\"]*)\" selected");
            fields[select.Groups[1].Value] = chosen.Success ? chosen.Groups[1].Value : "";
        }

        return fields;
    }

    // Uploads the seed's sample image through the media page and returns its address.
    private static async Task<string> Upload(HttpClient admin)
    {
        var media = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png")));
        form.Add(file, "file", "banner.png");
        using var alt = new StringContent("A blue square");
        form.Add(alt, "alt");
        using var token = new StringContent(Token().Match(media).Groups[1].Value);
        form.Add(token, "__RequestVerificationToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/admin/media", UriKind.Relative)) { Content = form };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await admin.SendAsync(request);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("path").GetString()!;
    }

    private static int Row(Dictionary<string, string> fields, string tag) =>
        int.Parse(fields.First(f => f.Key.EndsWith("].Tag", StringComparison.Ordinal) && f.Value == tag).Key.Split('[', ']')[1], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<HttpResponseMessage> Post(HttpClient client, string page, Dictionary<string, string> fields, string path = "/admin/tags")
    {
        fields["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent(fields);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    [GeneratedRegex("<input [^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("<select [^>]*name=\"([^\"]+)\"[^>]*>(.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
