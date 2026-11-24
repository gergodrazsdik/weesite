using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Replacing a media file: a new address, and the posts and pages follow (F-0143).</summary>
internal sealed class MediaReplaceTests
{
    private const string Sample = "b368a21f9e93-sample.png";

    [Test]
    public async Task An_admin_replaces_a_file_and_the_posts_that_use_it_follow()
    {
        using var copy = Copy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var post = Path.Combine(copy.Root, "content", "posts", "notes-on-static-rendering.md");
        var gradient = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "c0f10a37852f-gradient.jpg"));

        var list = await admin.GetStringAsync(new Uri("/admin/media?q=sample", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri($"/admin/media/replace?name={Sample}&back=%2Fadmin%2Fmedia%3Fq%3Dsample", UriKind.Relative));
        using var replaced = await PostAsync(admin, page, Sample, "square.jpg", gradient, "");
        var text = await File.ReadAllTextAsync(post);
        var after = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri(replaced.Headers.Location!.OriginalString, UriKind.Relative)));
        var archived = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "media.json"));

        await Assert.That(list).Contains($"href=\"/admin/media/replace?name={Sample}&amp;back=%2Fadmin%2Fmedia%3Fq%3Dsample\"");
        await Assert.That(page).Contains("Notes on static rendering");
        await Assert.That(page).Contains("accept=\".jpeg,.jpg,.png,.webp\"");
        await Assert.That(replaced.Headers.Location!.OriginalString).StartsWith("/admin/media?q=sample&replaced=c0f10a37852f-square.jpg%7C1");
        await Assert.That(after).Contains("Replaced with c0f10a37852f-square.jpg, which is used in 1 place now.");
        await Assert.That(text).Contains("/media/c0f10a37852f-square.jpg");
        await Assert.That(text).DoesNotContain("/media/" + Sample);
        await Assert.That(archived).Contains(Sample);
        await Assert.That(File.Exists(Path.Combine(copy.Root, "media", Sample))).IsTrue();
    }

    [Test]
    public async Task The_site_settings_count_as_uses_and_follow_a_replacement()
    {
        // F-0237: the logo was listed as unused, deleted and archived without a warning, and a
        // replacement left the settings on the old file.
        const string Logo = "b368a21f9e93-logo.png";
        using var copy = Copy();
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", Sample), Path.Combine(copy.Root, "media", Logo));
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settings))!.AsObject();
        json["logo"] = "/media/" + Logo;
        json["favicon"] = "/media/" + Logo;
        await File.WriteAllTextAsync(settings, json.ToJsonString());
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var gradient = await File.ReadAllBytesAsync(Path.Combine(SiteFactory.SeedPath, "media", "c0f10a37852f-gradient.jpg"));

        var unused = await admin.GetStringAsync(new Uri("/admin/media?show=unused", UriKind.Relative));
        var used = await admin.GetStringAsync(new Uri("/admin/media?show=used", UriKind.Relative));
        var delete = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/admin/media/delete?name={Logo}", UriKind.Relative)));
        var archive = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/admin/media/archive?name={Logo}", UriKind.Relative)));
        var page = await admin.GetStringAsync(new Uri($"/admin/media/replace?name={Logo}", UriKind.Relative));
        using var replaced = await PostAsync(admin, page, Logo, "logo.jpg", gradient, "");
        var after = await File.ReadAllTextAsync(settings);

        await Assert.That(unused).DoesNotContain($">{Logo}<");
        await Assert.That(used).Contains($">{Logo}<");
        await Assert.That(used).Contains("<summary>Used in 2</summary>");
        await Assert.That(used).Contains("<a href=\"/admin/settings#logo\">Site settings: Logo</a>");
        await Assert.That(used).Contains("<a href=\"/admin/settings#favicon\">Site settings: Favicon</a>");
        await Assert.That(delete).Contains("Still used here (2)");
        await Assert.That(archive).Contains("Still used here (2)");
        await Assert.That(page).Contains("Used in 2 places, which will use the new file:");
        await Assert.That(replaced.Headers.Location!.OriginalString).Contains("replaced=c0f10a37852f-logo.jpg%7C1");
        await Assert.That(after).Contains("\"logo\": \"/media/c0f10a37852f-logo.jpg\"");
        await Assert.That(after).Contains("\"favicon\": \"/media/c0f10a37852f-logo.jpg\"");
        await Assert.That(after).DoesNotContain(Logo);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "media.json"))).Contains(Logo);
    }

    [Test]
    public async Task An_image_is_replaced_only_by_an_image()
    {
        using var copy = Copy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri($"/admin/media/replace?name={Sample}", UriKind.Relative));
        using var refused = await PostAsync(admin, page, Sample, "notes.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray(), "");

        await Assert.That(System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(refused.Headers.Location!))).Contains("Replace an image with an image");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "notes-on-static-rendering.md"))).Contains("/media/" + Sample);
    }

    [Test]
    public async Task Authors_cant_replace_files()
    {
        using var copy = Copy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var page = await author.GetAsync(new Uri($"/admin/media/replace?name={Sample}", UriKind.Relative));
        var list = await author.GetStringAsync(new Uri("/admin/media", UriKind.Relative));

        await Assert.That(page.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(list).DoesNotContain("/admin/media/replace");
    }

    private static SiteCopy Copy()
    {
        var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", Sample), Path.Combine(media, Sample));
        return copy;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string page, string name, string fileName, byte[] data, string alt)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(data);
        form.Add(file, "file", fileName);
        using var nameField = new StringContent(name);
        form.Add(nameField, "name");
        using var altField = new StringContent(alt);
        form.Add(altField, "alt");
        using var back = new StringContent(Forms.Token().Match(page) is { Success: true } ? BackOf(page) : "/admin/media");
        form.Add(back, "back");
        using var token = new StringContent(Forms.Token().Match(page).Groups[1].Value);
        form.Add(token, "__RequestVerificationToken");
        return await client.PostAsync(new Uri("/admin/media/replace", UriKind.Relative), form);
    }

    private static string BackOf(string page) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(page, "name=\"back\" value=\"([^\"]*)\"").Groups[1].Value);

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }
}
