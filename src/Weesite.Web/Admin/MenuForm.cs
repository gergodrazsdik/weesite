using System.Globalization;
using System.Text.Json.Nodes;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// The menu page's form (T-0049): one row per item, each a label and a target (the blog, a page
/// or an address), ordered by a position number that <c>ws-menu-editor</c> fills in from its
/// move buttons.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class MenuForm
{
    /// <summary>The most items a menu has.</summary>
    public const int MaxItems = 8;

    /// <summary>The empty rows shown after the items, for new ones.</summary>
    public const int SpareRows = 3;

    /// <summary>The target value for the blog.</summary>
    public const string BlogTarget = "/blog";

    /// <summary>The prefix of a target that's a page.</summary>
    public const string PagePrefix = "page:";

    /// <summary>Gets or sets the version of <c>site.json</c> opened.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets the rows.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
    public List<MenuRow> Rows { get; set; } = [];

    /// <summary>Gets a form filled from the settings.</summary>
    /// <param name="opened">The settings and their version.</param>
    /// <returns>The form, with spare rows.</returns>
    public static MenuForm From(OpenedSettings opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var form = new MenuForm
        {
            ETag = opened.ETag,
            Rows = [.. opened.Settings.Menu.Select(m => m.Page is { } page
                ? new MenuRow { Label = m.Label, Target = PagePrefix + page }
                : m.Href == BlogTarget ? new MenuRow { Label = m.Label, Target = BlogTarget } : new MenuRow { Label = m.Label, Href = m.Href })],
        };
        form.Pad();
        return form;
    }

    /// <summary>Adds the spare rows and numbers the positions in order.</summary>
    public void Pad()
    {
        Rows ??= [];
        Rows = [.. Rows.Where(r => !r.IsEmpty)];
        for (var i = 0; i < SpareRows && Rows.Count < MaxItems; i++)
        {
            Rows.Add(new MenuRow());
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Position = (i + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Checks the form and returns the change to apply to <c>site.json</c>.</summary>
    /// <param name="pages">The slugs of the published pages.</param>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The change, or null with <paramref name="error"/> set.</returns>
    public Action<JsonObject>? ToChange(IReadOnlyCollection<string> pages, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pages);
        error = null;
        var items = new List<(int Position, int Index, MenuItem Item)>();
        var rows = (Rows ?? []).Where(r => !r.IsEmpty).ToList();
        if (rows.Count > MaxItems)
        {
            error = $"A menu can have at most {MaxItems} items.";
        }

        for (var i = 0; i < rows.Count && error is null; i++)
        {
            var row = rows[i];
            var label = (row.Label ?? "").Trim();
            var target = (row.Target ?? "").Trim();
            var href = (row.Href ?? "").Trim();
            MenuItem? item = null;
            if (label.Length is 0 or > 40)
            {
                error = "Each menu item needs a label of at most 40 characters.";
            }
            else if (target.StartsWith(PagePrefix, StringComparison.Ordinal))
            {
                var page = target[PagePrefix.Length..];
                item = pages.Contains(page) ? new MenuItem { Label = label, Page = page } : null;
                error = item is null ? $"'{label}' points to a page that isn't published." : null;
            }
            else if (target == BlogTarget)
            {
                item = new MenuItem { Label = label, Href = BlogTarget };
            }
            else if (IsAddress(href))
            {
                item = new MenuItem { Label = label, Href = href };
            }
            else
            {
                error = $"'{label}' needs a page, the blog, or an address such as /tags or https://example.org/.";
            }

            if (item is not null)
            {
                var position = int.TryParse(row.Position, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue;
                items.Add((position, i, item));
            }
        }

        if (error is not null)
        {
            return null;
        }

        var menu = items.OrderBy(i => i.Position).ThenBy(i => i.Index).Select(i => i.Item).ToList();
        return json =>
        {
            if (menu.Count == 0)
            {
                json.Remove("menu");
            }
            else
            {
                json["menu"] = SiteSettingsEditor.ToNode(menu);
            }
        };
    }

    /// <summary>Gets the change that drops a page's items from the menu (F-0076), if it has any.</summary>
    /// <param name="menu">The menu.</param>
    /// <param name="page">The page's slug.</param>
    /// <returns>The change, or null when no item points to the page.</returns>
    public static Action<JsonObject>? WithoutPage(IReadOnlyList<MenuItem> menu, string page)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (!menu.Any(m => m.Page == page))
        {
            return null;
        }

        var kept = menu.Where(m => m.Page != page).ToList();
        return json =>
        {
            if (kept.Count == 0)
            {
                json.Remove("menu");
            }
            else
            {
                json["menu"] = SiteSettingsEditor.ToNode(kept);
            }
        };
    }

    // A path on the site ("/tags", not "//host") or a web address.
    private static bool IsAddress(string href) =>
        href.Length is > 0 and <= 300
        && ((href[0] == '/' && !href.StartsWith("//", StringComparison.Ordinal))
            || (Uri.TryCreate(href, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)));
}

/// <summary>One row of the menu form.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class MenuRow
{
    /// <summary>Gets or sets the label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the target: <c>/blog</c>, <c>page:</c> and a slug, or empty for <see cref="Href"/>.</summary>
    public string? Target { get; set; }

    /// <summary>Gets or sets the address, when the target is empty.</summary>
    public string? Href { get; set; }

    /// <summary>Gets or sets the position, 1 first.</summary>
    public string? Position { get; set; }

    /// <summary>Gets whether the row is unused.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(Target) && string.IsNullOrWhiteSpace(Href);
}
