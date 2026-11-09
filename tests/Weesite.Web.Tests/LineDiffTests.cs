using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Comparing versions (T-0083): the line diff and the diff page.</summary>
internal sealed class LineDiffTests
{
    [Test]
    public async Task Unchanged_removed_and_added_lines_are_marked_in_order()
    {
        var lines = LineDiff.Compare("title\none\ntwo\nthree", "title\none\n2\nthree\nfour")!;

        await Assert.That(lines.Select(l => $"{l.Kind}:{l.Text}")).IsEquivalentTo(
            ["Same:title", "Same:one", "Removed:two", "Added:2", "Same:three", "Added:four"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Identical_texts_have_only_unchanged_lines_and_long_ones_arent_compared()
    {
        var same = LineDiff.Compare("a\r\nb\n", "a\nb")!;
        var tooLong = LineDiff.Compare(string.Join('\n', Enumerable.Repeat("x", LineDiff.MaxLines + 1)), "x");

        await Assert.That(same.All(l => l.Kind == DiffKind.Same)).IsTrue();
        await Assert.That(same.Count).IsEqualTo(2);
        await Assert.That(tooLong).IsNull();
    }

    [Test]
    [Arguments("/admin/diff?path=posts%2Fhello-world.md&version=no-such-version")]
    [Arguments("/admin/diff?path=posts%2Fno-such-post.md&version=v1")]
    [Arguments("/admin/diff")]
    public async Task The_diff_page_is_not_found_without_a_version_to_compare(string path)
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        using var response = await admin.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(System.Net.HttpStatusCode.NotFound);
    }
}
