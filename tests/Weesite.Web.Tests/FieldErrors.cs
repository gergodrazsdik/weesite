using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>Checks that a form's error marks its field and links to it (F-0081).</summary>
internal static class FieldErrors
{
    /// <summary>Asserts that exactly the field with <paramref name="id"/> is marked invalid and linked; with no id, that none is.</summary>
    /// <param name="html">The page.</param>
    /// <param name="id">The field's id, or null when the error isn't about one field.</param>
    /// <returns>A task.</returns>
    public static async Task AssertMarkedAsync(string html, string? id)
    {
        await Assert.That(Regex.Count(html, "aria-invalid=\"true\"")).IsEqualTo(id is null ? 0 : 1);
        if (id is null)
        {
            await Assert.That(html).DoesNotContain("Go to the field");
            return;
        }

        var escaped = Regex.Escape(id);
        await Assert.That(html).Matches($"<(input|select|textarea)(?=[^>]*\\bid=\"{escaped}\")(?=[^>]*\\baria-invalid=\"true\")[^>]*>");
        await Assert.That(html).Contains($"<a href=\"#{id}\">Go to the field</a>");
    }
}
