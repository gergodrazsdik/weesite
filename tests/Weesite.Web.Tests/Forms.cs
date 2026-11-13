using System.Text.RegularExpressions;

namespace Weesite.Web.Tests;

/// <summary>Reading the admin's forms in rendered pages, shared by the tests that post them (F-0118).</summary>
internal static partial class Forms
{
    /// <summary>Finds the antiforgery token a page's form carries: group 1 is its value.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    public static partial Regex Token();
}
