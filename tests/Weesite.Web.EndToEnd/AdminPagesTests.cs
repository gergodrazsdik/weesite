using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The admin pages after the UI and UX review (T-0177) in a real browser, signed in as an admin:
/// the one way of asking before an action, the menu's Remove, the analytics' pause and chart, and
/// the focus after a media action. A site of their own: they archive, delete and save.
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.Keyed, Key = "admin-pages")]
internal sealed class AdminPagesTests(SiteServer site) : PageTest
{
    // An upload answers once the content has reloaded, which on a busy CI runner can take a while.
    private const float UploadTimeout = 30_000;

    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task Deleting_a_message_asks_with_a_heading_then_cancel_then_the_action()
    {
        // U-02: the dialog has a heading with the question, the text under it, Cancel then Delete.
        await Page.OpenAsync(site.At("/admin/messages"));
        var delete = Page.GetByRole(AriaRole.Button, new() { Name = "Delete the message from Bob Example" });
        await delete.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Alertdialog, new() { Name = "Delete the message from Bob Example?" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = "Delete the message from Bob Example?" })).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync("It goes for good: this can't be undone.");
        await Expect(dialog.GetByRole(AriaRole.Button)).ToHaveTextAsync(["Cancel", "Delete"]);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeFocusedAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(delete).ToBeFocusedAsync();
    }

    [Test]
    public async Task A_typed_confirmation_has_no_copy_and_cancel_comes_first()
    {
        // U-02: typing the slug is the check; a Copy button beside it would make it none.
        await Page.OpenAsync(site.At("/admin/delete?path=posts/hello-world.md"));

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Delete “Hello, world” for good?", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Type the slug, hello-world, to confirm")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Copy" })).ToHaveCountAsync(0);
        await Expect(Page.Locator("form[action='/admin/delete'] .form-actions > *")).ToHaveTextAsync(["Cancel", "Delete permanently"]);
    }

    [Test]
    public async Task Archiving_a_file_asks_in_the_same_dialog_and_the_focus_goes_to_the_notice()
    {
        var text = Path.Combine(Path.GetTempPath(), $"archive-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(text, "A file to archive.");
        await Page.OpenAsync(site.At("/admin/media"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Upload a file" }).ClickAsync();
        await Page.Locator("#file").SetInputFilesAsync(text);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Upload", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("uploaded="), new() { Timeout = UploadTimeout });
        File.Delete(text);

        // U-31: after an upload, the notice with the Markdown has the focus.
        await Expect(Page.Locator(".notice[data-focus]")).ToBeFocusedAsync();
        var name = await Page.Locator("#snippet").InputValueAsync();
        name = Regex.Match(name, @"/media/([^)]+)\)").Groups[1].Value;

        // U-02: Archive asks in the dialog: its heading, Cancel then Archive.
        await Page.OpenAsync(site.At("/admin/media?q=archive-"));
        await Page.GetByRole(AriaRole.Link, new() { Name = $"Archive {name}" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = $"Archive {name}?" });
        await Expect(dialog.GetByRole(AriaRole.Heading, new() { Name = $"Archive {name}?" })).ToBeVisibleAsync();
        await Expect(dialog.Locator(".form-actions > *")).ToHaveTextAsync(["Cancel", "Archive"]);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Archive" }).ClickAsync();

        // U-31: back on the list, the notice says what happened and has the focus.
        var notice = Page.GetByRole(AriaRole.Status).Filter(new() { HasText = $"Archived {name}." });
        await Expect(notice).ToBeFocusedAsync();
    }

    [Test]
    public async Task A_menu_item_is_removed_with_its_button_and_empty_rows_have_none()
    {
        // U-57: empty rows have no buttons; Remove empties a row and says so; saving drops it.
        await Page.OpenAsync(site.At("/admin/menu"));
        var rows = Page.Locator(".menu-row");
        var empty = rows.Nth(3);
        await Expect(empty.GetByLabel("Label")).ToHaveValueAsync("");
        await Expect(empty.GetByRole(AriaRole.Button, new() { Name = "Move up" })).ToBeHiddenAsync();
        await Expect(empty.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeHiddenAsync();

        var about = rows.Nth(1);
        await Expect(about.GetByLabel("Label")).ToHaveValueAsync("About");
        await about.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();

        await Expect(about.GetByLabel("Label")).ToHaveValueAsync("");
        await Expect(about.GetByLabel("Label")).ToBeFocusedAsync();
        await Expect(about.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("About removed. Save to apply.");
        await Expect(Page.Locator(".menu-preview li")).ToHaveTextAsync(["Blog"]);
        await Expect(rows.Nth(0).GetByRole(AriaRole.Button, new() { Name = "Move down" })).ToBeDisabledAsync();

        // Typing in an empty row gives it its buttons.
        await empty.GetByLabel("Label").FillAsync("Notes");
        await Expect(empty.GetByRole(AriaRole.Button, new() { Name = "Move up" })).ToBeEnabledAsync();
        await empty.GetByLabel("Label").FillAsync("");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".menu-preview li")).ToHaveTextAsync(["Blog"]);
    }

    [Test]
    public async Task Analytics_shows_when_it_is_paused_and_a_days_column_opens_it_in_the_table()
    {
        await Page.OpenAsync(site.At("/admin/analytics"));
        var updated = Page.Locator(".analytics-updated");

        // U-05: the site's zone (UTC here) and 24-hour clock, with seconds.
        await Expect(updated).ToHaveTextAsync(new Regex(@"^Updated at \d{2}:\d{2}:\d{2} UTC$"));

        // U-52: paused, the line says so, not only the button.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Pause updates" }).ClickAsync();
        await Expect(updated).ToHaveTextAsync(new Regex(@"^Updates paused\. Last updated at \d{2}:\d{2}:\d{2} UTC$"));
        await Expect(Page.Locator(".analytics-refresh.paused")).ToHaveCountAsync(1);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Resume updates" }).ClickAsync();
        await Expect(updated).ToHaveTextAsync(new Regex("^Updated at "));

        // U-52: a column opens the table at its day, marked and focused.
        var column = Page.Locator(".day-chart .column").Last;
        var day = await column.GetAttributeAsync("data-day");
        await column.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Page views by people, per day" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator("tr.picked th")).ToHaveTextAsync(day!);
        await Expect(dialog.Locator("tr.picked th")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog.Locator("tr.picked")).ToHaveCountAsync(0);
    }

    [Test]
    [Arguments("/admin/comments?tab=approved")]
    [Arguments("/admin/audit")]
    [Arguments("/admin/editors")]
    [Arguments("/admin/backup")]
    public async Task Admin_page_has_no_axe_violations(string path)
    {
        await Page.OpenAsync(site.At(path));

        var axe = await Page.RunAxe();

        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
