using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Weesite.ServiceDefaults;

/// <summary>
/// Defaults shared by the web app and the re-index job: OpenTelemetry logs, traces and metrics,
/// health checks, service discovery and HTTP resilience. Locally the telemetry goes to the Aspire
/// dashboard; in Azure to Application Insights, through the OTLP endpoint in configuration.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Path of the health check that also covers dependencies (readiness).</summary>
    public const string HealthPath = "/health";

    /// <summary>Path of the liveness check: the process answers.</summary>
    public const string AlivePath = "/alive";

    private const string LiveTag = "live";

    /// <summary>Adds telemetry, health checks, service discovery and resilience to an app.</summary>
    /// <typeparam name="TBuilder">The host application builder.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>Configures OpenTelemetry logging, metrics and tracing, exported over OTLP when configured.</summary>
    /// <typeparam name="TBuilder">The host application builder.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(options =>
                    // Health probes would drown out real requests.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthPath, StringComparison.Ordinal)
                        && !context.Request.Path.StartsWithSegments(AlivePath, StringComparison.Ordinal))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Application Insights in Azure (brief, Monitoring; T-0054): infra/app sets the connection
        // string. A share of the requests and their traces (Telemetry:SamplingRatio, a quarter by
        // default), all metrics, and the logs at the configured levels; the workspace's daily cap
        // bounds the cost.
        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            var samplingRatio = builder.Configuration.GetValue("Telemetry:SamplingRatio", 0.25f);
            builder.Services.AddOpenTelemetry().UseAzureMonitor(options => options.SamplingRatio = samplingRatio);
        }

        return builder;
    }

    /// <summary>Adds a liveness check; readiness checks for dependencies are added by the app.</summary>
    /// <typeparam name="TBuilder">The host application builder.</typeparam>
    /// <param name="builder">The builder to configure.</param>
    /// <returns>The same builder.</returns>
    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag]);

        return builder;
    }

    /// <summary>
    /// Maps <see cref="HealthPath"/> (every check) and <see cref="AlivePath"/> (liveness only).
    /// They report status only, no details, so they are safe to expose to the platform's probes.
    /// </summary>
    /// <param name="app">The app to map the endpoints on.</param>
    /// <returns>The same app.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks(HealthPath);
        app.MapHealthChecks(AlivePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
        });

        return app;
    }
}
