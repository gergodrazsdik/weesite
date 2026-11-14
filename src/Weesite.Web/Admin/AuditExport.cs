using System.Globalization;
using System.Text;

namespace Weesite.Web.Admin;

/// <summary>
/// What the audit log page shows and exports (T-0122): the latest actions, or a whole month, with
/// a filter on who, what or the target; and the export of the same as CSV.
/// </summary>
internal static class AuditExport
{
    /// <summary>The most rows one month's view or one export reads.</summary>
    public const int MaxRows = 100_000;

    /// <summary>How many months an export without a month covers, this one included.</summary>
    public const int ExportMonths = 12;

    /// <summary>Reads a month from the query: <c>yyyy-MM</c>, as a month field sends it.</summary>
    /// <param name="value">The query value.</param>
    /// <param name="start">The month's first moment, UTC.</param>
    /// <returns>Whether it was a month.</returns>
    public static bool TryMonth(string? value, out DateTimeOffset start)
    {
        // From 2000 to 9998: the month after the last one must exist too (T-0122: 9999-12 failed).
        if (DateTime.TryParseExact(value?.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) && month.Year is >= 2000 and <= 9998)
        {
            start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return true;
        }

        start = default;
        return false;
    }

    /// <summary>Gets whether an entry matches the filter: text in who, what or the target.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="filter">The filter; empty matches everything.</param>
    /// <returns>Whether it matches.</returns>
    public static bool Matches(AuditEntry entry, string? filter)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return filter?.Trim() is not { Length: > 0 } q
            || entry.Actor.Contains(q, StringComparison.OrdinalIgnoreCase)
            || entry.Action.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (entry.Target?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>Gets an entry as a CSV line (RFC 4180), with a line break.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The line.</returns>
    public static string Line(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Join(',', Cell(entry.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), Cell(entry.Actor), Cell(entry.Action), Cell(entry.Target ?? "")) + "\r\n";
    }

    /// <summary>Gets a CSV cell: quoted, and with a leading quote mark when a spreadsheet would read it as a formula.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The cell.</returns>
    public static string Cell(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Targets hold titles and names editors typed: "=…" mustn't run in a spreadsheet (CWE-1236),
        // nor " =…", which some spreadsheets read as a formula once they trim the spaces.
        if (value.TrimStart(' ').Length > 0 && value.TrimStart(' ')[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Maps <c>GET /admin/audit/export.csv?month=yyyy-MM&amp;q=…</c> (admins): the month's actions,
    /// or the last <see cref="ExportMonths"/> months', filtered as on the page, newest first. Streamed;
    /// the export itself is recorded.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapAuditExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/audit/export.csv", async (string? month, string? q, HttpContext context, IAuditLog log, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var (from, to, name) = TryMonth(month, out var start)
                ? (start, start.AddMonths(1), "audit-" + start.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".csv")
                : (new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1 - ExportMonths), now.AddMinutes(1), "audit.csv");
            var cancel = context.RequestAborted;
            await log.WriteAsync(new AuditEntry(now, EditorClaims.Email(context.User), "audit-exported", name), cancel);

            context.Response.ContentType = "text/csv; charset=utf-8";
            context.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
            await using var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            await writer.WriteAsync("\"When (UTC)\",\"Who\",\"What\",\"Target\"\r\n".AsMemory(), cancel);
            // At most MaxRows read, matching or not, as on the page; a cut-off export says so in its
            // last line rather than looking complete (T-0122).
            var read = 0;
            await foreach (var entry in log.ReadAsync(from, to, cancel))
            {
                if (++read > MaxRows)
                {
                    await writer.WriteAsync(Line(new AuditEntry(entry.At, "", "(export stopped)", $"Only the newest {MaxRows:N0} actions were read: pick a month.")).AsMemory(), cancel);
                    break;
                }

                if (Matches(entry, q))
                {
                    await writer.WriteAsync(Line(entry).AsMemory(), cancel);
                }
            }
        }).RequireAuthorization(EditorClaims.AdminPolicy);
}
