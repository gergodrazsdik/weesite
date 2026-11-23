using Azure.Data.Tables;

namespace Weesite.Web.Analytics;

/// <summary>Wires up page-view analytics (brief, Analytics).</summary>
internal static partial class AnalyticsExtensions
{
    /// <summary>
    /// Adds the recorder and the writer, with the table store when content comes from Blob
    /// Storage (the <see cref="TableServiceClient"/> is registered then) and the memory store
    /// otherwise, and the visitor-country lookup with its loader. <c>Analytics:Enabled</c> set to
    /// false turns counting off.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep page views in memory (content from files).</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddPageViews(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<MemoryPageViewStore>();
            services.AddSingleton<IPageViewStore>(s => s.GetRequiredService<MemoryPageViewStore>());
        }
        else
        {
            services.AddSingleton<IPageViewStore, TablePageViewStore>();
        }

        services.AddSingleton<AnalyticsData>();
        services.AddSingleton<PageViewRecorder>();
        services.AddHostedService<PageViewWriter>();

        // Visitor countries (T-0143): the database loads in the background, after startup.
        services.AddSingleton<CountryLookup>();
        services.AddSingleton<CountryDatabaseLoader>();
        services.AddHostedService(s => s.GetRequiredService<CountryDatabaseLoader>());
        return services;
    }

    /// <summary>Counts each finished request that is a page view.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IApplicationBuilder UsePageViews(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetValue("Analytics:Enabled", true))
        {
            return app;
        }

        var recorder = app.ApplicationServices.GetRequiredService<PageViewRecorder>();
        var logger = app.ApplicationServices.GetRequiredService<ILogger<PageViewRecorder>>();
        return app.Use(async (context, next) =>
        {
            await next(context);
            if (!PageViewRecorder.Counts(context))
            {
                return;
            }

            try
            {
                await recorder.RecordAsync(context);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogRecordFailed(logger, e);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record a page view")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
