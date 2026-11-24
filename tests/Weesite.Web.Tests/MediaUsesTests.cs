using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The media page's count of the posts and pages using each file (T-0112).</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class MediaUsesTests(SharedSite shared)
{
    [Test]
    public async Task Each_file_says_where_its_used_and_unused_ones_can_be_listed_alone()
    {
        var factory = shared.Factory;
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var all = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        var unused = await admin.GetStringAsync(new Uri("/admin/media?show=unused", UriKind.Relative));

        var diagram = Card(all, "0e3d1bf78702-diagram.svg");
        await Assert.That(diagram).Contains("<summary>Used in 1</summary>");
        await Assert.That(diagram).Contains("<a href=\"/admin/edit?path=posts%2Fnotes-on-static-rendering.md\">Notes on static rendering</a>");
        await Assert.That(unused).DoesNotContain(">0e3d1bf78702-diagram.svg<");
        await Assert.That(unused).Contains("aria-current=\"page\">Unused <span>");
        await Assert.That(Count(unused, "Not used")).IsEqualTo(Count(all, "Not used"));
        await Assert.That(unused).DoesNotContain("<summary>Used in");
    }

    [Test]
    public async Task An_svg_has_a_thumbnail_and_file_links_open_in_a_new_tab()
    {
        // T-0120.
        var factory = shared.Factory;
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var page = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        var picker = await admin.GetStringAsync(new Uri("/admin/media.json", UriKind.Relative));

        await Assert.That(page).Contains("<img src=\"/media/0e3d1bf78702-diagram.svg\" alt loading=\"lazy\" />");
        await Assert.That(page).Contains("<a class=\"name\" href=\"/media/0e3d1bf78702-diagram.svg\" target=\"_blank\" rel=\"noopener\">0e3d1bf78702-diagram.svg<span class=\"visually-hidden\"> (opens in a new tab)</span></a>");
        await Assert.That(picker).Contains("\"thumbnail\":\"/media/0e3d1bf78702-diagram.svg\"");
    }

    [Test]
    public async Task The_sidebars_media_count_isnt_a_listing_on_every_page_and_follows_an_archive()
    {
        // F-0248: every admin page listed the whole media container for the count.
        using var copy = new SiteCopy();
        var folder = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"), Path.Combine(folder, "b368a21f9e93-sample.png"));
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "0e3d1bf78702-diagram.svg"), Path.Combine(folder, "0e3d1bf78702-diagram.svg"));
        var media = new CountingMedia(new Weesite.Content.Sources.FileMediaStore(folder));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<Weesite.Content.Sources.IMediaStore>(media)));
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        // The content's own load lists media too (ListWithSizesAsync, not counted); the first page
        // may still meet it.
        _ = await admin.GetStringAsync(new Uri("/admin", UriKind.Relative));
        var first = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));
        var listed = media.Listings;
        var second = await admin.GetStringAsync(new Uri("/admin/pages", UriKind.Relative));
        var again = media.Listings - listed;
        var page = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        using var form = new FormUrlEncodedContent([new("name", "0e3d1bf78702-diagram.svg"), new("archived", "true"), new("__RequestVerificationToken", Forms.Token().Match(page).Groups[1].Value)]);
        using var archived = await admin.PostAsync(new Uri("/admin/media/archive", UriKind.Relative), form);
        var after = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));

        await Assert.That(first).Contains("Media<span><span class=\"visually-hidden\">, </span>2</span>");
        await Assert.That(second).Contains("Media<span><span class=\"visually-hidden\">, </span>2</span>");
        await Assert.That(again).IsEqualTo(0);
        await Assert.That(after).Contains("Media<span><span class=\"visually-hidden\">, </span>1</span>");
    }

    [Test]
    public async Task The_sidebars_media_count_follows_every_change_to_the_files()
    {
        // The kept count was dropped only by Media's own archive and replace: an archive made
        // elsewhere, such as the old file an app's replace_media archives after the content has
        // reloaded, left it wrong for up to a minute. Every change to the files counts again now.
        using var copy = new SiteCopy();
        var folder = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"), Path.Combine(folder, "b368a21f9e93-sample.png"));
        File.Copy(Path.Combine(SiteFactory.SeedPath, "media", "0e3d1bf78702-diagram.svg"), Path.Combine(folder, "0e3d1bf78702-diagram.svg"));
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        var library = factory.Services.GetRequiredService<Weesite.Content.Media.MediaLibrary>();
        async Task<string> SidebarAsync() =>
            System.Text.RegularExpressions.Regex.Match(await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative)), "Media<span><span class=\"visually-hidden\">, </span>(\\d+)</span>").Groups[1].Value;

        var first = await SidebarAsync();
        await library.SetArchivedAsync("0e3d1bf78702-diagram.svg", archived: true, CancellationToken.None);
        var archived = await SidebarAsync();
        await library.SetArchivedAsync("0e3d1bf78702-diagram.svg", archived: false, CancellationToken.None);
        var back = await SidebarAsync();
        var uploaded = (Weesite.Content.Media.MediaUploaded)await library.UploadAsync("notes.txt", "Notes."u8.ToArray(), CancellationToken.None);
        var more = await SidebarAsync();
        await library.DeleteAsync(uploaded.Item.Name, CancellationToken.None);
        var deleted = await SidebarAsync();

        await Assert.That(string.Join(' ', first, archived, back, more, deleted)).IsEqualTo("2 1 2 3 2");
    }

    // Media in files, counting how often the whole container is listed.
    private sealed class CountingMedia(Weesite.Content.Sources.IMediaStore inner) : Weesite.Content.Sources.IMediaStore
    {
        private int _listings;

        public int Listings => _listings;

        public Task<Weesite.Content.Sources.MediaFile?> OpenAsync(string name, CancellationToken cancellationToken) => inner.OpenAsync(name, cancellationToken);

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listings);
            return inner.ListAsync(cancellationToken);
        }

        public Task<IReadOnlyList<Weesite.Content.Sources.StoredMedia>> ListWithSizesAsync(CancellationToken cancellationToken) => inner.ListWithSizesAsync(cancellationToken);

        public Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => inner.WriteAsync(name, data, cancellationToken);

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken) => inner.DeleteAsync(name, cancellationToken);
    }

    private static string Card(string page, string name)
    {
        var start = page.IndexOf($">{name}<", StringComparison.Ordinal);
        var end = page.IndexOf("</li>", page.IndexOf("class=\"copy\"", start, StringComparison.Ordinal), StringComparison.Ordinal);
        return page[start..end];
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
