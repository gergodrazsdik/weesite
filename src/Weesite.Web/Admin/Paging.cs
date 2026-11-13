using System.Globalization;

namespace Weesite.Web.Admin;

/// <summary>Pages of the admin's long lists (T-0131): posts, pages, media, tags and the audit log.</summary>
internal static class Paging
{
    /// <summary>Gets how many pages a list takes.</summary>
    /// <param name="total">How many items it has.</param>
    /// <param name="pageSize">How many a page shows.</param>
    /// <returns>At least 1.</returns>
    public static int PageCount(int total, int pageSize) => Math.Max(1, (total + pageSize - 1) / pageSize);

    /// <summary>
    /// Gets the page asked for, from the query's text: 1 for anything that isn't a page number
    /// (as F-0080 reads query values), the last one past the end.
    /// </summary>
    /// <param name="page">The query value.</param>
    /// <param name="total">How many items the list has.</param>
    /// <param name="pageSize">How many a page shows.</param>
    /// <returns>A page from 1 to the last.</returns>
    public static int Current(string? page, int total, int pageSize) =>
        int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 1
            ? Math.Min(number, PageCount(total, pageSize))
            : 1;

    /// <summary>Gets one page of a list.</summary>
    /// <typeparam name="T">The items.</typeparam>
    /// <param name="items">The whole list, in order.</param>
    /// <param name="current">The page, from 1.</param>
    /// <param name="pageSize">How many a page shows.</param>
    /// <returns>The page's items.</returns>
    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int current, int pageSize) =>
        [.. items.Skip((current - 1) * pageSize).Take(pageSize)];

    /// <summary>Adds the page to a list's address, leaving page 1 out.</summary>
    /// <param name="url">The list's address with its filter and tab.</param>
    /// <param name="page">The page.</param>
    /// <returns>The address of that page.</returns>
    public static string With(string url, int page) =>
        page <= 1 ? url : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "page=" + page.ToString(CultureInfo.InvariantCulture);
}
