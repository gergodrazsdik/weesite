using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Saving a settings page with nothing changed (F-0175).</summary>
internal sealed class UnchangedSettingsSaveTests
{
    [Test]
    [Arguments("/admin/settings", "settings")]
    [Arguments("/admin/menu", "menu")]
    [Arguments("/admin/tags", "tags")]
    [Arguments("/admin/theme", "theme")]
    [Arguments("/admin/profile", "profile")]
    public async Task A_settings_page_saved_without_changes_writes_nothing_and_says_so(string path, string form)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        var audit = factory.Services.GetRequiredService<IAuditLog>();

        // The first save may rewrite a file written by hand (or create the editor's profile); the
        // second, of the page as it then shows, has nothing to change.
        using (var first = await SaveAsync(admin, path, form))
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Redirect).Because(Notice(await first.Content.ReadAsStringAsync()));
        }

        var files = Stamps(copy.Root);
        var entries = (await audit.LatestAsync(50, CancellationToken.None)).Count;
        using var second = await SaveAsync(admin, path, form);
        var after = await admin.GetStringAsync(new Uri(path, UriKind.Relative));

        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.Redirect).Because(Notice(await second.Content.ReadAsStringAsync()));
        await Assert.That(after).Contains("Nothing had changed, so no new version was saved.");
        await Assert.That(Stamps(copy.Root)).IsEquivalentTo(files);
        await Assert.That((await audit.LatestAsync(50, CancellationToken.None)).Count).IsEqualTo(entries);
    }

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string path, string form)
    {
        var page = await client.GetStringAsync(new Uri(path, UriKind.Relative));
        using var content = new FormUrlEncodedContent(Forms.Values(page, form, button: "save"));
        return await client.PostAsync(new Uri(path, UriKind.Relative), content);
    }

    // What the page said instead of saving.
    private static string Notice(string page) =>
        System.Text.RegularExpressions.Regex.Match(page, "<p class=\"notice[^\"]*\"[^>]*>(.*?)</p>", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Groups[1].Value;

    private static Dictionary<string, DateTime> Stamps(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "settings")).ToDictionary(f => f, File.GetLastWriteTimeUtc);
}
