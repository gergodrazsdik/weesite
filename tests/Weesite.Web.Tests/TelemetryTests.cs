using System.Net;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Weesite.Web.Tests;

/// <summary>Telemetry to Application Insights (T-0054).</summary>
internal sealed class TelemetryTests
{
    // A connection string whose ingestion endpoint refuses connections: nothing leaves the machine.
    private const string ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://127.0.0.1:9/";

    [Test]
    public async Task A_connection_string_turns_on_the_exporter_with_a_quarter_of_the_traces()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("APPLICATIONINSIGHTS_CONNECTION_STRING", ConnectionString));
        using var client = factory.CreateClient();

        using var home = await client.GetAsync(new Uri("/", UriKind.Relative));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<AzureMonitorOptions>>().Get(Options.DefaultName);

        await Assert.That(home.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(options.SamplingRatio).IsEqualTo(0.25f);
    }
}
