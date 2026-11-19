using System.Diagnostics.CodeAnalysis;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;
using Weesite.Web.Visitors;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The contact form in a browser (ADR 0011): with JavaScript the message carries a proof of work
/// and is verified; without, it's still sent, as unverified. The honeypot stays out of reach.
/// </summary>
[ClassDataSource<ContactSite>(Shared = SharedType.PerClass)]
internal sealed class ContactTests(ContactSite site) : PageTest
{
    private static readonly AxeRunOptions WcagAa = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa", "best-practice"] },
    };

    [Test]
    public async Task With_javascript_a_message_is_sent_with_a_proof_and_kept_verified()
    {
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/contact"));

        await Page.GetByLabel("Your name").FillAsync("Script Sender");
        await Page.GetByLabel("Your email address").FillAsync("script@example.org");
        await Page.GetByLabel("Message", new() { Exact = true }).FillAsync("Sent with JavaScript.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(site.At("/contact?sent=1"));
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Thank you" })).ToBeVisibleAsync();
        var message = site.Messages.Single(m => m.Name == "Script Sender");
        await Assert.That(message.Unverified).IsFalse();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }

    [Test]
    public async Task Without_javascript_a_message_is_still_sent_but_unverified()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();
        await page.OpenAsync(site.At("/contact"));

        await page.GetByLabel("Your name").FillAsync("Plain Sender");
        await page.GetByLabel("Your email address").FillAsync("plain@example.org");
        await page.GetByLabel("Message", new() { Exact = true }).FillAsync("Sent without JavaScript.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(site.At("/contact?sent=1"));
        await Expect(page.GetByText("Thank you: your message is on its way.")).ToBeVisibleAsync();
        await Assert.That(site.Messages.Single(m => m.Name == "Plain Sender").Unverified).IsTrue();
    }

    [Test]
    public async Task The_honeypot_is_out_of_the_tab_order_and_the_accessibility_tree()
    {
        await Page.OpenAsync(site.At("/contact"));

        await Expect(Page.GetByLabel("Leave this empty")).ToBeHiddenAsync();
        var tree = await Page.Locator("form.contact-form").AriaSnapshotAsync();
        await Assert.That(tree).DoesNotContain("Leave this empty");

        await Page.GetByLabel("Message", new() { Exact = true }).FocusAsync();
        var stops = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            stops.Add(await Page.EvaluateAsync<string>("() => document.activeElement?.id || document.activeElement?.textContent?.trim() || ''"));
        }

        await Assert.That(stops).DoesNotContain("contact-website");
        await Assert.That(stops).Contains("Send");
    }

    [Test]
    public async Task An_error_keeps_the_text_takes_the_focus_and_passes_axe()
    {
        await Page.OpenAsync(site.At("/contact"));

        await Page.GetByLabel("Your name").FillAsync("Markup Sender");
        await Page.GetByLabel("Your email address").FillAsync("markup@example.org");
        await Page.GetByLabel("Message", new() { Exact = true }).FillAsync("<b>Bold</b> words.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        var alert = Page.GetByRole(AriaRole.Alert);
        await Expect(alert).ToContainTextAsync("Write the message as plain text");
        await Expect(alert).ToBeFocusedAsync();
        await Expect(Page.GetByLabel("Message", new() { Exact = true })).ToHaveValueAsync("<b>Bold</b> words.");
        await Expect(Page.GetByLabel("Message", new() { Exact = true })).ToHaveAttributeAsync("aria-invalid", "true");
        await alert.GetByRole(AriaRole.Link, new() { Name = "Go to the field" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("#contact-message$"));

        var result = await Page.RunAxe(WcagAa);
        await Assert.That(result.Violations.Select(v => v.Id)).IsEmpty();
        await Assert.That(site.Messages.Where(m => m.Name == "Markup Sender")).IsEmpty();
    }
}

/// <summary>The sample site with the contact form's minimum time off, so a browser may send at once.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class ContactSite : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new(new Dictionary<string, string> { ["Visitor:MinimumSeconds"] = "0" });

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Gets the messages kept so far.</summary>
    public IReadOnlyList<ContactMessage> Messages => _factory.Services.GetRequiredService<MemoryMessageStore>().All;

    /// <inheritdoc />
    public async Task InitializeAsync() => BaseAddress = await _factory.StartAsync();

    /// <summary>Gets the absolute address of a path on the site.</summary>
    /// <param name="path">A path such as <c>/contact</c>.</param>
    /// <returns>The address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
