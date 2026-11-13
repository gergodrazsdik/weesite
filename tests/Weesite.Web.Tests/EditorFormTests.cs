using Weesite.Content;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The editor form turned into a draft.</summary>
internal sealed class EditorFormTests
{
    [Test]
    [Arguments("published", "2026-11-14 9:0", true)]
    [Arguments("draft", "half typed", true)]
    [Arguments("scheduled", "half typed", false)]
    [Arguments("scheduled", "2026-11-14 09:00", true)]
    public async Task The_publish_time_counts_only_for_a_scheduled_item(string status, string publishAt, bool valid)
    {
        // F-0114: a half-typed time left behind after picking another status refused the save.
        var form = EditorForm.From(new ContentDraft { Kind = ContentKind.Post, Title = "A", Slug = "a", Date = new DateOnly(2026, 11, 1) }, "posts/a.md", "etag");
        form.Status = status;
        form.PublishAt = publishAt;

        var draft = form.ToDraft("en", out var error);

        await Assert.That(draft is not null).IsEqualTo(valid);
        await Assert.That(error is null).IsEqualTo(valid);
        await Assert.That(draft?.PublishAt).IsEqualTo(status == "scheduled" && valid ? new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero) : null);
    }
}
