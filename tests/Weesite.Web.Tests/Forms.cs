using System.Net;
using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>Reading the admin's forms in rendered pages, shared by the tests that post them (F-0118).</summary>
internal static partial class Forms
{
    /// <summary>Finds the antiforgery token a page's form carries: group 1 is its value.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    public static partial Regex Token();

    /// <summary>
    /// Reads what a browser would post from one of a page's forms, as rendered: its inputs, the
    /// checked boxes and radios, the selected options and the text areas (F-0175).
    /// </summary>
    /// <param name="page">The page's HTML.</param>
    /// <param name="handler">The form's name (Blazor's <c>_handler</c>).</param>
    /// <param name="button">The value of the submit button pressed, posted with its name when it has one, as a browser does.</param>
    /// <returns>The fields in order, names repeated where the form repeats them.</returns>
    public static List<KeyValuePair<string, string>> Values(string page, string handler, string? button = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var marker = page.IndexOf($"name=\"_handler\" value=\"{handler}\"", StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new InvalidOperationException($"No form {handler} on the page.");
        }

        var start = page.LastIndexOf("<form", marker, StringComparison.Ordinal);
        var form = page[start..page.IndexOf("</form>", marker, StringComparison.Ordinal)];
        var fields = new List<(int At, string Name, string Value)>();
        foreach (Match input in Input().Matches(form))
        {
            var tag = input.Value;
            var type = Attribute(tag, "type") ?? "text";
            if (Attribute(tag, "name") is not { } name || Flag(tag, "disabled") || type is "submit" or "button" or "file" or "image"
                || (type is "checkbox" or "radio" && !Flag(tag, "checked")))
            {
                continue;
            }

            fields.Add((input.Index, name, Attribute(tag, "value") ?? (type == "checkbox" ? "on" : "")));
        }

        foreach (Match area in TextArea().Matches(form))
        {
            if (Attribute(area.Groups[1].Value, "name") is { } name && !Flag(area.Groups[1].Value, "disabled"))
            {
                fields.Add((area.Index, name, WebUtility.HtmlDecode(area.Groups[2].Value).TrimStart('\n')));
            }
        }

        foreach (Match select in Select().Matches(form))
        {
            if (Attribute(select.Groups[1].Value, "name") is not { } name || Flag(select.Groups[1].Value, "disabled"))
            {
                continue;
            }

            // Blazor renders the value on the select itself; plain HTML marks the option.
            var options = Option().Matches(select.Groups[2].Value).ToList();
            var chosen = options.FirstOrDefault(o => Flag(o.Groups[1].Value, "selected")) ?? options.FirstOrDefault();
            if (Attribute(select.Groups[1].Value, "value") is { } value)
            {
                fields.Add((select.Index, name, value));
            }
            else if (chosen is not null)
            {
                fields.Add((select.Index, name, Attribute(chosen.Groups[1].Value, "value") ?? WebUtility.HtmlDecode(chosen.Groups[2].Value)));
            }
        }

        foreach (Match submit in Button().Matches(form))
        {
            if (button is not null && Attribute(submit.Value, "name") is { } name && Attribute(submit.Value, "value") == button)
            {
                fields.Add((submit.Index, name, button));
                break;
            }
        }

        return [.. fields.OrderBy(f => f.At).Select(f => new KeyValuePair<string, string>(f.Name, f.Value))];
    }

    // Blazor writes an empty value as the bare attribute: value, not value="".
    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"\\s{name}(?:=\"([^\"]*)\")?(?=[\\s/>]|$)", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } match
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : null;

    private static bool Flag(string tag, string name) =>
        Regex.IsMatch(tag, $"\\s{name}(=\"[^\"]*\")?(?=[\\s/>]|$)", RegexOptions.None, TimeSpan.FromSeconds(1));

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("<button\\b[^>]*>")]
    private static partial Regex Button();

    [GeneratedRegex("<textarea\\b([^>]*)>(.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex TextArea();

    [GeneratedRegex("<select\\b([^>]*)>(.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex("<option\\b([^>]*)>(.*?)</option>", RegexOptions.Singleline)]
    private static partial Regex Option();
}
