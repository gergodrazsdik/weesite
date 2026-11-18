using System.Globalization;
using System.Net;
using MaxMind.Db;

namespace Weesite.Web.Analytics;

/// <summary>
/// Finds the country of a visitor's address (T-0143), with an IP to country database in the
/// MaxMind DB format (DB-IP's "IP to Country Lite", CC BY 4.0). <see cref="CountryDatabaseLoader"/>
/// loads it in the background and swaps in a newer one as a whole; until then, and when there's
/// none, no country is found. Only the ISO 3166 code leaves this class: the address is looked up
/// in memory and never kept.
/// </summary>
internal sealed partial class CountryLookup(ILogger<CountryLookup> logger)
{
    // The record's field with the code: { "country": { "iso_code": "DE" } }.
    private const string IsoCodeField = "iso_code";

    // The database in use and its version (a blob's ETag, or a file's size and time), swapped as a
    // whole. An old reader isn't disposed: a lookup on another thread may still be using it, and a
    // reader that holds its database in memory has nothing else to release.
    private volatile Database? _current;

    /// <summary>Gets the version of the database in use, or null when none is loaded.</summary>
    public string? Version => _current?.Version;

    /// <summary>
    /// Starts using a database from a stream, read whole into memory (the container's file system
    /// is read-only, so nothing is mapped from disk). The one in use stays if it isn't valid.
    /// </summary>
    /// <param name="stream">The database, from its start.</param>
    /// <param name="version">Its version, to tell whether a newer one has come.</param>
    /// <returns>The database's metadata.</returns>
    /// <exception cref="InvalidDataException">The file isn't an IP to country database.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The reader is kept in use; a replaced one is left to the garbage collector (see _current).")]
    public Metadata Load(Stream stream, string version)
    {
        var reader = Open(stream);
        _current = new Database(reader, version);
        return reader.Metadata;
    }

    private static Reader Open(Stream stream)
    {
        Reader reader;
        try
        {
            reader = new Reader(stream);
        }
        catch (Exception e) when (e is InvalidDatabaseException or ArgumentException or IOException)
        {
            throw new InvalidDataException("Not a MaxMind DB file.", e);
        }

        if (!reader.Metadata.DatabaseType.Contains("country", StringComparison.OrdinalIgnoreCase))
        {
            var type = reader.Metadata.DatabaseType;
            reader.Dispose();
            throw new InvalidDataException($"Not an IP to country database: {type}.");
        }

        return reader;
    }

    /// <summary>Finds the country of an address. Never throws: anything wrong finds no country.</summary>
    /// <param name="address">The client's address, as <see cref="ClientAddress.Of"/> gives it.</param>
    /// <returns>The ISO 3166 alpha-2 code in capitals, such as <c>DE</c>, or null.</returns>
    public string? Find(string address)
    {
        if (_current is not { } current || Parse(address) is not { } ip)
        {
            return null;
        }

        try
        {
            var record = current.Reader.Find<Dictionary<string, object>>(ip);
            return record is not null && record.TryGetValue("country", out var country)
                && country is IReadOnlyDictionary<string, object> fields
                && fields.TryGetValue(IsoCodeField, out var code) && code is string iso
                ? Normalize(iso)
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogLookupFailed(logger, e);
            return null;
        }
    }

    /// <summary>Keeps a stored country code only if it's two ASCII letters, and not <c>ZZ</c> (unknown).</summary>
    /// <param name="code">The code.</param>
    /// <returns>The code in capitals, or null.</returns>
    public static string? Normalize(string? code)
    {
        if (code is not { Length: 2 } || !char.IsAsciiLetter(code[0]) || !char.IsAsciiLetter(code[1]))
        {
            return null;
        }

        var upper = code.ToUpperInvariant();
        return upper == "ZZ" ? null : upper;
    }

    /// <summary>A country's English name (the admin is in English), or null when it isn't one .NET knows.</summary>
    /// <param name="code">An ISO 3166 alpha-2 code, such as <c>DE</c>.</param>
    /// <returns>Such as <c>Germany</c>, or null.</returns>
    public static string? EnglishName(string? code)
    {
        if (Normalize(code) is not { } known)
        {
            return null;
        }

        try
        {
            var region = new RegionInfo(known);

            // RegionInfo also takes culture names; only a region whose own code is this one counts.
            return string.Equals(region.TwoLetterISORegionName, known, StringComparison.Ordinal) ? region.EnglishName : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // The address without a port; an IPv4 address mapped into IPv6 (a dual-stack socket) as IPv4.
    private static IPAddress? Parse(string address)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            if (!IPEndPoint.TryParse(address, out var endPoint))
            {
                return null;
            }

            ip = endPoint.Address;
        }

        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Visitor countries: a lookup failed")]
    private static partial void LogLookupFailed(ILogger logger, Exception exception);

    private sealed record Database(Reader Reader, string Version);
}
