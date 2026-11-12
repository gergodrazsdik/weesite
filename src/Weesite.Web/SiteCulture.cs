using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Weesite.Web;

/// <summary>
/// The culture of the site's language (ADR 0008): every request runs in it, so month and day names,
/// the lists' short dates and number formats follow <c>site.json</c>'s <c>language</c>. Resource
/// files fall back to English for a language without its own.
/// </summary>
internal static class SiteCulture
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly ConcurrentDictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> MonthDayPatterns = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> LongestMonthDays = new(StringComparer.Ordinal);

    /// <summary>Gets the culture for a language code: a specific one (<c>hu</c> gives hu-HU); English for an unknown code.</summary>
    /// <param name="language">A code such as <c>en</c>, <c>en-GB</c> or <c>hu</c>.</param>
    /// <returns>The culture.</returns>
    public static CultureInfo For(string? language) =>
        string.IsNullOrWhiteSpace(language) ? English : Cultures.GetOrAdd(language.Trim(), Resolve);

    /// <summary>Gets the Open Graph locale of a culture: language and territory, such as <c>hu_HU</c>.</summary>
    /// <param name="culture">The culture.</param>
    /// <returns>The locale.</returns>
    public static string OpenGraphLocale(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.Name.Replace('-', '_');
    }

    /// <summary>Gets <c>rtl</c> for a right-to-left language, null otherwise (the default, left to right).</summary>
    /// <param name="culture">The culture.</param>
    /// <returns>The direction, or null.</returns>
    public static string? Direction(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.IsRightToLeft ? "rtl" : null;
    }

    /// <summary>
    /// Gets the short month-and-day format of a culture, for dates under a year's heading (ADR
    /// 0006): its month-day pattern with the month abbreviated and the day in two digits, so the
    /// dates line up. "MMM dd" in English ("Oct 12"), "MMM dd." in Hungarian ("okt. 12.").
    /// </summary>
    /// <param name="culture">The culture.</param>
    /// <returns>The format.</returns>
    public static string MonthDayPattern(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return MonthDayPatterns.GetOrAdd(culture.Name, _ => Shorten(culture.DateTimeFormat.MonthDayPattern));
    }

    /// <summary>Gets the length of the culture's longest short date, over every month, for the lists' date column.</summary>
    /// <param name="culture">The culture.</param>
    /// <returns>The number of characters, such as 6 for "Sep 28" or 10 for "szept. 28.".</returns>
    public static int LongestMonthDay(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return LongestMonthDays.GetOrAdd(culture.Name, _ =>
        {
            var pattern = MonthDayPattern(culture);
            return Enumerable.Range(1, 12).Max(month => new DateOnly(2026, month, 28).ToString(pattern, culture).Length);
        });
    }

    /// <summary>Runs every request in the site language's culture.</summary>
    /// <param name="app">The app.</param>
    public static void UseSiteCulture(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var culture = For(context.RequestServices.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Site.Language);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return next(context);
        });

    private static CultureInfo Resolve(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language, predefinedOnly: true);
            if (culture.Equals(CultureInfo.InvariantCulture))
            {
                return English;
            }

            return culture.IsNeutralCulture ? CultureInfo.GetCultureInfo(CultureInfo.CreateSpecificCulture(culture.Name).Name) : culture;
        }
        catch (CultureNotFoundException)
        {
            return English;
        }
    }

    // "MMMM" becomes "MMM" and a lone "d" becomes "dd"; quoted literals such as 'de' stay as they are.
    private static string Shorten(string pattern)
    {
        var result = new StringBuilder(pattern.Length + 2);
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c is '\'' or '"')
            {
                var end = pattern.IndexOf(c, i + 1, StringComparison.Ordinal);
                end = end < 0 ? pattern.Length - 1 : end;
                result.Append(pattern, i, end - i + 1);
                i = end + 1;
                continue;
            }

            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c)
            {
                run++;
            }

            result.Append(c switch
            {
                'M' when run >= 4 => "MMM",
                'd' when run == 1 => "dd",
                _ => new string(c, run),
            });
            i += run;
        }

        return result.ToString();
    }
}
