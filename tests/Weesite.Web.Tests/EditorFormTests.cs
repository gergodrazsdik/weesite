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

        var draft = form.ToDraft(out var error);

        await Assert.That(draft is not null).IsEqualTo(valid);
        await Assert.That(error is null).IsEqualTo(valid);
        await Assert.That(draft?.PublishAt).IsEqualTo(status == "scheduled" && valid ? new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero) : null);
    }

    [Test]
    [Arguments("  Short  ", "Short")]
    [Arguments("   ", null)]
    public async Task The_seo_title_is_trimmed_and_empty_means_none(string typed, string? saved)
    {
        var form = EditorForm.From(new ContentDraft { Kind = ContentKind.Post, Title = "A", Slug = "a", Date = new DateOnly(2026, 11, 1) }, "posts/a.md", "etag");
        form.SeoTitle = typed;

        var draft = form.ToDraft(out _);

        await Assert.That(draft!.SeoTitle).IsEqualTo(saved);
        await Assert.That(draft.ToMarkdown().Contains("seo_title", StringComparison.Ordinal)).IsEqualTo(saved is not null);
    }

    [Test]
    [Arguments("", "A scheduled item needs a time in Publish at, such as 2026-11-10 09:00.")]
    [Arguments("tomorrow", "Publish at isn't a date and time such as 2026-11-10 09:00.")]
    public async Task A_scheduling_error_names_the_field_by_its_label(string publishAt, string message)
    {
        // U-44: the field's visible label, not the front matter's publish_at; the page links the field.
        var form = EditorForm.From(new ContentDraft { Kind = ContentKind.Post, Title = "A", Slug = "a", Date = new DateOnly(2026, 11, 1) }, "posts/a.md", "etag");
        form.Status = "scheduled";
        form.PublishAt = publishAt;

        var draft = form.ToDraft(out var error);

        await Assert.That(draft).IsNull();
        await Assert.That(error).IsEqualTo(message);
        await Assert.That(EditorErrors.FieldOf(error)).IsEqualTo("publish-at");
    }
}
