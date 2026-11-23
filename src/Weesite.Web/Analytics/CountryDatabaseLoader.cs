using System.Globalization;
using Azure;
using Azure.Storage.Blobs;

namespace Weesite.Web.Analytics;

/// <summary>
/// Loads the IP to country database for <see cref="CountryLookup"/> in the background after
/// startup (T-0143), so it never holds up readiness, and picks up a newer one: once a day, or
/// every hour while there's none. It reads <c>Analytics:CountryDatabase</c> (a file: tests,
/// local runs) or else the <c>country.mmdb</c> blob in the <c>geoip</c> container, which the
/// geoip workflow uploads each month. It only reads: every replica loads the same blob.
/// </summary>
internal sealed partial class CountryDatabaseLoader(
    CountryLookup lookup, IConfiguration configuration, IServiceProvider services, TimeProvider time, ILogger<CountryDatabaseLoader> logger)
    : BackgroundService
{
    /// <summary>The container the geoip workflow uploads to.</summary>
    public const string Container = "geoip";

    /// <summary>The blob the database is uploaded as.</summary>
    public const string BlobName = "country.mmdb";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckEveryWhileMissing = TimeSpan.FromHours(1);
    private bool _reportedMissing;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Analytics:Enabled", true))
        {
            return;
        }

        // Out of the startup path: hosted services start one after another.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken);
                await Task.Delay(lookup.Version is null ? CheckEveryWhileMissing : CheckEvery, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Loads the database if there's a newer one than the one in use. Never throws, except when cancelled.</summary>
    /// <param name="cancellationToken">Stops the load.</param>
    /// <returns>A task.</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (configuration["Analytics:CountryDatabase"] is { Length: > 0 } path)
            {
                LoadFile(path);
            }
            else if (services.GetService<BlobServiceClient>() is { } blobs)
            {
                await LoadBlobAsync(blobs.GetBlobContainerClient(Container).GetBlobClient(BlobName), cancellationToken);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogLoadFailed(logger, e);
        }
    }

    private void LoadFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            Missing();
            return;
        }

        var version = string.Create(CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");
        if (version == lookup.Version)
        {
            return;
        }

        using var stream = file.OpenRead();
        Loaded(lookup.Load(stream, version), null);
    }

    private async Task LoadBlobAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        string version;
        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            version = properties.Value.ETag.ToString();
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            Missing();
            return;
        }

        if (version == lookup.Version)
        {
            return;
        }

        // Whatever is there now, under its own version: a database uploaded since the check above
        // is simply the one loaded, rather than a refused download and a day's wait (F-0226).
        var download = await blob.DownloadContentAsync(cancellationToken);
        using var stream = download.Value.Content.ToStream();
        var month = download.Value.Details.Metadata.TryGetValue("month", out var m) ? m : null;
        Loaded(lookup.Load(stream, download.Value.Details.ETag.ToString()), month);
    }

    private void Loaded(MaxMind.Db.Metadata metadata, string? month)
    {
        _reportedMissing = false;
        LogLoaded(logger, metadata.DatabaseType, month, metadata.BuildDate);
    }

    // Said once until a database comes: a site without the geoip workflow has none.
    private void Missing()
    {
        if (!_reportedMissing)
        {
            LogMissing(logger);
            _reportedMissing = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Visitor countries: loaded {DatabaseType}, month {Month}, built {BuildDate}")]
    private static partial void LogLoaded(ILogger logger, string databaseType, string? month, DateTime buildDate);

    [LoggerMessage(Level = LogLevel.Information, Message = "Visitor countries: there's no country database yet, so no countries are recorded")]
    private static partial void LogMissing(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Visitor countries: the country database couldn't be loaded; the one in use, if any, stays")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);
}
